#define MyAppName "HomeBridge"
#define MyAppVersion "1.0.0-alpha.1"
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
[Icons]
Name: "{autoprograms}\HomeBridge"; Filename: "{app}\HomeBridge.exe"
Name: "{autodesktop}\HomeBridge"; Filename: "{app}\HomeBridge.exe"; Tasks: desktopicon
[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; Flags: unchecked
[Run]
Filename: "{app}\HomeBridge.exe"; Description: "Launch HomeBridge"; Flags: nowait postinstall skipifsilent
