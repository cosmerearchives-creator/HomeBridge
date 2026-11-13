param([Parameter(Mandatory)][string]$Installer)
$ErrorActionPreference = 'Stop'
$root = Join-Path $env:ProgramFiles 'HomeBridge'
if ((Test-Path -LiteralPath $root) -or (Get-Service HomeBridgeDiscovery -ErrorAction SilentlyContinue)) {
    throw 'Run installer smoke tests on a clean CI runner only.'
}
$setup = (Resolve-Path -LiteralPath $Installer).Path
try {
    foreach ($pass in 1, 2) {
        $process = Start-Process -FilePath $setup -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-' -WindowStyle Hidden -Wait -PassThru
        if ($process.ExitCode -ne 0) { throw "Installer pass $pass failed with exit code $($process.ExitCode)." }
        $service = Get-CimInstance Win32_Service -Filter "Name='HomeBridgeDiscovery'"
        if ($service.State -ne 'Running' -or $service.StartName -notmatch 'LocalService$') {
            throw 'HomeBridgeDiscovery must be running as LocalService.'
        }
        foreach ($expected in @(
            @{ Name = 'HomeBridge-Discovery'; Protocol = 'UDP'; Port = '45679'; Program = 'service\HomeBridge.Service.exe' },
            @{ Name = 'HomeBridge-PairingDiscovery'; Protocol = 'UDP'; Port = '45871'; Program = 'HomeBridge.exe' },
            @{ Name = 'HomeBridge-PairedFiles'; Protocol = 'TCP'; Port = '45871'; Program = 'HomeBridge.exe' }
        )) {
            $rule = Get-NetFirewallRule -Name $expected.Name
            $port = $rule | Get-NetFirewallPortFilter
            $address = $rule | Get-NetFirewallAddressFilter
            $program = $rule | Get-NetFirewallApplicationFilter
            if ($rule.Enabled -ne 'True' -or $rule.Profile -ne 'Private' -or $rule.Direction -ne 'Inbound' -or $rule.Action -ne 'Allow' -or
                $port.Protocol -ne $expected.Protocol -or $port.LocalPort -ne $expected.Port -or
                ($address.RemoteAddress -join ',') -ne 'LocalSubnet' -or $program.Program -ne (Join-Path $root $expected.Program)) {
                throw "Unexpected firewall configuration for $($expected.Name)."
            }
        }
        foreach ($key in 'HomeBridge.Share', 'HomeBridge.Stop', 'HomeBridge.Send') {
            if (-not (Test-Path -LiteralPath "Registry::HKEY_LOCAL_MACHINE\Software\Classes\*\shell\$key\command")) {
                throw "Missing Explorer command $key."
            }
        }
    }
}
finally {
    $uninstall = Join-Path $root 'unins000.exe'
    if (Test-Path -LiteralPath $uninstall) {
        $process = Start-Process -FilePath $uninstall -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -WindowStyle Hidden -Wait -PassThru
        if ($process.ExitCode -ne 0) { throw "Uninstall failed with exit code $($process.ExitCode)." }
    }
}
if ((Get-Service HomeBridgeDiscovery -ErrorAction SilentlyContinue) -or
    (Get-NetFirewallRule -Name 'HomeBridge-Discovery','HomeBridge-PairingDiscovery','HomeBridge-PairedFiles' -ErrorAction SilentlyContinue)) {
    throw 'Uninstall left the service or firewall rules behind.'
}
