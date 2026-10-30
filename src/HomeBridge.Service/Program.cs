using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "HomeBridge");
builder.Services.AddHostedService<StatusWorker>();
await builder.Build().RunAsync();

// LocalService, no network sockets, user paths, plugins, shell execution or impersonation.
// Status is informational only. It must never authorize a desktop operation.
sealed class StatusWorker(ILogger<StatusWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var acl = new PipeSecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        foreach (var type in new[] { WellKnownSidType.LocalServiceSid, WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(type, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.Read | PipeAccessRights.Synchronize, AccessControlType.Allow));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var pipe = NamedPipeServerStreamAcl.Create("HomeBridge.Status.v1", PipeDirection.Out, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 1024, acl);
                await pipe.WaitForConnectionAsync(stoppingToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                await pipe.WriteAsync(Encoding.UTF8.GetBytes("HomeBridge service v1: running; network=disabled; commands=none\n"), timeout.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                logger.LogWarning("Local status pipe unavailable ({ErrorType}); retrying.", ex.GetType().Name);
                try { await Task.Delay(1000, stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
    }
}
