using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace HomeBridge.Desktop;
public static class DeviceDiscovery
{
    public static async Task<IReadOnlyList<string>> ScanAsync(TimeSpan timeout)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        udp.EnableBroadcast = true;
        var request = Encoding.ASCII.GetBytes("HOMEBRIDGE_DISCOVER_V1");
        await udp.SendAsync(request, new IPEndPoint(IPAddress.Broadcast, 45679));
        var devices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cancel = new System.Threading.CancellationTokenSource(timeout);
        while (true)
        {
            try {
                var reply = await udp.ReceiveAsync(cancel.Token);
                var text = Encoding.UTF8.GetString(reply.Buffer);
                if (!text.StartsWith("HOMEBRIDGE_HERE_V1:", StringComparison.Ordinal)) continue;
                var name = text.Substring("HOMEBRIDGE_HERE_V1:".Length);
                if (name.Length is > 0 and < 128 && !name.Contains('\n') && !name.Contains('\r'))
                    devices.Add(name + " (" + reply.RemoteEndPoint.Address + ")");
            } catch (OperationCanceledException) { break; }
        }
        return new List<string>(devices);
    }
}
