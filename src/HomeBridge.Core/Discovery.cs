using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace HomeBridge.Core;

public sealed record Advertisement(int Version, string Nonce, string Name, string Fingerprint);
public sealed record DiscoveredDevice(string Address, string Name, string Fingerprint)
{
    public override string ToString() => $"{Name} — {Address}";
}

public sealed class DiscoveryResponder : IAsyncDisposable
{
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    public DiscoveryResponder(string name, string fingerprint)
    {
        _udp = new UdpClient(AddressFamily.InterNetwork);
        try { _udp.ExclusiveAddressUse = true; _udp.Client.Bind(new IPEndPoint(IPAddress.Any, NetworkAgent.Port)); }
        catch { _udp.Dispose(); throw; }
        _loop = Run(name, fingerprint);
    }
    private async Task Run(string name, string fingerprint)
    {
        var nextResponse = DateTimeOffset.MinValue;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var packet = await _udp.ReceiveAsync(_stop.Token);
                if (!LanAddress.IsLocal(packet.RemoteEndPoint.Address) || packet.Buffer.Length > 256 || DateTimeOffset.UtcNow < nextResponse) continue;
                var query = System.Text.Encoding.ASCII.GetString(packet.Buffer);
                if (!query.StartsWith("HB1:") || query.Length != 36 || !query[4..].All(Uri.IsHexDigit)) continue;
                nextResponse = DateTimeOffset.UtcNow.AddMilliseconds(250);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new Advertisement(1, query[4..], name, fingerprint));
                await _udp.SendAsync(bytes, packet.RemoteEndPoint, _stop.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    public static async Task<IReadOnlyList<DiscoveredDevice>> Scan(CancellationToken cancel = default)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var query = System.Text.Encoding.ASCII.GetBytes("HB1:" + nonce);
        await udp.SendAsync(query, new IPEndPoint(IPAddress.Broadcast, NetworkAgent.Port), cancel);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel); timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var results = new Dictionary<string, DiscoveredDevice>();
        try
        {
            while (results.Count < 64)
            {
                var packet = await udp.ReceiveAsync(timeout.Token);
                if (packet.Buffer.Length > 1024 || !LanAddress.IsLocal(packet.RemoteEndPoint.Address)) continue;
                try
                {
                    var ad = JsonSerializer.Deserialize<Advertisement>(packet.Buffer);
                    if (ad is not { Version: 1 } || ad.Nonce != nonce || ad.Name is null || ad.Name.Length > 80 ||
                        ad.Fingerprint is null || ad.Fingerprint.Length != 64 || !ad.Fingerprint.All(Uri.IsHexDigit)) continue;
                    var address = packet.RemoteEndPoint.Address.ToString();
                    results[address] = new(address, ad.Name, ad.Fingerprint);
                }
                catch (JsonException) { }
            }
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { }
        return results.Values.ToList();
    }
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync(); _udp.Dispose(); await _loop; _stop.Dispose();
    }
}
