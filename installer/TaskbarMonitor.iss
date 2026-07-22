; Inno Setup script for TaskbarMonitor.
; Bundles the self-contained single-file build (no .NET runtime required),
; registers the elevated logon Scheduled Task, and cleans everything up on
; uninstall. Build with installer\build-installer.ps1.

#define MyAppName "TaskbarMonitor"
#define MyAppVersion "1.2.0"
#define MyAppPublisher "Marllon Gomes"
#define MyAppURL "https://github.com/MarllonGomes/TaskbarMonitor"
#define MyAppExeName "TaskbarMonitor.exe"

[Setup]
AppId={{B7E6D3A2-4C19-4F7B-9A64-2E85C1F0D9AB}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\{#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
; admin: required for the elevated Scheduled Task (temperature sensors)
PrivilegesRequired=admin
OutputDir=output
OutputBaseFilename=TaskbarMonitor-Setup-{#MyAppVersion}
SetupIconFile=..\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "tasksetup.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\SECURITY.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

[Run]
; register the logon task (elevated, no time limit) and start the app
Filename: "powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\tasksetup.ps1"" -Install"; \
    Flags: runhidden waituntilterminated; \
    StatusMsg: "Registering startup task..."

[UninstallRun]
; stop the app and remove the task before files are deleted
Filename: "powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\tasksetup.ps1"" -Uninstall"; \
    Flags: runhidden waituntilterminated; \
    RunOnceId: "RemoveScheduledTask"

[Code]
// Stop any running instance (and its task) before copying files,
// so the exe is never locked during an update.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  R: Integer;
begin
  Exec('schtasks.exe', '/End /TN TaskbarMonitor', '', SW_HIDE, ewWaitUntilTerminated, R);
  Exec('taskkill.exe', '/F /IM TaskbarMonitor.exe', '', SW_HIDE, ewWaitUntilTerminated, R);
  Sleep(300);
  Result := '';
end;
