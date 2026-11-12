; Inno Setup script for Intranet Sync Agent
; Target: Windows Server 2012+ / .NET Framework 4.8
; Build with ISCC.exe after Release MSBuild (see build\build-installer.bat)
;
; Does NOT collect Aliyun / cloud credentials — configure later in Admin UI.

#define MyAppName "Intranet Sync Agent"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Intranet Sync"
#define MyAppExeName "IntranetSync.Service.exe"
#define MyServiceName "IntranetSyncAgent"
#define MyAppURL "http://127.0.0.1:8787/"

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
; Keep ProgramData config on uninstall (watermarks / encrypted secrets)
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
; Register Windows Service via InstallUtil (ProjectInstaller); fallback sc create in code
Filename: "{cmd}"; Parameters: "/c ""{app}\InstallService.cmd"""; StatusMsg: "Registering and starting Windows Service..."; Flags: runhidden waituntilterminated
Filename: "{app}\OpenAdmin.cmd"; Description: "Open Admin UI now"; Flags: postinstall nowait skipifsilent unchecked

[UninstallRun]
Filename: "{cmd}"; Parameters: "/c ""{app}\UninstallService.cmd"""; RunOnceId: "StopUninstallService"; Flags: runhidden waituntilterminated

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
  if not FileExists(ExpandConstant('{win}\Microsoft.NET\Framework\v4.0.30319\mscorlib.dll'))
     and not FileExists(ExpandConstant('{win}\Microsoft.NET\Framework64\v4.0.30319\mscorlib.dll')) then
  begin
    MsgBox('.NET Framework 4.x runtime was not detected.'#13#10 +
           'Install .NET Framework 4.8 before continuing.', mbError, MB_OK);
    Result := False;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    // Ensure data directory exists; no credentials written here.
    ForceDirectories(ExpandConstant('{commonappdata}\IntranetSyncAgent'));
  end;
end;
