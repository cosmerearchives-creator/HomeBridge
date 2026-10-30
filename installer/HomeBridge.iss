#define MyAppName "HomeBridge"
#define MyAppVersion "1.0.0-alpha.3"
[Setup]
AppId={{A18862F5-1AB5-4B7E-BABE-4878F7B10D29}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
DefaultDirName={autopf}\HomeBridge
DisableDirPage=yes
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
[InstallDelete]
Type: files; Name: "{app}\install-service.ps1"
[Icons]
Name: "{autoprograms}\HomeBridge"; Filename: "{app}\HomeBridge.exe"
Name: "{autodesktop}\HomeBridge"; Filename: "{app}\HomeBridge.exe"; Tasks: desktopicon
[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; Flags: unchecked
Name: backgroundservice; Description: "Install local status service (LocalService; no network access)"; Flags: unchecked
[Run]
Filename: "{app}\HomeBridge.exe"; Description: "Launch HomeBridge"; Flags: nowait postinstall skipifsilent
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
var HadService, HadLegacyService: Boolean;

function ServiceMissing: Boolean;
begin
  Result := not RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\HomeBridge');
end;

function OwnsService(Name: string): Boolean;
var Image: string;
begin
  Result := RegQueryStringValue(HKLM, 'SYSTEM\CurrentControlSet\Services\' + Name, 'ImagePath', Image) and
    (CompareText(Image, '"' + ExpandConstant('{app}\service\HomeBridge.Service.exe') + '"') = 0);
end;

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

procedure RunSC(Parameters: string);
var Code: Integer;
begin
  if not Exec(ExpandConstant('{sys}\sc.exe'), Parameters, '', SW_HIDE, ewWaitUntilTerminated, Code) then
    RaiseException('Could not run Windows Service Control.');
  if Code <> 0 then RaiseException('Windows Service Control failed: ' + IntToStr(Code));
end;

function PrepareToInstall(var NeedsRestart: Boolean): string;
begin
  Result := '';
  HadService := not ServiceMissing;
  HadLegacyService := RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\HomeBridgeDiscovery');
  if (HadService or HadLegacyService or WizardIsTaskSelected('backgroundservice')) and
      (CompareText(ExpandConstant('{app}'), ExpandConstant('{autopf}\HomeBridge')) <> 0) then begin
    Result := 'The service requires the protected default Program Files\HomeBridge location.'; exit;
  end;
  if HadService then begin
    if not OwnsService('HomeBridge') then Result := 'An unrelated HomeBridge service exists. Resolve that service before installing.'
    else if not StopAndWait('HomeBridge') then Result := 'HomeBridge service did not stop. Stop it in Services and retry.';
  end;
  if (Result = '') and HadLegacyService then begin
    if not OwnsService('HomeBridgeDiscovery') then Result := 'An unrelated HomeBridgeDiscovery service exists. Resolve it before installing.'
    else if not StopAndWait('HomeBridgeDiscovery') then Result := 'Legacy discovery service did not stop. Stop it in Services and retry.';
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then begin
    if HadLegacyService then RunSC('delete HomeBridgeDiscovery');
    if (HadLegacyService or WizardIsTaskSelected('backgroundservice')) and ServiceMissing then
      RunSC('create HomeBridge binPath= "\"' + ExpandConstant('{app}\service\HomeBridge.Service.exe') + '\"" start= auto obj= "NT AUTHORITY\LocalService"');
    if HadService or HadLegacyService or WizardIsTaskSelected('backgroundservice') then RunSC('start HomeBridge');
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usUninstall) and not ServiceMissing then begin
    if not OwnsService('HomeBridge') then RaiseException('Refusing to remove an unrelated HomeBridge service.');
    if not StopAndWait('HomeBridge') then RaiseException('HomeBridge service did not stop. Stop it in Services and retry uninstall.');
    RunSC('delete HomeBridge');
  end;
end;
