using System.Buffers.Binary;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using HomeBridge.Core;
using HomeBridge.Desktop;
using Xunit;

[CollectionDefinition("Network", DisableParallelization = true)]
public sealed class NetworkCollection;

public sealed class Scratch : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "HomeBridge-tests-" + Guid.NewGuid());
    public Scratch() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, true);
}

public sealed class SecurityTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    [Fact] public void InvitationsExpireAreSingleUseAndAttemptLimited()
    {
        var clock = new Clock(); var gate = new PairingGate(clock); var first = gate.Open();
        Assert.True(gate.Consume(first.Token)); Assert.False(gate.Consume(first.Token));
        var expired = gate.Open(); clock.Now += TimeSpan.FromMinutes(5); Assert.False(gate.Consume(expired.Token));
        var limited = gate.Open(); for (var i = 0; i < 5; i++) Assert.False(gate.Consume(new string('0', 64)));
        Assert.False(gate.Consume(limited.Token));
        var canceled = gate.Open(); gate.Close(); Assert.False(gate.Consume(canceled.Token));
        var replaced = gate.Open(); var current = gate.Open(); Assert.False(gate.Consume(replaced.Token)); Assert.True(gate.Consume(current.Token));
    }
    [Fact] public async Task InvitationCanOnlyBeConsumedOnceConcurrently()
    {
        var gate = new PairingGate(); var invitation = gate.Open();
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => gate.Consume(invitation.Token))));
        Assert.Single(results, x => x);
    }
    [Theory]
    [InlineData("8.8.8.8", false)] [InlineData("127.0.0.1", true)] [InlineData("192.168.1.2", true)]
    [InlineData("172.31.1.1", true)] [InlineData("172.32.1.1", false)] [InlineData("::1", true)]
    [InlineData("::ffff:8.8.8.8", false)]
    public void OnlyPrivateAddressesAreAllowed(string address, bool expected) => Assert.Equal(expected, LanAddress.IsLocal(IPAddress.Parse(address)));

    [Fact] public async Task OversizedAndTruncatedFramesAreRejected()
    {
        var header = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, Wire.MaximumBytes + 1);
        await Assert.ThrowsAsync<InvalidDataException>(() => Wire.Read<Message>(new MemoryStream(header), default));
        BinaryPrimitives.WriteInt32BigEndian(header, 40);
        await Assert.ThrowsAsync<EndOfStreamException>(() => Wire.Read<Message>(new MemoryStream(header), default));
    }
    [Fact] public void SettingsAndIdentitySurviveRestartAndCorruptionFailsClosed()
    {
        using var scratch = new Scratch(); var store = new StateStore(scratch.Path);
        var settings = new UserSettings(); settings.Applications.Add(new("Editor", @"C:\Tools\editor.exe"));
        settings.Peers.Add(new(new string('A', 64), "Other PC")); store.Save(settings);
        Assert.Equal(settings.Applications, new StateStore(scratch.Path).Load().Applications);
        Assert.Equal(settings.Peers, store.Load().Peers);
        using var first = Identity.Load(store); using var second = Identity.Load(store);
        Assert.Equal(Identity.Fingerprint(first), Identity.Fingerprint(second));
        Assert.False(Identity.Matches(first, new string('0', 64)));
        var protectedBytes = File.ReadAllBytes(System.IO.Path.Combine(scratch.Path, "identity.dpapi"));
        Assert.NotEqual(first.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx), protectedBytes);
        File.WriteAllBytes(System.IO.Path.Combine(scratch.Path, "identity.dpapi"), [1, 2, 3]);
        Assert.Throws<CryptographicException>(() => Identity.Load(store));
        File.WriteAllText(System.IO.Path.Combine(scratch.Path, "settings.json"), "{\"Version\":99}");
        Assert.Throws<InvalidDataException>(() => store.Load());
        Assert.Empty(Directory.GetFiles(scratch.Path, "*.tmp"));
    }
    [Fact] public void RenamePreservesExtensionAndUndoAndRejectsCollisions()
    {
        using var scratch = new Scratch(); var path = System.IO.Path.Combine(scratch.Path, "report.txt"); File.WriteAllText(path, "original");
        var plan = RenameEngine.Preview([path], new("report", "final", "", "-v2"));
        Assert.EndsWith("final-v2.txt", plan[0].Destination);
        var applied = RenameEngine.Apply(plan); Assert.Equal("original", File.ReadAllText(plan[0].Destination));
        RenameEngine.Undo(applied); Assert.True(File.Exists(path));
        File.WriteAllText(plan[0].Destination, "occupied"); Assert.Throws<IOException>(() => RenameEngine.Preview([path], new("report", "final", "", "-v2")));
        Assert.Equal("occupied", File.ReadAllText(plan[0].Destination));
    }
    [Fact] public void InitialPreviewDisplayNameMigratesWithoutResettingIt()
    {
        using var scratch = new Scratch(); var store = new StateStore(scratch.Path);
        File.WriteAllText(System.IO.Path.Combine(scratch.Path, "settings.json"), "{\"DisplayName\":\"My living room PC\"}");
        var settings = store.Load(); Assert.Equal("My living room PC", settings.DeviceName);
        store.Save(settings); Assert.Equal("My living room PC", store.Load().DeviceName);
    }
    [Fact] public void RenameRollsBackWhenLaterTargetAppears()
    {
        using var scratch = new Scratch();
        var a = System.IO.Path.Combine(scratch.Path, "a.txt"); var b = System.IO.Path.Combine(scratch.Path, "b.txt");
        File.WriteAllText(a, "a"); File.WriteAllText(b, "b");
        var plans = RenameEngine.Preview([a, b], new("", "", "new-", ""));
        File.WriteAllText(plans[1].Destination, "new occupant");
        Assert.Throws<IOException>(() => RenameEngine.Apply(plans)); Assert.True(File.Exists(a)); Assert.True(File.Exists(b));
    }
}

