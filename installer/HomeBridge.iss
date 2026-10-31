#define MyAppName "HomeBridge"
#ifndef MyAppVersion
#define MyAppVersion "1.0.0-alpha.6"
#endif
[Setup]
AppId={{A18862F5-1AB5-4B7E-BABE-4878F7B10D29}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
DefaultDirName={autopf}\HomeBridge
DefaultGroupName=HomeBridge
OutputDir=..\artifacts
OutputBaseFilename=HomeBridge-Setup
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UninstallDisplayIcon={app}\HomeBridge.exe
[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\publish-service\*"; DestDir: "{app}\service"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "install-service.ps1"; DestDir: "{app}"; Flags: ignoreversion
[Icons]
Name: "{autoprograms}\HomeBridge"; Filename: "{app}\HomeBridge.exe"
Name: "{autodesktop}\HomeBridge"; Filename: "{app}\HomeBridge.exe"; Tasks: desktopicon
[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; Flags: unchecked
[Run]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\install-service.ps1"""; Flags: runhidden waituntilterminated
Filename: "{app}\HomeBridge.exe"; Description: "Launch HomeBridge"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop HomeBridgeDiscovery"; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "delete HomeBridgeDiscovery"; Flags: runhidden waituntilterminated
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Get-NetFirewallRule -DisplayName 'HomeBridge Discovery (Private LAN)' -ErrorAction SilentlyContinue | Remove-NetFirewallRule"""; Flags: runhidden waituntilterminated

[Code]
type
  TServiceStatus = record
    ServiceType, CurrentState, ControlsAccepted, Win32ExitCode,
    ServiceSpecificExitCode, CheckPoint, WaitHint: LongWord;
  end;
function OpenSCManager(Machine, Database, Access: LongWord): THandle;
  external 'OpenSCManagerW@advapi32.dll stdcall';
function OpenService(Manager: THandle; Name: string; Access: LongWord): THandle;
  external 'OpenServiceW@advapi32.dll stdcall';
function QueryServiceStatus(Service: THandle; var Status: TServiceStatus): Boolean;
  external 'QueryServiceStatus@advapi32.dll stdcall';
function ControlService(Service: THandle; Control: LongWord; var Status: TServiceStatus): Boolean;
  external 'ControlService@advapi32.dll stdcall';
function CloseServiceHandle(Handle: THandle): Boolean;
  external 'CloseServiceHandle@advapi32.dll stdcall';
function StopAndWait(Name: string): Boolean;
var Manager, Service: THandle; Status: TServiceStatus; I: Integer;
begin
  Result := False;
  Manager := OpenSCManager(0, 0, 1);
  if Manager = 0 then exit;
  Service := OpenService(Manager, Name, $24);
  if Service <> 0 then begin
    if QueryServiceStatus(Service, Status) then begin
      if Status.CurrentState <> 1 then ControlService(Service, 1, Status);
      for I := 1 to 100 do begin
        if not QueryServiceStatus(Service, Status) then break;
        if Status.CurrentState = 1 then begin Result := True; break; end;
        Sleep(100);
      end;
    end;
    CloseServiceHandle(Service);
  end;
  CloseServiceHandle(Manager);
end;

function PrepareToInstall(var NeedsRestart: Boolean): string;
var Image: string;
begin
  Result := '';
  if RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\HomeBridgeDiscovery') then begin
    if not RegQueryStringValue(HKLM, 'SYSTEM\CurrentControlSet\Services\HomeBridgeDiscovery', 'ImagePath', Image) or
       (CompareText(Image, '"' + ExpandConstant('{app}\service\HomeBridge.Service.exe') + '"') <> 0) then
      Result := 'An unrelated HomeBridgeDiscovery service exists. Resolve it before upgrading.'
    else if not StopAndWait('HomeBridgeDiscovery') then
      Result := 'The discovery service could not stop. Stop it in Services and retry.';
  end;
end;
