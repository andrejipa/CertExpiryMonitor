<#
.SYNOPSIS
    Publica o CertExpiryMonitor e opcionalmente gera o instalador.

.DESCRIPTION
    Sincroniza a versao no .csproj e no .iss, compila com dotnet publish
    (self-contained, win-x64, single-file) e exige Inno Setup quando
    a geracao do instalador for solicitada.

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
$dotnet      = Join-Path $root ".dotnet-local\dotnet.exe"
if (-not (Test-Path $dotnet)) {
    $dotnet = "dotnet"
}

# Valida dependencias antes de alterar versoes ou remover o publish anterior.
$iscc = $null
if ($BuildInstaller) {
    $iscc = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
        "${env:LOCALAPPDATA}\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if (-not $iscc) {
        throw "Inno Setup 6 nao encontrado; -BuildInstaller exige ISCC.exe."
    }
}

Write-Host "=== CertExpiryMonitor Release: v$Version ===" -ForegroundColor Cyan

# -------------------------------------------------------------------------
# 1. Atualiza versao no .csproj
# -------------------------------------------------------------------------
Write-Host "[1/4] Atualizando versao em $csproj ..."

$csprojContent = Get-Content -LiteralPath $csproj -Raw -Encoding UTF8
$csprojContent = $csprojContent -replace '<Version>[^<]+</Version>',        "<Version>$Version</Version>"
$csprojContent = $csprojContent -replace '<AssemblyVersion>[^<]+</AssemblyVersion>', "<AssemblyVersion>$Version.0</AssemblyVersion>"
$csprojContent = $csprojContent -replace '<FileVersion>[^<]+</FileVersion>',  "<FileVersion>$Version.0</FileVersion>"
$csprojContent = $csprojContent -replace '<InformationalVersion>[^<]+</InformationalVersion>', "<InformationalVersion>$Version</InformationalVersion>"
Set-Content -Path $csproj -Value $csprojContent -NoNewline -Encoding UTF8

# -------------------------------------------------------------------------
# 2. Atualiza versao no .iss
# -------------------------------------------------------------------------
Write-Host "[2/4] Atualizando versao em $issFile ..."

$issContent = Get-Content -LiteralPath $issFile -Raw -Encoding UTF8
$issContent = $issContent -replace '#define MyAppVersion "[^"]+"', "#define MyAppVersion `"$Version`""
Set-Content -Path $issFile -Value $issContent -NoNewline -Encoding UTF8

Write-Host "      Versao sincronizada: $Version"

# -------------------------------------------------------------------------
# 3. dotnet publish
# -------------------------------------------------------------------------
Write-Host "[3/4] Publicando ..."

if (Test-Path -LiteralPath $publishDir) {
    $publishItem = Get-Item -LiteralPath $publishDir -Force
    $resolvedPublish = (Resolve-Path -LiteralPath $publishDir).Path
    $expectedPublish = [IO.Path]::GetFullPath((Join-Path $root "publish"))
    if (-not $publishItem.PSIsContainer -or
        ($publishItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        -not $resolvedPublish.Equals($expectedPublish, [StringComparison]::OrdinalIgnoreCase) -or
        -not ([IO.Path]::GetDirectoryName($resolvedPublish)).Equals($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Limpeza recusada: publish deve ser um diretorio real diretamente dentro do repositorio."
    }
    Remove-Item -LiteralPath $resolvedPublish -Recurse -Force
}

& $dotnet restore $csproj --locked-mode
if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore --locked-mode falhou com codigo $LASTEXITCODE"
}

$publishArgs = @(
    "publish", $csproj,
    "--configuration", "Release",
    "--runtime", "win-x64",
    "--self-contained", "true",
    "--no-restore",
    "/p:PublishSingleFile=true",
    "/p:IncludeNativeLibrariesForSelfExtract=true",
    "--output", $publishDir
)

& $dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish falhou com codigo $LASTEXITCODE"
}

Write-Host "      Publicado em: $publishDir"

# -------------------------------------------------------------------------
# 4. Inno Setup (opcional)
# -------------------------------------------------------------------------
if ($BuildInstaller) {
    Write-Host "[4/4] Compilando instalador ..."

    & $iscc $issFile
    if ($LASTEXITCODE -ne 0) {
        throw "ISCC falhou com codigo $LASTEXITCODE"
    }
    $installerPath = Join-Path $root "installer-output\CertExpiryMonitorSetup.exe"
    if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
        throw "ISCC terminou sem gerar o instalador esperado: $installerPath"
    }
    Write-Host "      Instalador gerado."
} else {
    Write-Host "[4/4] Ignorado (use -BuildInstaller para gerar o .exe do instalador)."
}

Write-Host ""
Write-Host "=== Publicacao v$Version concluida ===" -ForegroundColor Green
