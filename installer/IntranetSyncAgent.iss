; Inno Setup script for Intranet Sync Agent
; Target: Windows Server 2012 R2+ / .NET Framework 4.5.1 (inbox on 2012 R2)
; Does NOT bundle or install .NET 4.8 — avoids reboot-blocking redist on locked hosts.
; Aliyun / API credentials are configured later in Admin UI.

#define MyAppName "Intranet Sync Agent"
#define MyAppVersion "1.2.0"
#define MyAppPublisher "Intranet Sync"
#define MyAppExeName "IntranetSync.Service.exe"
#define MyServiceName "IntranetSyncAgent"
#define MyAppURL "http://127.0.0.1:8787/"
; .NET Framework 4.5.1 Release DWORD (Server 2012 R2 inbox): 378675+
#define DotNet451Release 378675

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-INTRANETSYNC01}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={pf}\IntranetSyncAgent
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=IntranetSyncAgent-Setup
Compression=lzma
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x86 x64
ArchitecturesInstallIn64BitMode=x64
MinVersion=6.1sp1
WizardStyle=modern
UninstallDisplayName={#MyAppName}
SetupLogging=yes
CloseApplications=yes
RestartApplications=no

[Dirs]
Name: "{commonappdata}\IntranetSyncAgent"; Flags: uninsneveruninstall

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut to open Admin UI"; GroupDescription: "Additional icons:"; Flags: unchecked

[Files]
; Release build output from IntranetSync.Service (exe + project refs + NuGet DLLs)
Source: "..\src\IntranetSync.Service\bin\Release\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb;*.xml"
Source: "OpenAdmin.cmd"; DestDir: "{app}"; Flags: ignoreversion
Source: "InstallService.cmd"; DestDir: "{app}"; Flags: ignoreversion
Source: "UninstallService.cmd"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Open Admin UI"; Filename: "{app}\OpenAdmin.cmd"; WorkingDir: "{app}"; Comment: "Open local Admin UI (configured port, default 8787)"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{commondesktop}\Intranet Sync Admin"; Filename: "{app}\OpenAdmin.cmd"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{cmd}"; Parameters: "/c ""{app}\InstallService.cmd"""; StatusMsg: "Registering and starting Windows Service..."; Flags: runhidden waituntilterminated
Filename: "{app}\OpenAdmin.cmd"; Description: "Open Admin UI now"; Flags: postinstall nowait skipifsilent unchecked

[UninstallRun]
Filename: "{cmd}"; Parameters: "/c ""{app}\UninstallService.cmd"""; RunOnceId: "StopUninstallService"; Flags: runhidden waituntilterminated

[Code]
function IsDotNet451OrHigher(): Boolean;
var
  Release: Cardinal;
begin
  Result := False;
  if RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) then
    Result := Release >= {#DotNet451Release};
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  NeedsRestart := False;

  if IsDotNet451OrHigher() then
    Exit;

  Result := '.NET Framework 4.5.1 or newer is required (included with Windows Server 2012 R2). Setup will not install .NET 4.8. Please enable/install .NET 4.5.1 and retry.';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    ForceDirectories(ExpandConstant('{commonappdata}\IntranetSyncAgent'));
end;
