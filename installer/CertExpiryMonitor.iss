#define MyAppName "CertExpiryMonitor"
#define MyAppVersion "1.0.11"
#define MyAppPublisher "Escritorio"
#define MyAppExeName "CertExpiryMonitor.exe"

[Setup]
AppId={{7E2D29E5-7F71-4CF3-9D57-C4F3C4475D42}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableDirPage=yes
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
; Startup e aplicado pelo app conforme StartupEnabled, apenas apos leitura valida.
; Nao registrar tarefa aqui: atualizacoes devem preservar startup desligado.
; Se o usuario desmarcar Iniciar agora, a preferencia sera aplicada na proxima abertura.

; Iniciar o app apos instalacao (interativa OU silent).
Filename: "{app}\{#MyAppExeName}"; Parameters: "--background"; Description: "Iniciar {#MyAppName}"; Flags: nowait postinstall skipifsilent
Filename: "{app}\{#MyAppExeName}"; Parameters: "--background"; Flags: nowait runhidden skipifnotsilent

[UninstallRun]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Get-Process -Name '{#MyAppName}' -ErrorAction SilentlyContinue | Where-Object {{ $_.Path -ieq '{app}\{#MyAppExeName}' } | Stop-Process -Force -ErrorAction SilentlyContinue"""; Flags: runhidden; RunOnceId: "StopCertExpiryMonitor"
Filename: "{cmd}"; Parameters: "/C schtasks /delete /tn ""{#MyAppName}"" /f >NUL 2>NUL & exit /B 0"; Flags: runhidden; RunOnceId: "RemoveCertExpiryMonitorTask"
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Microsoft\Windows\CurrentVersion\Run"" /v ""{#MyAppName}"" /f >NUL 2>NUL & exit /B 0"; Flags: runhidden; RunOnceId: "RemoveCertExpiryMonitorRun"
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Classes\cert-expiry-monitor"" /f >NUL 2>NUL & exit /B 0"; Flags: runhidden; RunOnceId: "RemoveCertExpiryMonitorProtocol"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
Type: files; Name: "{userprograms}\CertExpiryMonitor.lnk"

[Code]
procedure StopInstalledApp();
var
  ResultCode: Integer;
begin
  Exec(
    'powershell.exe',
    '-NoProfile -ExecutionPolicy Bypass -Command "Get-Process -Name ''{#MyAppName}'' -ErrorAction SilentlyContinue | Where-Object { $_.Path -ieq ''' + ExpandConstant('{app}\{#MyAppExeName}') + ''' } | Stop-Process -Force -ErrorAction SilentlyContinue"',
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
