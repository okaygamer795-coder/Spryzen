; SideLoadr installer script (Inno Setup 6).
; Built locally with ISCC.exe or in CI (windows-latest has Inno preinstalled).
; Version is injected: ISCC.exe /DMyAppVersion=0.9.0 installer/sideloader.iss
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#define MyAppName "SideLoadr"
#define MyAppPublisher "Spryzen"
#define MyAppExeName "SideLoadr.exe"

[Setup]
AppId={{7E8F9A0B-1C2D-4E5F-8A6B-7C8D9E0F1A2B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\SideLoadr
DefaultGroupName=SideLoadr
OutputDir=..\installer-output
OutputBaseFilename=SideLoadr-Setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64
UninstallDisplayIcon={app}\{#MyAppExeName}
WizardStyle=modern

[Tasks]
Name: desktopicon; Description: "Create a &desktop icon"; Flags: unchecked

[Files]
; Multi-file self-contained publish (no temp extraction at launch — works on full disks).
Source: "..\publish-app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\SideLoadr"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\SideLoadr"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch SideLoadr"; Flags: nowait postinstall skipifsilent
