param(
    [Parameter(Mandatory = $true)]
    [string]$InstallerUrl,

    [string]$Sha256 = "",

    [switch]$Silent
)

$ErrorActionPreference = "Stop"

$appName = "CertExpiryMonitor"
$installDir = Join-Path $env:LOCALAPPDATA "Programs\$appName"
$exePath = Join-Path $installDir "$appName.exe"
$tempDir = Join-Path $env:TEMP "$appName-install"
$installerPath = Join-Path $tempDir "$appName-Setup.exe"

New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

Write-Host "Baixando instalador..."
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Invoke-WebRequest -Uri $InstallerUrl -OutFile $installerPath -UseBasicParsing

if (-not [string]::IsNullOrWhiteSpace($Sha256)) {
    $actualHash = (Get-FileHash -Path $installerPath -Algorithm SHA256).Hash
    if ($actualHash -ne $Sha256.ToUpperInvariant()) {
        throw "Hash SHA256 invalido. Esperado: $Sha256. Obtido: $actualHash"
    }
}

Write-Host "Fechando versao em execucao, se houver..."
Get-Process -Name $appName -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $exePath } |
    Stop-Process -Force -ErrorAction SilentlyContinue

Write-Host "Instalando..."
$arguments = if ($Silent) { "/verysilent /suppressmsgboxes" } else { "/silent /suppressmsgboxes" }
$process = Start-Process -FilePath $installerPath -ArgumentList $arguments -Wait -PassThru
if ($process.ExitCode -ne 0) {
    throw "Instalador retornou codigo $($process.ExitCode)"
}

if (-not (Test-Path $exePath)) {
    throw "Aplicativo nao encontrado apos instalacao: $exePath"
}

if (-not (Get-Process -Name $appName -ErrorAction SilentlyContinue)) {
    Start-Process -FilePath $exePath -ArgumentList "--background"
}

Write-Host "Instalacao concluida: $exePath"
