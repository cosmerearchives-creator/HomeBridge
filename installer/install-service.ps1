$ErrorActionPreference = 'Stop'
$name = 'HomeBridgeDiscovery'
$expectedRoot = Join-Path $env:ProgramFiles 'HomeBridge'
if ($PSScriptRoot -ne $expectedRoot) { throw 'Repair requires the Program Files installation.' }
$exe = Join-Path $PSScriptRoot 'service\HomeBridge.Service.exe'
$desktop = Join-Path $PSScriptRoot 'HomeBridge.exe'
if (-not (Test-Path -LiteralPath $exe) -or -not (Test-Path -LiteralPath $desktop)) {
    throw 'HomeBridge installation files are missing.'
}
$quotedExe = '"' + $exe + '"'
$existing = Get-CimInstance Win32_Service -Filter "Name='HomeBridgeDiscovery'"
if ($existing) {
    if ($existing.PathName -ne $quotedExe) { throw 'An unrelated service uses the HomeBridge discovery name.' }
    if ($existing.State -ne 'Stopped') { Stop-Service -Name $name }
    & "$env:SystemRoot\System32\sc.exe" config $name start= auto obj= 'NT AUTHORITY\LocalService' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Unable to configure the discovery service.' }
} else {
    & "$env:SystemRoot\System32\sc.exe" create $name binPath= ('\"' + $exe + '\"') start= auto obj= 'NT AUTHORITY\LocalService' DisplayName= 'HomeBridge Discovery' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Unable to register the discovery service.' }
}
Start-Service -Name $name
(Get-Service -Name $name).WaitForStatus('Running', [TimeSpan]::FromSeconds(10))

function Set-HomeBridgeRule($ruleName, $label, $protocol, $port, $program) {
    Remove-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue
    New-NetFirewallRule -Name $ruleName -DisplayName $label -Direction Inbound -Action Allow -Enabled True -Profile Private -Protocol $protocol -LocalPort $port -RemoteAddress LocalSubnet -Program $program -EdgeTraversalPolicy Block | Out-Null
}
Set-HomeBridgeRule 'HomeBridge-Discovery' 'HomeBridge Discovery (Private LAN)' UDP 45679 $exe
Set-HomeBridgeRule 'HomeBridge-PairingDiscovery' 'HomeBridge Pairing Discovery (Private LAN)' UDP 45871 $desktop
Set-HomeBridgeRule 'HomeBridge-PairedFiles' 'HomeBridge Paired Files (Private LAN)' TCP 45871 $desktop
Get-NetFirewallRule -DisplayName 'HomeBridge Discovery (Private LAN)' -ErrorAction SilentlyContinue |
    Where-Object Name -ne 'HomeBridge-Discovery' | Remove-NetFirewallRule
