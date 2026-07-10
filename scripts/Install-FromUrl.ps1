param(
    [Parameter(Mandatory = $true)]
    [string]$InstallerUrl,

    [string]$Sha256 = "",

    [switch]$AllowInsecureUrl,

    [switch]$SkipHashValidation,

    [switch]$Silent
)

$ErrorActionPreference = "Stop"

$appName = "CertExpiryMonitor"
$installDir = Join-Path $env:LOCALAPPDATA "Programs\$appName"
$exePath = Join-Path $installDir "$appName.exe"
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$tempDir = Join-Path $tempRoot "$appName-install-$([Guid]::NewGuid().ToString('N'))"
$installerPath = Join-Path $tempDir "$appName-Setup.exe"

function Invoke-InstallerProcess([string]$FilePath, [string]$Arguments) {
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.Arguments = $Arguments
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true

    $process = [Diagnostics.Process]::Start($startInfo)
    if (-not $process.WaitForExit(120000)) {
        try { $process.Kill($true) } catch { try { $process.Kill() } catch { } }
        throw "Instalador excedeu 120 segundos."
    }

    if ($process.ExitCode -ne 0) {
        throw "Instalador retornou codigo $($process.ExitCode)"
    }
}

function Get-AppProcesses {
    @(Get-Process -Name $appName -ErrorAction SilentlyContinue | ForEach-Object {
        $path = $null
        try { $path = $_.Path } catch { }
        [pscustomobject]@{
            Id = $_.Id
            Path = $path
        }
    })
}

function Test-ProcessPathEquals([string]$Actual, [string]$Expected) {
    -not [string]::IsNullOrWhiteSpace($Actual) -and
        $Actual.Equals($Expected, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-NoForeignAppProcess([string]$ExecutablePath) {
    $foreign = @(Get-AppProcesses |
        Where-Object { -not (Test-ProcessPathEquals $_.Path $ExecutablePath) })
    if ($foreign.Count -gt 0) {
        $list = ($foreign | ForEach-Object {
            if ($_.Path) { " - PID $($_.Id): $($_.Path)" } else { " - PID $($_.Id): caminho indisponivel" }
        }) -join [Environment]::NewLine
        throw "Outra instancia de $appName esta rodando fora da instalacao atual. Feche-a antes de instalar:$([Environment]::NewLine)$list"
    }
}

function Stop-InstalledAppProcessAndWait([string]$ExecutablePath) {
    Get-AppProcesses |
        Where-Object { Test-ProcessPathEquals $_.Path $ExecutablePath } |
        ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }

    $deadline = (Get-Date).AddSeconds(10)
    do {
        $remaining = @(Get-AppProcesses |
            Where-Object { Test-ProcessPathEquals $_.Path $ExecutablePath })
        if ($remaining.Count -eq 0) {
            return
        }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)

    throw "Nao foi possivel encerrar a instancia instalada antes de executar o instalador: $ExecutablePath"
}

$installerUri = $null
if (-not [Uri]::TryCreate($InstallerUrl, [UriKind]::Absolute, [ref]$installerUri)) {
    throw "InstallerUrl deve ser uma URL absoluta."
}

if ($installerUri.Scheme -ne "https" -and -not $AllowInsecureUrl) {
    throw "InstallerUrl deve usar HTTPS. Para HTTP/file/UNC, execute novamente com -AllowInsecureUrl."
}

if ($installerUri.Scheme -ne "https" -and $AllowInsecureUrl -and $SkipHashValidation) {
    throw "Nao combine -AllowInsecureUrl com -SkipHashValidation. Em URL insegura, informe -Sha256."
}

$normalizedSha256 = $Sha256.Trim()
if ([string]::IsNullOrWhiteSpace($normalizedSha256) -and -not $SkipHashValidation) {
    throw "Sha256 e obrigatorio. Para instalar sem validar hash, execute novamente com -SkipHashValidation."
}

if (-not [string]::IsNullOrWhiteSpace($normalizedSha256) -and $normalizedSha256 -notmatch '^[0-9a-fA-F]{64}$') {
    throw "Sha256 deve conter exatamente 64 caracteres hexadecimais."
}

try {
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

    Write-Host "Baixando instalador..."
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $installerUri -OutFile $installerPath -UseBasicParsing

    if (-not [string]::IsNullOrWhiteSpace($normalizedSha256)) {
        $actualHash = (Get-FileHash -Path $installerPath -Algorithm SHA256).Hash
        if ($actualHash -ne $normalizedSha256.ToUpperInvariant()) {
            throw "Hash SHA256 invalido. Esperado: $normalizedSha256. Obtido: $actualHash"
        }
    }
    else {
        # Uso legado/inseguro: permitido apenas por decisao explicita do operador.
        Write-Warning "Instalando sem validar SHA256 por causa de -SkipHashValidation."
    }

    Write-Host "Fechando versao em execucao, se houver..."
    Assert-NoForeignAppProcess $exePath
    Stop-InstalledAppProcessAndWait $exePath

    Write-Host "Instalando..."
    $arguments = if ($Silent) { "/verysilent /suppressmsgboxes /norestart" } else { "/silent /suppressmsgboxes /norestart" }
    Invoke-InstallerProcess -FilePath $installerPath -Arguments $arguments

    if (-not (Test-Path $exePath)) {
        throw "Aplicativo nao encontrado apos instalacao: $exePath"
    }

    $installedProcess = @(Get-AppProcesses | Where-Object { Test-ProcessPathEquals $_.Path $exePath })
    $otherProcesses = @(Get-AppProcesses | Where-Object { -not (Test-ProcessPathEquals $_.Path $exePath) })
    if ($installedProcess.Count -eq 0 -and $otherProcesses.Count -gt 0) {
        $paths = ($otherProcesses | ForEach-Object { if ($_.Path) { $_.Path } else { "PID $($_.Id) sem Path" } }) -join "; "
        throw "Outra instancia de $appName esta rodando fora da instalacao atual: $paths"
    }

    if ($installedProcess.Count -eq 0) {
        Start-Process -FilePath $exePath -ArgumentList "--background"
    }

    Write-Host "Instalacao concluida: $exePath"
}
finally {
    $tempDirFull = [IO.Path]::GetFullPath($tempDir)
    $tempRootWithSeparator = $tempRoot.TrimEnd('\') + '\'
    $tempLeaf = Split-Path -Leaf $tempDirFull
    if ($tempDirFull.StartsWith($tempRootWithSeparator, [StringComparison]::OrdinalIgnoreCase) -and
        $tempLeaf.StartsWith("$appName-install-", [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $tempDirFull)) {
        # Remove apenas a pasta temporaria criada por esta execucao.
        Remove-Item -LiteralPath $tempDirFull -Recurse -Force -ErrorAction SilentlyContinue
    }
}
