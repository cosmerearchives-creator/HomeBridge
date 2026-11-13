using HomeBridge.Core;
using System.Diagnostics;
using System.IO;

namespace HomeBridge.Desktop;

internal static class NetworkDiagnostics
{
    private static string PowerShell => Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    public static async Task<string> Inspect(CancellationToken ct)
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            $profiles = @(Get-NetConnectionProfile | ForEach-Object { "$($_.InterfaceAlias): $($_.NetworkCategory)" })
            if ($profiles.Count) { 'Network profiles: ' + ($profiles -join ', ') } else { 'No connected Windows network profile was found.' }
            if (-not (Get-NetConnectionProfile | Where-Object NetworkCategory -eq 'Private')) {
                'HomeBridge inbound rules require a Private profile. If this is your trusted home network, review its profile in Windows Settings.'
            }
            $service = Get-Service -Name HomeBridgeDiscovery -ErrorAction SilentlyContinue
            if ($service) { "HomeBridge discovery service: $($service.Status)" } else { 'HomeBridge discovery service is missing.' }
            function CheckRule($name, $protocol, $port, $program) {
                $rules = @(Get-NetFirewallRule -PolicyStore ActiveStore -Name $name -ErrorAction SilentlyContinue)
                foreach ($rule in $rules) {
                    $filter = $rule | Get-NetFirewallPortFilter
                    $address = $rule | Get-NetFirewallAddressFilter
                    $application = $rule | Get-NetFirewallApplicationFilter
                    if ($rule.Enabled -eq 'True' -and $rule.Direction -eq 'Inbound' -and $rule.Action -eq 'Allow' -and
                        $rule.Profile -eq 'Private' -and $filter.Protocol -eq $protocol -and $filter.LocalPort -eq $port -and
                        (@($address.RemoteAddress) -join ',') -eq 'LocalSubnet' -and $application.Program -eq $program) {
                        "$name firewall rule: correct"
                        return
                    }
                }
                "$name firewall rule: missing or misconfigured"
            }
            CheckRule 'HomeBridge-Discovery' 'UDP' '45679' (Join-Path $env:ProgramFiles 'HomeBridge\service\HomeBridge.Service.exe')
            CheckRule 'HomeBridge-PairingDiscovery' 'UDP' '45871' (Join-Path $env:ProgramFiles 'HomeBridge\HomeBridge.exe')
            CheckRule 'HomeBridge-PairedFiles' 'TCP' '45871' (Join-Path $env:ProgramFiles 'HomeBridge\HomeBridge.exe')
            """;
        var start = new ProcessStartInfo(PowerShell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", script }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Windows diagnostics could not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) process.Kill(); }
        var result = await output;
        return process.ExitCode == 0 ? result.Trim() : result.Trim() + Environment.NewLine + "Windows could not verify some checks: " + (await errors).Trim();
    }

    public static async Task Repair(CancellationToken ct)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "HomeBridge");
        var script = Path.Combine(folder, "install-service.ps1");
        if (!File.Exists(script)) throw new IOException("Install HomeBridge from GitHub Releases before repairing.");
        SharePaths.CheckLocal(script);
        var start = new ProcessStartInfo(PowerShell) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = folder };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Windows could not start the repair.");
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0) throw new IOException("Repair did not finish. Try reinstalling HomeBridge and run diagnostics again.");
    }
}
