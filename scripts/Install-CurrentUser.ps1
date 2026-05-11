param(
    [string]$SourceDirectory = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string]$InstallDirectory = "$env:LOCALAPPDATA\Programs\CertExpiryMonitor"
)

$ErrorActionPreference = "Stop"

New-Item -ItemType Directory -Path $InstallDirectory -Force | Out-Null

$exe = Get-ChildItem -Path $SourceDirectory -Filter "CertExpiryMonitor.exe" -Recurse |
    Sort-Object FullName |
    Select-Object -First 1

if (-not $exe) {
    throw "CertExpiryMonitor.exe nao encontrado. Execute dotnet publish antes da instalacao."
}

Copy-Item -Path (Join-Path $exe.Directory.FullName "*") -Destination $InstallDirectory -Recurse -Force

$installedExe = Join-Path $InstallDirectory "CertExpiryMonitor.exe"

# O proprio aplicativo registra a inicializacao no Task Scheduler (ou HKCU\Run
# como fallback) ao iniciar pela primeira vez com a opcao StartupEnabled=true.
# Nao escrevemos HKCU\Run aqui para evitar entradas duplicadas.
Start-Process -FilePath $installedExe -ArgumentList "--background"

Write-Host "CertExpiryMonitor instalado em $InstallDirectory"
