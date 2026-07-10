param(
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

    if ((Test-Path -LiteralPath $candidate) -and
        -not (Test-Path -LiteralPath (Join-Path $candidate "CertExpiryMonitor.exe")) -and
        -not (Test-Path -LiteralPath (Join-Path $candidate "unins000.exe"))) {
        throw "InstallDirectory nao parece conter uma instalacao do CertExpiryMonitor: $candidate"
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

    throw "Nao foi possivel encerrar a instancia instalada antes de remover arquivos: $ExecutablePath"
}

$InstallDirectory = Assert-SafeInstallDirectory $InstallDirectory
$installedExe = Join-Path $InstallDirectory "CertExpiryMonitor.exe"

# Remove a tarefa agendada criada pelo aplicativo (preferido sobre HKCU\Run).
schtasks.exe /delete /tn "CertExpiryMonitor" /f 2>$null | Out-Null

# Remove tambem a entrada HKCU\Run para compatibilidade com versoes antigas.
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
Remove-ItemProperty -Path $runKey -Name "CertExpiryMonitor" -ErrorAction SilentlyContinue

# Remove o protocolo usado pelos toasts para abrir a janela de detalhes.
Remove-Item -Path "HKCU:\Software\Classes\cert-expiry-monitor" -Recurse -Force -ErrorAction SilentlyContinue

$toastShortcut = Join-Path ([Environment]::GetFolderPath("StartMenu")) "Programs\CertExpiryMonitor.lnk"
Remove-Item -LiteralPath $toastShortcut -Force -ErrorAction SilentlyContinue

Stop-InstalledAppProcessAndWait $installedExe

if (Test-Path $InstallDirectory) {
    Remove-Item -LiteralPath $InstallDirectory -Recurse -Force
}

Write-Host "CertExpiryMonitor removido do usuario atual."
