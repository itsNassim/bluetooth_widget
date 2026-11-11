; Inno Setup script - run by the GitHub pipeline with /DAppVersion=x.y.z
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{B7E2C5A1-4D3F-4C6A-9E21-5A8D0F7B1C42}
AppName=Bluetooth Battery Tray
AppVersion={#AppVersion}
AppPublisher=BtBatteryTray
DefaultDirName={autopf}\BtBatteryTray
DefaultGroupName=Bluetooth Battery Tray
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=BtBatteryTray-{#AppVersion}-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=Bluetooth Battery Tray
CloseApplications=yes

[Tasks]
Name: "autostart"; Description: "Start with Windows"; GroupDescription: "Options:"

[Files]
Source: "..\publish\BtBatteryTray.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Bluetooth Battery Tray"; Filename: "{app}\BtBatteryTray.exe"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "BtBatteryTray"; ValueData: """{app}\BtBatteryTray.exe"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\BtBatteryTray.exe"; Description: "Launch Bluetooth Battery Tray"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM BtBatteryTray.exe"; Flags: runhidden; RunOnceId: "KillApp"
