; Inno Setup script for Intranet Sync Agent
; Target: Windows Server 2012+ / .NET Framework 4.8
; Bundles ndp48 offline installer and installs it when missing.
; Aliyun / API credentials are configured later in Admin UI.

#define MyAppName "Intranet Sync Agent"
#define MyAppVersion "1.1.6"
#define MyAppPublisher "Intranet Sync"
#define MyAppExeName "IntranetSync.Service.exe"
#define MyServiceName "IntranetSyncAgent"
#define MyAppURL "http://127.0.0.1:8787/"
; .NET Framework 4.8 Release DWORD (any OS): 528040+
#define DotNet48Release 528040
#define DotNetRedist "ndp48-x86-x64-allos-enu.exe"

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
; Bundled .NET 4.8 offline redist (extracted to {tmp} only when needed)
Source: "redist\{#DotNetRedist}"; Flags: dontcopy nocompression
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
function IsDotNet48OrHigher(): Boolean;
var
  Release: Cardinal;
begin
  Result := False;
  if RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) then
    Result := Release >= {#DotNet48Release};
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  InstallerPath: String;
begin
  Result := '';
  NeedsRestart := False;

  if IsDotNet48OrHigher() then
    Exit;

  WizardForm.StatusLabel.Caption := 'Installing .NET Framework 4.8 (one-time, may take several minutes)...';
  WizardForm.StatusLabel.Update;
  ExtractTemporaryFile('{#DotNetRedist}');
  InstallerPath := ExpandConstant('{tmp}\{#DotNetRedist}');

  if not FileExists(InstallerPath) then
  begin
    Result := '.NET Framework 4.8 redistributable was not found in the setup package.';
    Exit;
  end;

  { /passive shows progress; /norestart lets Inno handle reboot prompting }
  if not Exec(InstallerPath, '/passive /norestart /showrmui', '', SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode) then
  begin
    Result := 'Failed to start .NET Framework 4.8 installer.';
    Exit;
  end;

  { 0 = success; 1641 / 3010 = success, reboot required }
  if (ResultCode <> 0) and (ResultCode <> 1641) and (ResultCode <> 3010) then
  begin
    Result := '.NET Framework 4.8 installation failed (exit code ' + IntToStr(ResultCode) + ').';
    Exit;
  end;

  if (ResultCode = 1641) or (ResultCode = 3010) then
    NeedsRestart := True;

  if not IsDotNet48OrHigher() and not NeedsRestart then
  begin
    Result := '.NET Framework 4.8 is still not detected after installation.';
    Exit;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    ForceDirectories(ExpandConstant('{commonappdata}\IntranetSyncAgent'));
end;
