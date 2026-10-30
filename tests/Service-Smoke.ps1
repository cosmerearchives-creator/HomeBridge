$ErrorActionPreference = 'Stop'
$installer = Join-Path $PSScriptRoot '..\artifacts\HomeBridge-Setup.exe'
$install = Start-Process -FilePath $installer -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/TASKS=backgroundservice' -Wait -PassThru -WindowStyle Hidden
if ($install.ExitCode -ne 0) { throw "Installer failed: $($install.ExitCode)" }
try {
    $service = Get-CimInstance Win32_Service -Filter "Name='HomeBridge'"
    if ($service.StartName -ne 'NT AUTHORITY\LocalService') { throw 'Unexpected service account' }
    if ($service.PathName -ne ('"' + $env:ProgramFiles + '\HomeBridge\service\HomeBridge.Service.exe"')) { throw 'Service path must be quoted and protected' }
    (Get-Service HomeBridge).WaitForStatus('Running', [TimeSpan]::FromSeconds(15))
    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', 'HomeBridge.Status.v1', [System.IO.Pipes.PipeDirection]::In, [System.IO.Pipes.PipeOptions]::Asynchronous, [System.Security.Principal.TokenImpersonationLevel]::Anonymous)
    try {
        $pipe.Connect(5000)
        $bytes = [byte[]]::new(256)
        $cancel = [System.Threading.CancellationTokenSource]::new(5000)
        try { $count = $pipe.ReadAsync($bytes, 0, $bytes.Length, $cancel.Token).GetAwaiter().GetResult() } finally { $cancel.Dispose() }
        $status = [System.Text.Encoding]::UTF8.GetString($bytes, 0, $count)
        if ($status -notmatch 'network=disabled; commands=none') { throw 'Invalid service status' }
    } finally { $pipe.Dispose() }
    # Verify upgrade can stop, replace and restart an already installed worker.
    $upgrade = Start-Process -FilePath $installer -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/TASKS=backgroundservice' -Wait -PassThru -WindowStyle Hidden
    if ($upgrade.ExitCode -ne 0) { throw "Upgrade failed: $($upgrade.ExitCode)" }
    (Get-Service HomeBridge).WaitForStatus('Running', [TimeSpan]::FromSeconds(15))
} finally {
    $uninstaller = Join-Path $env:ProgramFiles 'HomeBridge\unins000.exe'
    $remove = Start-Process -FilePath $uninstaller -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -PassThru -WindowStyle Hidden
    if ($remove.ExitCode -ne 0) { throw "Uninstall failed: $($remove.ExitCode)" }
}
if (Get-Service HomeBridge -ErrorAction SilentlyContinue) { throw 'Service remains after uninstall' }
