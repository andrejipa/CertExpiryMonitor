#define MyAppName "CertExpiryMonitor"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Escritorio"
#define MyAppExeName "CertExpiryMonitor.exe"

[Setup]
AppId={{7E2D29E5-7F71-4CF3-9D57-C4F3C4475D42}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\installer-output
OutputBaseFilename=CertExpiryMonitorSetup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes
CloseApplicationsFilter={#MyAppExeName}
RestartApplications=no

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "startmenu"; Description: "Criar atalho no Menu Iniciar"; GroupDescription: "Atalhos:"; Flags: checkedonce

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--background"; Tasks: startmenu
Name: "{group}\Desinstalar {#MyAppName}"; Filename: "{uninstallexe}"; Tasks: startmenu

[Run]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--background"; Description: "Iniciar {#MyAppName}"; Flags: nowait postinstall skipifsilent
Filename: "{app}\{#MyAppExeName}"; Parameters: "--background"; Flags: nowait runhidden skipifnotsilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C taskkill /IM ""{#MyAppExeName}"" >NUL 2>NUL & exit /B 0"; Flags: runhidden; RunOnceId: "StopCertExpiryMonitor"
Filename: "{cmd}"; Parameters: "/C schtasks /delete /tn ""{#MyAppName}"" /f >NUL 2>NUL & exit /B 0"; Flags: runhidden; RunOnceId: "RemoveCertExpiryMonitorTask"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
Type: files; Name: "{userprograms}\CertExpiryMonitor.lnk"

[Code]
procedure StopInstalledApp();
var
  ResultCode: Integer;
begin
  Exec(
    ExpandConstant('{cmd}'),
    '/C taskkill /IM "{#MyAppExeName}" /F >NUL 2>NUL & exit /B 0',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);
  Sleep(1000);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopInstalledApp();
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    StopInstalledApp();
end;
