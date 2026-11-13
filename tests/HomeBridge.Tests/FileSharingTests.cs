using System.Security.Authentication;
using HomeBridge.Core;
using Xunit;

[Collection("Network")]
public sealed class FileSharingTests
{
    [Fact]
    public async Task MutualTrustAndExplicitUploadPermissionAreRequired()
    {
        using var hostDir = new Scratch();
        using var clientDir = new Scratch();
        using var shared = new Scratch();
        var source = Path.Combine(clientDir.Path, "outgoing.txt");
        File.WriteAllText(source, "incoming");
        var original = Path.Combine(shared.Path, "original.txt");
        File.WriteAllText(original, "original");

        var hs = new StateStore(hostDir.Path);
        var cs = new StateStore(clientDir.Path);
        using var hi = Identity.Load(hs);
        using var ci = Identity.Load(cs);
        var share = new SharedLocation(Guid.NewGuid(), shared.Path, false);
        var hostSettings = new UserSettings { Shares = [share] };
        var hostPeer = new TrustedPeer(Identity.Fingerprint(hi), "Host", "127.0.0.1");
        var clientSettings = new UserSettings { Peers = [hostPeer] };
        await using var host = new NetworkAgent(hs, hostSettings, hi);
        await using var client = new NetworkAgent(cs, clientSettings, ci);
        host.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        await Assert.ThrowsAnyAsync<IOException>(() => client.Browse(hostPeer, default, "", ct));

        lock (hostSettings) hostSettings.Peers.Add(new(Identity.Fingerprint(ci), "Client", "127.0.0.1"));
        Assert.Single(await client.Browse(hostPeer, default, "", ct));
        Assert.Single(await client.Browse(hostPeer, share.Id, "", ct));
        var received = Path.Combine(clientDir.Path, "received.txt");
        await client.Download(hostPeer, share.Id, "original.txt", received, null, ct);
        Assert.Equal("original", File.ReadAllText(received));
        await Assert.ThrowsAnyAsync<IOException>(() => client.Download(hostPeer, share.Id, "../original.txt", received, null, ct));
        await Assert.ThrowsAnyAsync<IOException>(() => client.Upload(hostPeer, share.Id, "incoming.txt", source, null, ct));
        Assert.False(File.Exists(Path.Combine(shared.Path, "incoming.txt")));

        lock (hostSettings) hostSettings.Shares[0] = share with { Upload = true };
        await client.Upload(hostPeer, share.Id, "nested/incoming.txt", source, null, ct);
        Assert.Equal("incoming", File.ReadAllText(Path.Combine(shared.Path, "nested", "incoming.txt")));
        await Assert.ThrowsAnyAsync<IOException>(() => client.Upload(hostPeer, share.Id, "original.txt", source, null, ct));
        Assert.Equal("original", File.ReadAllText(original));

        lock (hostSettings) hostSettings.Peers.Clear();
        await Assert.ThrowsAnyAsync<IOException>(() => client.Browse(hostPeer, default, "", ct));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("C:alternate")]
    [InlineData("nested/file")]
    [InlineData("nested\\file")]
    [InlineData("NUL.txt")]
    [InlineData("COM¹.log")]
    [InlineData("alias.")]
    [InlineData(".homebridge-private.partial")]
    public void UnsafeWindowsNamesAreRejected(string name) =>
        Assert.Throws<InvalidDataException>(() => SharePaths.Child("C:\\Shared", name));

    [Fact]
    public async Task InterruptedOrDuplicateDownloadsNeverReplaceOriginalsOrLeavePartialFiles()
    {
        using var scratch = new Scratch();
        var target = Path.Combine(scratch.Path, "existing.txt");
        File.WriteAllText(target, "original");
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            SharePaths.ReceiveNew(new MemoryStream([1, 2]), target, 3, () => true, null, default));
        await Assert.ThrowsAnyAsync<IOException>(() =>
            SharePaths.ReceiveNew(new MemoryStream([1, 2]), target, 2, () => true, null, default));
        await Assert.ThrowsAsync<AuthenticationException>(() =>
            SharePaths.ReceiveNew(new MemoryStream([1, 2]), target, 2, () => false, null, default));
        Assert.Equal("original", File.ReadAllText(target));
        Assert.Single(Directory.GetFiles(scratch.Path));
    }

    [Fact]
    public void LegacySettingsRetainDisplayNameAndUpdatePreferences()
    {
        using var scratch = new Scratch();
        File.WriteAllText(Path.Combine(scratch.Path, "settings.json"),
            """{"DisplayName":"Kitchen PC","AutomaticUpdateChecks":false,"IncludePreviewUpdates":false}""");
        var settings = new StateStore(scratch.Path).Load();
        Assert.Equal("Kitchen PC", settings.DeviceName);
        Assert.False(settings.AutomaticUpdateChecks);
        Assert.False(settings.IncludePreviewUpdates);
        Assert.Empty(settings.Shares);
    }
}
