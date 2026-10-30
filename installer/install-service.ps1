$ErrorActionPreference = 'Stop'
$name = 'HomeBridgeDiscovery'
$exe = Join-Path $PSScriptRoot 'service\HomeBridge.Service.exe'
if (-not (Test-Path $exe)) { throw "Missing service executable: $exe" }
$existing = Get-Service -Name $name -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -ne 'Stopped') { Stop-Service -Name $name -Force -ErrorAction Stop }
    & sc.exe config $name binPath= ('"' + $exe + '"') start= auto | Out-Null
} else {
    New-Service -Name $name -BinaryPathName ('"' + $exe + '"') -DisplayName 'HomeBridge Discovery' -Description 'Local LAN device discovery; no file or command access.' -StartupType Automatic | Out-Null
}
Start-Service -Name $name

# Discovery requests contain no credentials or remote-control capabilities.
# Restrict the inbound UDP listener to Private networks and local subnet.
$ruleName = 'HomeBridge Discovery (Private LAN)'
Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow -Enabled True -Profile Private -Protocol UDP -LocalPort 45679 -RemoteAddress LocalSubnet | Out-Null
