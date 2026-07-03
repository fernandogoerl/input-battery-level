; Inno Setup script for Peripheral Battery Tray.
; Produces a per-user installer (no admin required) that installs the self-contained
; single-file exe, optionally enables "start with Windows", and cleans up on uninstall.

#define AppName "Peripheral Battery Tray"
#define AppExeName "BatteryTray.exe"
#define AppVersion "1.0.0"
#define AppPublisher "Fernando Goerl"
#define AppUrl "https://github.com/fernandogoerl/input-battery-level"
#define RunValueName "BatteryTray"

[Setup]
; Keep this AppId stable across versions so upgrades replace in place.
AppId={{ACCEFA47-1ADE-4D9A-A5EB-1504759AD3E1}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={localappdata}\Programs\BatteryTray
DisableProgramGroupPage=yes
DisableDirPage=yes
; Standard-user install: HID and XInput need no elevation.
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=BatteryTray-Setup-{#AppVersion}
SetupIconFile=..\src\BatteryTray\app.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; We close/relaunch the app ourselves in [Code]; skip the Restart Manager UI.
CloseApplications=no
; --- Requirements check ------------------------------------------------------
; The app is self-contained (bundles the .NET 8 runtime), so there is no separate
; runtime to install. It does need 64-bit Windows 10 1809 (10.0.17763) or newer for
; the WinRT Bluetooth-battery APIs. Setup refuses to run on anything older or on a
; non-x64 CPU, with a clear message, rather than installing something that won't work.
MinVersion=10.0.17763
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Files]
Source: "..\publish\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Comment: "Monitor peripheral battery levels from the system tray"

[Tasks]
Name: "startup"; Description: "Start automatically when I sign in to Windows"; GroupDescription: "Startup:"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#RunValueName}"; ValueData: """{app}\{#AppExeName}"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName} now"; Flags: nowait postinstall skipifsilent

[Code]
function InitializeSetup(): Boolean;
var
  Version: TWindowsVersion;
begin
  GetWindowsVersionEx(Version);
  // 64-bit Windows 10 1809+ is required (see [Setup] MinVersion). Give a clearer,
  // app-specific message than the generic "unsupported version" one.
  if not IsWin64 then
  begin
    MsgBox('Peripheral Battery Tray requires 64-bit Windows.' + #13#10 +
           'This machine is running 32-bit Windows, so Setup cannot continue.',
           mbCriticalError, MB_OK);
    Result := False;
    Exit;
  end;
  if (Version.Major < 10) or ((Version.Major = 10) and (Version.Build < 17763)) then
  begin
    MsgBox('Peripheral Battery Tray requires Windows 10 version 1809 (build 17763)' + #13#10 +
           'or newer. Please update Windows and try again.',
           mbCriticalError, MB_OK);
    Result := False;
    Exit;
  end;
  Result := True;
end;

procedure KillRunning;
var
  ResultCode: Integer;
begin
  Exec('taskkill.exe', '/f /im {#AppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  // Close any running instance so its file isn't locked during copy.
  if CurStep = ssInstall then
    KillRunning;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    KillRunning;
    // The app can enable autostart itself from its tray menu; remove that entry
    // too, even if the install-time task wasn't selected.
    RegDeleteValue(HKEY_CURRENT_USER,
      'Software\Microsoft\Windows\CurrentVersion\Run', '{#RunValueName}');
  end;
end;
