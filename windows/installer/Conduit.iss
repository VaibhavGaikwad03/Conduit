; Conduit — Windows installer (Inno Setup 6)
; Builds a single ConduitSetup-<version>.exe from a self-contained publish.
; Compile with installer\build-installer.ps1 (which publishes first, then runs ISCC).

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif

#define AppName "Conduit"
#define AppPublisher "Vaibhav Gaikwad"
#define AppExe "Conduit.exe"
#define AppId "{{7C3F2A10-9E44-4B2D-8C1A-CD0177A1C0DE}"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExe}
OutputDir=..\artifacts\installer
OutputBaseFilename=ConduitSetup-{#AppVersion}
SetupIconFile=..\src\Conduit.App\Assets\conduit.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; App is win-x64 self-contained, so require a 64-bit Windows.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Firewall rules + Program Files need admin.
PrivilegesRequired=admin

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startup"; Description: "Start Conduit automatically when I sign in"; GroupDescription: "Startup:"

[Files]
; The entire self-contained publish folder (includes ConduitCamera.dll).
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Optional launch-at-login (per-machine). Created only if the "startup" task is picked.
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
  ValueName: "Conduit"; ValueData: """{app}\{#AppExe}"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
; Allow Conduit through Windows Firewall (all ports it uses: UDP 5461 discovery, TCP 5462 session).
Filename: "{sys}\netsh.exe"; \
  Parameters: "advfirewall firewall add rule name=""Conduit"" dir=in action=allow program=""{app}\{#AppExe}"" enable=yes profile=private,domain"; \
  Flags: runhidden; StatusMsg: "Adding Windows Firewall rule..."
; Offer to launch when finished.
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; \
  Flags: nowait postinstall skipifsilent

[UninstallRun]
; Remove the firewall rule.
Filename: "{sys}\netsh.exe"; \
  Parameters: "advfirewall firewall delete rule name=""Conduit"""; \
  Flags: runhidden; RunOnceId: "DelConduitFwRule"
; Unregister the virtual-camera DLL if the app registered it (best-effort).
Filename: "{sys}\regsvr32.exe"; \
  Parameters: "/u /s ""{commonappdata}\Conduit\ConduitCamera.dll"""; \
  Flags: runhidden; RunOnceId: "UnregConduitCamera"

[UninstallDelete]
; Clean up the service-readable camera DLL copy left by the app (in ProgramData).
Type: filesandordirs; Name: "{commonappdata}\Conduit"
