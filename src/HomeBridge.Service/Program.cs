using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "HomeBridgeDiscovery");
builder.Services.AddHostedService<DiscoveryWorker>();
await builder.Build().RunAsync();

sealed class DiscoveryWorker(ILogger<DiscoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 45679));
        logger.LogInformation("HomeBridge discovery service listening");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var incoming = await udp.ReceiveAsync(stoppingToken);
                if (Encoding.ASCII.GetString(incoming.Buffer) != "HOMEBRIDGE_DISCOVER_V1") continue;
                if (!IsPrivate(incoming.RemoteEndPoint.Address)) continue;
                var payload = Encoding.UTF8.GetBytes("HOMEBRIDGE_HERE_V1:" + Environment.MachineName);
                await udp.SendAsync(payload, incoming.RemoteEndPoint, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Discovery request failed"); }
        }
    }
    private static bool IsPrivate(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        if (b.Length != 4) return false;
        return b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] >= 16 && b[1] <= 31;
    }
}
