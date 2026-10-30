using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;

namespace HomeBridge.Core;

// Runs with the interactive user's rights. This protocol has no file, shell, or input commands.
public sealed class NetworkAgent(StateStore store, UserSettings settings, X509Certificate2 identity) : IAsyncDisposable
{
    public const int Port = 45871;
    private readonly PairingGate _pairing = new();
    private readonly NearbyPairingWindow _nearby = new();
    private CancellationTokenSource? _nearbyWindow;
    public Func<PairingPrompt, CancellationToken, Task<bool>>? ConfirmNearbyPairing { get; set; }
    public void OpenNearbyPairing()
    {
        if (!Running) throw new InvalidOperationException("Turn on nearby devices first.");
        CloseNearbyPairing(); _nearbyWindow = new CancellationTokenSource(TimeSpan.FromMinutes(2)); _nearby.Open();
    }
    public void CloseNearbyPairing() { _nearby.Close(); _nearbyWindow?.Cancel(); }
    private readonly SemaphoreSlim _slots = new(8);
    private readonly List<Task> _clients = [];
    private CancellationTokenSource? _stop;
    private TcpListener? _listener;
    private DiscoveryResponder? _discovery;
    private Task? _accept;
    private readonly object _lifecycle = new();
    private Task? _shutdown;
    public bool Running => _stop is not null;
    public string? LastError { get; private set; }
    public Func<string, Stream, CancellationToken, Task>? ScreenRequestHandler { get; set; }
    public bool IsTrusted(string fingerprint) { lock (settings) return settings.Peers.Any(p => p.Fingerprint == fingerprint); }

