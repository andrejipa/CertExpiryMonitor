param(
    [string]$SourceDirectory = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string]$InstallDirectory = "$env:LOCALAPPDATA\Programs\CertExpiryMonitor"
)

$ErrorActionPreference = "Stop"

function Assert-SafeInstallDirectory([string]$Path) {
    $programsRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA "Programs"))
    $expected = [IO.Path]::GetFullPath((Join-Path $programsRoot "CertExpiryMonitor"))
    $candidate = [IO.Path]::GetFullPath($Path)
    $programsRootWithSeparator = $programsRoot.TrimEnd('\') + '\'

    if (-not $candidate.StartsWith($programsRootWithSeparator, [StringComparison]::OrdinalIgnoreCase)) {
        throw "InstallDirectory deve ficar dentro de $programsRoot"
    }

    if (-not $candidate.Equals($expected, [StringComparison]::OrdinalIgnoreCase)) {
        throw "InstallDirectory deve ser exatamente $expected"
    }

    return $candidate
}

function Test-ProcessPathEquals([string]$Actual, [string]$Expected) {
    -not [string]::IsNullOrWhiteSpace($Actual) -and
        $Actual.Equals($Expected, [StringComparison]::OrdinalIgnoreCase)
}

function Get-AppProcesses {
    @(Get-Process -Name "CertExpiryMonitor" -ErrorAction SilentlyContinue | ForEach-Object {
        $path = $null
        try { $path = $_.Path } catch { }
        [pscustomobject]@{
            Id = $_.Id
            Path = $path
        }
    })
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

    throw "Nao foi possivel encerrar a instancia instalada antes de copiar arquivos: $ExecutablePath"
}

function Assert-NoForeignAppProcess([string]$ExecutablePath) {
    $foreign = @(Get-AppProcesses |
        Where-Object { -not (Test-ProcessPathEquals $_.Path $ExecutablePath) })
    if ($foreign.Count -gt 0) {
        $list = ($foreign | ForEach-Object { " - PID $($_.Id): $($_.Path)" }) -join [Environment]::NewLine
        throw "Outra instancia de CertExpiryMonitor esta rodando fora da instalacao atual. Feche-a antes de instalar:$([Environment]::NewLine)$list"
    }
}

function Resolve-SourceExecutable([string]$Root) {
    $directExe = Join-Path $Root "CertExpiryMonitor.exe"
    if (Test-Path -LiteralPath $directExe) {
        return Get-Item -LiteralPath $directExe
    }

    $publishExe = Join-Path $Root "publish\CertExpiryMonitor.exe"
    if (Test-Path -LiteralPath $publishExe) {
        return Get-Item -LiteralPath $publishExe
    }

    $candidates = @(Get-ChildItem -Path $Root -Filter "CertExpiryMonitor.exe" -Recurse |
        Where-Object {
            $_.FullName -notmatch '\\bin\\Debug\\' -and
            $_.FullName -notmatch '\\obj\\' -and
            $_.FullName -notmatch '\\installer-output\\'
        } |
        Sort-Object FullName)

    if ($candidates.Count -eq 1) {
        return $candidates[0]
    }

    if ($candidates.Count -gt 1) {
        $list = ($candidates | ForEach-Object { " - $($_.FullName)" }) -join [Environment]::NewLine
        throw "Mais de um CertExpiryMonitor.exe encontrado. Informe -SourceDirectory apontando para a pasta publish:$([Environment]::NewLine)$list"
    }

    return $null
}

$exe = Resolve-SourceExecutable $SourceDirectory
$InstallDirectory = Assert-SafeInstallDirectory $InstallDirectory

if (-not $exe) {
    throw "CertExpiryMonitor.exe nao encontrado. Execute dotnet publish antes da instalacao."
}

New-Item -ItemType Directory -Path $InstallDirectory -Force | Out-Null
$installedExe = Join-Path $InstallDirectory "CertExpiryMonitor.exe"
Assert-NoForeignAppProcess $installedExe
Stop-InstalledAppProcessAndWait $installedExe
Copy-Item -Path (Join-Path $exe.Directory.FullName "*") -Destination $InstallDirectory -Recurse -Force

# O proprio aplicativo registra a inicializacao no Task Scheduler (ou HKCU\Run
# como fallback) ao iniciar pela primeira vez com a opcao StartupEnabled=true.
# Nao escrevemos HKCU\Run aqui para evitar entradas duplicadas.
Start-Process -FilePath $installedExe -ArgumentList "--background"

Write-Host "CertExpiryMonitor instalado em $InstallDirectory"
