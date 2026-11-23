; Bluetooth Fleet Monitor - Windows installer (Inno Setup 6).
; Build:  ISCC.exe /DAppVersion=0.3.1 /DSourceExe=<path to published exe> BluetoothFleetMonitor.iss
; Installs for the current user by default (no administrator needed); the user may choose "all users" in the first dialog.
; Data (database, certificate, settings, logs) lives in %LOCALAPPDATA%\BluetoothFleetMonitor\data and is KEPT on uninstall and upgrade.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceExe
  #define SourceExe "..\Builds\Windows\publish\BluetoothFleetMonitor.Desktop.exe"
#endif

[Setup]
AppId={{7B0F5C1E-3D7A-4B58-9C2E-0BF0A000F1E7}
AppName=Bluetooth Fleet Monitor
AppVersion={#AppVersion}
AppVerName=Bluetooth Fleet Monitor {#AppVersion}
AppPublisher=unupunct
AppPublisherURL=https://github.com/unupunct/Bluetooth-Analizer-Best
AppSupportURL=https://github.com/unupunct/Bluetooth-Analizer-Best/issues
DefaultDirName={autopf}\Bluetooth Fleet Monitor
DefaultGroupName=Bluetooth Fleet Monitor
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\Builds\Release
OutputBaseFilename=BluetoothFleetMonitor-{#AppVersion}-Setup
LicenseFile=..\..\LICENSE
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\Windows\Desktop\Assets/app.ico
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
CloseApplications=yes
RestartApplications=no
UninstallDisplayName=Bluetooth Fleet Monitor
UninstallDisplayIcon={app}\BluetoothFleetMonitor.exe
VersionInfoVersion={#AppVersion}
VersionInfoDescription=Bluetooth Fleet Monitor setup

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; DestName: "BluetoothFleetMonitor.exe"; Flags: ignoreversion
Source: "appsettings.installed.json"; DestDir: "{app}"; DestName: "appsettings.json"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Bluetooth Fleet Monitor"; Filename: "{app}\BluetoothFleetMonitor.exe"
Name: "{autodesktop}\Bluetooth Fleet Monitor"; Filename: "{app}\BluetoothFleetMonitor.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\BluetoothFleetMonitor.exe"; Description: "Start Bluetooth Fleet Monitor"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    MsgBox('Bluetooth Fleet Monitor was removed.' + #13#10 + #13#10 +
           'Your data (database, pairing, settings, logs) was kept in:' + #13#10 +
           ExpandConstant('{localappdata}\BluetoothFleetMonitor\data') + #13#10 +
           'Delete that folder yourself if you want it gone.', mbInformation, MB_OK);
end;