    public void Start()
    {
        lock (_lifecycle)
        {
        if (_shutdown is { IsCompleted: false }) throw new InvalidOperationException("Networking is stopping. Try again after it stops.");
        if (Running) return;
        _shutdown = null;
        if (!Identity.IsValid(identity)) throw new InvalidOperationException("Device certificate expired. Back up state and re-pair after resetting identity.");
        var listener = new TcpListener(IPAddress.Any, Port);
        listener.Server.ExclusiveAddressUse = true;
        listener.Start(16);
        try { _discovery = new DiscoveryResponder(settings.DeviceName, Identity.Fingerprint(identity)); }
        catch { listener.Stop(); throw; }
        _listener = listener; _stop = new(); LastError = null;
        _accept = AcceptLoop(_stop.Token);
        }
    }
    public Invitation Invite(string address)
    {
        if (!Running) throw new InvalidOperationException("Enable local networking first.");
        var (token, expires) = _pairing.Open();
        var invitation = new Invitation(1, address, Port, Identity.Fingerprint(identity), token, expires);
        invitation.Validate(); return invitation;
    }
    public void CancelInvitation() => _pairing.Close();
    private async Task AcceptLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                if (!LanAddress.IsLocal(((IPEndPoint)client.Client.RemoteEndPoint!).Address) || !_slots.Wait(0)) { client.Dispose(); continue; }
                _clients.RemoveAll(t => t.IsCompleted);
                _clients.Add(Serve(client, ct));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        { if (!ct.IsCancellationRequested) LastError = "Network listener stopped. Disable and re-enable networking."; }
    }
    private async Task Serve(TcpClient client, CancellationToken stop)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(8)); var ct = timeout.Token;
            try
            {
                using var tls = new SslStream(client.GetStream(), false);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
                    ServerCertificate = identity, ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    AllowTlsResume = false, CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    // Unknown certificates can only attempt explicit pairing; authorization follows below.
                    RemoteCertificateValidationCallback = (_, cert, _, _) => cert is not null && Identity.IsValid(cert)
                }, ct);
                if (tls.RemoteCertificate is null) return;
                var fingerprint = Identity.Fingerprint(tls.RemoteCertificate);
                var message = await Wire.Read<Message>(tls, ct);
                if (message.Operation == "pair-nearby-v1")
                {
                    if (ConfirmNearbyPairing is null || !_nearby.TryEnter()) { await Wire.Write(tls, new Reply(false), ct); return; }
                    try {
                        timeout.CancelAfter(TimeSpan.FromMinutes(2));
                        using var consent = CancellationTokenSource.CreateLinkedTokenSource(ct, _nearbyWindow!.Token);
                        await AcceptNearby(fingerprint, ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString(), message, tls, consent.Token);
                    }
                    finally { _nearby.Exit(); }
                    return;
                }
                if (message.Operation == "screen-v1" && IsTrusted(fingerprint) && ScreenRequestHandler is { } screen)
                {
                    timeout.CancelAfter(TimeSpan.FromMinutes(30));
                    await screen(fingerprint, tls, ct); return;
                }
                var authorized = false;
                if (message.Name is null || message.Name.Length > 80) return;
                lock (settings)
                {
                    if (message.Operation == "pair" && _pairing.Consume(message.Token))
                    {
                        var previous = settings.Peers.ToList();
                        settings.Peers.RemoveAll(p => p.Fingerprint == fingerprint);
                        settings.Peers.Add(new(fingerprint, message.Name, ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString()));
                        try { store.Save(settings); authorized = true; }
                        catch { settings.Peers = previous; throw; }
                    }
                    else if (message.Operation == "ping") authorized = settings.Peers.Any(p => p.Fingerprint == fingerprint);
                }
                await Wire.Write(tls, new Reply(authorized, authorized ? settings.DeviceName : ""), ct);
            }
            catch (Exception ex) when (ex is IOException or AuthenticationException or OperationCanceledException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException or ArgumentException or FormatException)
            { /* Untrusted peers cannot crash the listener. Never log tokens or payloads. */ }
            catch (UnauthorizedAccessException) { LastError = "Unable to save trust. Pairing refused; check local settings permissions."; }
            finally { _slots.Release(); }
        }
    }
    public async Task Pair(Invitation invitation, CancellationToken ct = default)
    {
        invitation.Validate();
        var reply = await Exchange(invitation.Address, invitation.Port, invitation.Fingerprint,
            new Message("pair", settings.DeviceName, invitation.Token), ct);
        if (!reply.Success) throw new AuthenticationException("Pairing refused. Create a new invitation on the other device.");
        lock (settings)
        {
            var previous = settings.Peers.ToList();
            settings.Peers.RemoveAll(p => p.Fingerprint == invitation.Fingerprint);
            settings.Peers.Add(new(invitation.Fingerprint, reply.Name, invitation.Address));
            try { store.Save(settings); } catch { settings.Peers = previous; throw; }
        }
    }
    private void SavePeer(string fingerprint, string name, string address)
    {
        lock (settings)
        {
            var previous = settings.Peers.ToList(); settings.Peers.RemoveAll(p => p.Fingerprint == fingerprint);
            settings.Peers.Add(new(fingerprint, name, address));
            try { store.Save(settings); } catch { settings.Peers = previous; throw; }
        }
    }
    private async Task AcceptNearby(string peerPin, string address, Message message, Stream stream, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(message.Name) || message.Name.Length > 80 || message.Token is null || message.Token.Length != 64 || !message.Token.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid nearby pairing request.");
        var nonce = RandomNumberGenerator.GetBytes(32);
        await Wire.Write(stream, new PairingChallenge(PairingComparison.Commit(nonce), settings.DeviceName), ct);
        var reveal = await Wire.Read<PairingReveal>(stream, ct);
        var peerNonce = PairingComparison.Reveal(reveal.Nonce, message.Token);
        await Wire.Write(stream, new PairingReveal(Convert.ToHexString(nonce)), ct);
        var code = PairingComparison.Code(Identity.Fingerprint(identity), peerPin, peerNonce, nonce);
        var localApproved = await ConfirmNearbyPairing!(new(message.Name, code), ct);
        var remote = await Wire.Read<PairingDecision>(stream, ct);
        ct.ThrowIfCancellationRequested();
        if (localApproved && remote.Approved) SavePeer(peerPin, message.Name, address);
        await Wire.Write(stream, new Reply(localApproved && remote.Approved, settings.DeviceName), ct);
        if (localApproved && remote.Approved) CloseNearbyPairing();
    }
    public async Task PairNearby(DiscoveredDevice device, Func<PairingPrompt, CancellationToken, Task<bool>> confirm, CancellationToken cancel)
    {
        if (!IPAddress.TryParse(device.Address, out var ip) || !LanAddress.IsLocal(ip)) throw new InvalidDataException("Choose a nearby PC.");
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancel); session.CancelAfter(TimeSpan.FromMinutes(2));
        using var client = new TcpClient();
        using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(session.Token)) {
            handshake.CancelAfter(TimeSpan.FromSeconds(8)); await client.ConnectAsync(ip, Port, handshake.Token);
        }
        using var tls = new SslStream(client.GetStream(), false);
        using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(session.Token)) {
            handshake.CancelAfter(TimeSpan.FromSeconds(8)); await AuthenticateClient(tls, device.Fingerprint, handshake.Token);
        }
        var ct = session.Token; var nonce = RandomNumberGenerator.GetBytes(32);
        await Wire.Write(tls, new Message("pair-nearby-v1", settings.DeviceName, PairingComparison.Commit(nonce)), ct);
        // A closed pairing window returns the normal refusal instead of a challenge.
        var challenge = await Wire.Read<System.Text.Json.JsonElement>(tls, ct);
        if (!challenge.TryGetProperty("Commitment", out var commitment)) throw new AuthenticationException("On the other PC, click Allow pairing, then try again.");
        var name = challenge.GetProperty("Name").GetString();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80) throw new InvalidDataException("Invalid device name.");
        await Wire.Write(tls, new PairingReveal(Convert.ToHexString(nonce)), ct);
        var reveal = await Wire.Read<PairingReveal>(tls, ct);
        var peerNonce = PairingComparison.Reveal(reveal.Nonce, commitment.GetString()!);
        var code = PairingComparison.Code(device.Fingerprint, Identity.Fingerprint(identity), nonce, peerNonce);
        var approved = await confirm(new(name, code), ct);
        await Wire.Write(tls, new PairingDecision(approved), ct);
        var reply = await Wire.Read<Reply>(tls, ct); ct.ThrowIfCancellationRequested();
        if (!approved || !reply.Success) throw new AuthenticationException("Pairing canceled. Both PCs must confirm the same code.");
        SavePeer(device.Fingerprint, name, device.Address);
    }
    public async Task Ping(string address, string fingerprint, CancellationToken ct = default)
    {
        lock (settings) if (!settings.Peers.Any(p => p.Fingerprint == fingerprint)) throw new AuthenticationException("Device is not paired.");
        if (!(await Exchange(address, Port, fingerprint, new Message("ping", settings.DeviceName), ct)).Success)
            throw new AuthenticationException("The other device no longer trusts this device. Re-pair explicitly.");
    }
    private async Task<Reply> Exchange(string address, int port, string pin, Message message, CancellationToken cancel)
    {
        if (!IPAddress.TryParse(address, out var ip) || !LanAddress.IsLocal(ip)) throw new InvalidDataException("Only local IPv4 addresses are supported.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel); timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var ct = timeout.Token;
        using var client = new TcpClient(); await client.ConnectAsync(ip, port, ct);
        using var tls = new SslStream(client.GetStream(), false);
        await AuthenticateClient(tls, pin, ct);
        await Wire.Write(tls, message, ct);
        var reply = await Wire.Read<Reply>(tls, ct);
        if (reply.Name is null || reply.Name.Length > 80) throw new InvalidDataException("Invalid device name.");
        return reply;
    }
    private Task AuthenticateClient(SslStream tls, string pin, CancellationToken ct) =>
        tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {
            TargetHost = "HomeBridge", ClientCertificates = new X509CertificateCollection { identity },
            LocalCertificateSelectionCallback = (_, _, _, _, _) => identity,
            RemoteCertificateValidationCallback = (_, cert, _, _) => Identity.Matches(cert, pin),
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, AllowTlsResume = false,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        }, ct);

    public async Task ViewScreen(string address, string fingerprint, Func<byte[], CancellationToken, Task> onFrame, CancellationToken cancel)
    {
        if (!IsTrusted(fingerprint)) throw new AuthenticationException("Pair this device first.");
        if (!IPAddress.TryParse(address, out var ip) || !LanAddress.IsLocal(ip)) throw new InvalidDataException("Enter a private LAN address.");
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancel); session.CancelAfter(TimeSpan.FromMinutes(30));
        using var client = new TcpClient();
        using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(session.Token))
        {
            handshake.CancelAfter(TimeSpan.FromSeconds(8));
            await client.ConnectAsync(ip, Port, handshake.Token);
        }
        using var tls = new SslStream(client.GetStream(), false);
        using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(session.Token))
        {
            handshake.CancelAfter(TimeSpan.FromSeconds(8));
            await AuthenticateClient(tls, fingerprint, handshake.Token);
            await Wire.Write(tls, new Message("screen-v1"), handshake.Token);
            if (!(await Wire.Read<Reply>(tls, handshake.Token)).Success)
                throw new AuthenticationException("Screen viewing was refused. The other PC must approve sharing to this paired device first.");
        }
        while (!session.IsCancellationRequested)
        {
            if (!IsTrusted(fingerprint)) throw new AuthenticationException("Device trust was revoked.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(session.Token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
            await Wire.Write(tls, new Message("next-frame"), deadline.Token);
            var jpeg = await ScreenFrames.Read(tls, deadline.Token);
            await onFrame(jpeg, session.Token);
        }
        session.Token.ThrowIfCancellationRequested();
    }
    public ValueTask DisposeAsync()
    {
        lock (_lifecycle) return new ValueTask(_shutdown ??= StopCore());
    }
    private async Task StopCore()
    {
        CloseNearbyPairing();
        _pairing.Close();
        if (_stop is null) return;
        await _stop.CancelAsync(); _listener?.Stop();
        if (_accept is not null) await _accept;
        await Task.WhenAll(_clients); _clients.Clear();
        if (_discovery is not null) await _discovery.DisposeAsync();
        _stop.Dispose(); _stop = null;
    }
}