[Collection("Network")]
public sealed class NetworkTests
{
    [Fact] public async Task PairThenAuthenticateRevokeAndRejectWrongPinAndToken()
    {
        using var serverDir = new Scratch(); using var clientDir = new Scratch();
        var serverStore = new StateStore(serverDir.Path); var clientStore = new StateStore(clientDir.Path);
        var serverSettings = new UserSettings(); var clientSettings = new UserSettings();
        using var serverId = Identity.Load(serverStore); using var clientId = Identity.Load(clientStore);
        await using var server = new NetworkAgent(serverStore, serverSettings, serverId);
        await using var client = new NetworkAgent(clientStore, clientSettings, clientId);
        server.Start(); var invite = server.Invite("127.0.0.1");
        Assert.False((await RawRequest(serverId, clientId, new Message("ping", "Unknown PC"))).Success);
        var pinFailure = await Record.ExceptionAsync(() => client.Pair(invite with { Fingerprint = new string('0', 64) }));
        Assert.True(pinFailure is AuthenticationException or IOException, pinFailure?.ToString());
        await Assert.ThrowsAsync<AuthenticationException>(() => client.Pair(invite with { Token = new string('0', 64) }));
        Assert.Empty(serverSettings.Peers); Assert.Empty(clientSettings.Peers);
        await client.Pair(invite);
        Assert.Single(serverStore.Load().Peers); Assert.Single(clientStore.Load().Peers);
        await client.Ping("127.0.0.1", invite.Fingerprint);
        Assert.False((await RawRequest(serverId, clientId, new Message("launch", "Unknown command"))).Success);
        await Assert.ThrowsAsync<AuthenticationException>(() => client.Pair(invite));
        lock (serverSettings) { serverSettings.Peers.Clear(); serverStore.Save(serverSettings); }
        await Assert.ThrowsAsync<AuthenticationException>(() => client.Ping("127.0.0.1", invite.Fingerprint));
        var missingCertificate = await Record.ExceptionAsync(() => RawRequest(serverId, null, new Message("pair", "No certificate", invite.Token)));
        Assert.True(missingCertificate is AuthenticationException or IOException, missingCertificate?.ToString());
        await server.DisposeAsync(); await server.DisposeAsync();
        server.Start(); // Releasing sockets and restarting must retain revocation and identity.
        await Assert.ThrowsAsync<AuthenticationException>(() => client.Ping("127.0.0.1", invite.Fingerprint));
    }

    private static async Task<Reply> RawRequest(X509Certificate2 server, X509Certificate2? clientCertificate, Message message)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); var ct = timeout.Token;
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, NetworkAgent.Port, ct);
        using var tls = new SslStream(client.GetStream(), false);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {
            TargetHost = "HomeBridge", AllowTlsResume = false,
            ClientCertificates = clientCertificate is null ? new() : new() { clientCertificate },
            LocalCertificateSelectionCallback = (_, _, _, _, _) => clientCertificate!,
            RemoteCertificateValidationCallback = (_, cert, _, _) => Identity.Matches(cert, Identity.Fingerprint(server))
        }, ct);
        await Wire.Write(tls, message, ct); return await Wire.Read<Reply>(tls, ct);
    }

    [Fact] public async Task DiscoveryReturnsOnlyUntrustedPublicMetadataAndRejectsMalformedQueries()
    {
        var fingerprint = new string('A', 64);
        await using var responder = new DiscoveryResponder("Test PC", fingerprint);
        using var client = new UdpClient(AddressFamily.InterNetwork);
        var endpoint = new IPEndPoint(IPAddress.Loopback, NetworkAgent.Port);
        await client.SendAsync(Encoding.ASCII.GetBytes("HB1:bad"), endpoint);
        using (var shortTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await client.ReceiveAsync(shortTimeout.Token));
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        await client.SendAsync(Encoding.ASCII.GetBytes("HB1:" + nonce), endpoint);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var result = await client.ReceiveAsync(timeout.Token);
        var ad = JsonSerializer.Deserialize<Advertisement>(result.Buffer)!;
        Assert.Equal(nonce, ad.Nonce); Assert.Equal(fingerprint, ad.Fingerprint); Assert.Equal("Test PC", ad.Name);
        Assert.DoesNotContain("Token", Encoding.UTF8.GetString(result.Buffer));
    }
}
