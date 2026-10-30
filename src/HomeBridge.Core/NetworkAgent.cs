using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace HomeBridge.Core;

// Runs with the interactive user's rights. This protocol has no file, shell, or input commands.
public sealed class NetworkAgent(StateStore store, UserSettings settings, X509Certificate2 identity) : IAsyncDisposable
{
    public const int Port = 45871;
    private readonly PairingGate _pairing = new();
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
                    // Unknown certificates may only submit an invitation. Authorization follows below.
                    RemoteCertificateValidationCallback = (_, cert, _, _) => cert is not null && Identity.IsValid(cert)
                }, ct);
                if (tls.RemoteCertificate is null) return;
                var fingerprint = Identity.Fingerprint(tls.RemoteCertificate);
                var message = await Wire.Read<Message>(tls, ct);
                var authorized = false;
                if (message.Name is null || message.Name.Length > 80) return;
                lock (settings)
                {
                    if (message.Operation == "pair" && _pairing.Consume(message.Token))
                    {
                        var previous = settings.Peers.ToList();
                        settings.Peers.RemoveAll(p => p.Fingerprint == fingerprint);
                        settings.Peers.Add(new(fingerprint, message.Name));
                        try { store.Save(settings); authorized = true; }
                        catch { settings.Peers = previous; throw; }
                    }
                    else if (message.Operation == "ping") authorized = settings.Peers.Any(p => p.Fingerprint == fingerprint);
                }
                await Wire.Write(tls, new Reply(authorized, authorized ? settings.DeviceName : ""), ct);
            }
            catch (Exception ex) when (ex is IOException or AuthenticationException or OperationCanceledException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException or ArgumentException)
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
            settings.Peers.Add(new(invitation.Fingerprint, reply.Name));
            try { store.Save(settings); } catch { settings.Peers = previous; throw; }
        }
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
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {
            TargetHost = "HomeBridge", ClientCertificates = new X509CertificateCollection { identity },
            LocalCertificateSelectionCallback = (_, _, _, _, _) => identity,
            RemoteCertificateValidationCallback = (_, cert, _, _) => Identity.Matches(cert, pin),
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, AllowTlsResume = false,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        }, ct);
        await Wire.Write(tls, message, ct);
        var reply = await Wire.Read<Reply>(tls, ct);
        if (reply.Name is null || reply.Name.Length > 80) throw new InvalidDataException("Invalid device name.");
        return reply;
    }
    public ValueTask DisposeAsync()
    {
        lock (_lifecycle) return new ValueTask(_shutdown ??= StopCore());
    }
    private async Task StopCore()
    {
        _pairing.Close();
        if (_stop is null) return;
        await _stop.CancelAsync(); _listener?.Stop();
        if (_accept is not null) await _accept;
        await Task.WhenAll(_clients); _clients.Clear();
        if (_discovery is not null) await _discovery.DisposeAsync();
        _stop.Dispose(); _stop = null;
    }
}
