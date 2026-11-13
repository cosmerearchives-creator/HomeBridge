using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using HomeBridge.Core;

namespace HomeBridge.Desktop;

internal sealed record ExplorerCommand(string Verb, string? Path);

internal static class ExplorerCommands
{
    public static string Channel { get; } = "HomeBridge.Explorer." + WindowsIdentity.GetCurrent().User!.Value;
    public static ExplorerCommand? Parse(string[] args) => args switch
    {
        ["--view-shares"] => new("view", null),
        ["--share", var path] => new("share", path),
        ["--stop-sharing", var path] => new("stop", path),
        ["--send", var path] => new("send", path),
        _ => null
    };

    public static void Forward(ExplorerCommand command)
    {
        using var pipe = new NamedPipeClientStream(".", Channel, PipeDirection.Out, PipeOptions.CurrentUserOnly,
            TokenImpersonationLevel.Identification);
        pipe.Connect(3000);
        Wire.Write(pipe, command, CancellationToken.None).GetAwaiter().GetResult();
    }

    public static async Task Listen(Func<ExplorerCommand, Task> dispatch, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            using var pipe = new NamedPipeServerStream(Channel, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
            try
            {
                await pipe.WaitForConnectionAsync(stop);
                using var read = CancellationTokenSource.CreateLinkedTokenSource(stop);
                read.CancelAfter(TimeSpan.FromSeconds(3));
                var command = await Wire.Read<ExplorerCommand>(pipe, read.Token);
                pipe.Disconnect();
                await dispatch(command);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or JsonException) { }
        }
    }
}
