<#
.SYNOPSIS
    Publica o CertExpiryMonitor e opcionalmente gera o instalador.

.DESCRIPTION
    Sincroniza a versao no .csproj e no .iss, compila com dotnet publish
    (self-contained, win-x64, single-file) e, se o Inno Setup estiver
    disponivel, gera o instalador.

.PARAMETER Version
    Versao semantica ex: "1.2.3". Obrigatorio.

.PARAMETER BuildInstaller
    Se presente, chama o Inno Setup para gerar CertExpiryMonitorSetup.exe.

.EXAMPLE
    .\Publish-Release.ps1 -Version "1.1.0"
    .\Publish-Release.ps1 -Version "1.1.0" -BuildInstaller
#>

param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [switch]$BuildInstaller
)

$ErrorActionPreference = "Stop"

$root        = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$csproj      = Join-Path $root "CertExpiryMonitor.csproj"
$issFile     = Join-Path $root "installer\CertExpiryMonitor.iss"
$publishDir  = Join-Path $root "publish"

Write-Host "=== CertExpiryMonitor Release: v$Version ===" -ForegroundColor Cyan

# -------------------------------------------------------------------------
# 1. Atualiza versao no .csproj
# -------------------------------------------------------------------------
Write-Host "[1/4] Atualizando versao em $csproj ..."

$csprojContent = Get-Content $csproj -Raw
$csprojContent = $csprojContent -replace '<Version>[^<]+</Version>',        "<Version>$Version</Version>"
$csprojContent = $csprojContent -replace '<AssemblyVersion>[^<]+</AssemblyVersion>', "<AssemblyVersion>$Version.0</AssemblyVersion>"
$csprojContent = $csprojContent -replace '<FileVersion>[^<]+</FileVersion>',  "<FileVersion>$Version.0</FileVersion>"
$csprojContent = $csprojContent -replace '<InformationalVersion>[^<]+</InformationalVersion>', "<InformationalVersion>$Version</InformationalVersion>"
Set-Content -Path $csproj -Value $csprojContent -NoNewline

# -------------------------------------------------------------------------
# 2. Atualiza versao no .iss
# -------------------------------------------------------------------------
Write-Host "[2/4] Atualizando versao em $issFile ..."

$issContent = Get-Content $issFile -Raw
$issContent = $issContent -replace '#define MyAppVersion "[^"]+"', "#define MyAppVersion `"$Version`""
Set-Content -Path $issFile -Value $issContent -NoNewline

Write-Host "      Versao sincronizada: $Version"

# -------------------------------------------------------------------------
# 3. dotnet publish
# -------------------------------------------------------------------------
Write-Host "[3/4] Publicando ..."

if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}

$publishArgs = @(
    "publish", $csproj,
    "--configuration", "Release",
    "--runtime", "win-x64",
    "--self-contained", "true",
    "/p:PublishSingleFile=true",
    "/p:IncludeNativeLibrariesForSelfExtract=true",
    "--output", $publishDir
)

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish falhou com codigo $LASTEXITCODE"
}

Write-Host "      Publicado em: $publishDir"

# -------------------------------------------------------------------------
# 4. Inno Setup (opcional)
# -------------------------------------------------------------------------
if ($BuildInstaller) {
    Write-Host "[4/4] Compilando instalador ..."

    $iscc = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1

    if (-not $iscc) {
        Write-Warning "Inno Setup nao encontrado. Pule -BuildInstaller ou instale o Inno Setup 6."
    } else {
        & $iscc $issFile
        if ($LASTEXITCODE -ne 0) {
            throw "ISCC falhou com codigo $LASTEXITCODE"
        }
        Write-Host "      Instalador gerado."
    }
} else {
    Write-Host "[4/4] Ignorado (use -BuildInstaller para gerar o .exe do instalador)."
}

Write-Host ""
Write-Host "=== Publicacao v$Version concluida ===" -ForegroundColor Green
