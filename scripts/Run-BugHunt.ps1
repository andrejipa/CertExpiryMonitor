param(
    [switch]$Maximum,
    [switch]$KeepArtifacts,
    [string]$DotNetPath,
    [string]$ArtifactsRoot
)

$ErrorActionPreference = "Stop"
if (Get-Variable -Name PSNativeCommandUseErrorActionPreference -Scope Global -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}

if (-not $Maximum) {
    throw "Esta rodada altera estado real do Windows. Execute com -Maximum para confirmar."
}

# O provider Cert: nao e carregado automaticamente em todas as sessoes nao interativas.
if ($null -eq (Get-PSDrive -Name Cert -ErrorAction SilentlyContinue)) {
    Import-Module Microsoft.PowerShell.Security -ErrorAction SilentlyContinue
}
if ($null -eq (Get-PSDrive -Name Cert -ErrorAction SilentlyContinue)) {
    throw "Provider Cert: indisponivel nesta sessao PowerShell."
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($DotNetPath)) {
    $DotNetPath = Join-Path $repoRoot ".dotnet-local\dotnet.exe"
}
if ([string]::IsNullOrWhiteSpace($ArtifactsRoot)) {
    $ArtifactsRoot = Join-Path $repoRoot "artifacts\bughunt"
}
if (-not (Test-Path $DotNetPath)) {
    throw "dotnet nao encontrado em $DotNetPath"
}

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$artifactDir = Join-Path $ArtifactsRoot $timestamp
$snapshotDir = Join-Path $artifactDir "snapshot"
$publishDir = Join-Path $artifactDir "publish"
$captureRoot = Join-Path $artifactDir "captures"
$logPath = Join-Path $artifactDir "bughunt.log"

$appName = "CertExpiryMonitor"
$taskName = "CertExpiryMonitor"
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$runValueName = "CertExpiryMonitor"
$subject = "CN=Codex BugHunt CertExpiryMonitor"
$localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$appData = [Environment]::GetFolderPath([Environment+SpecialFolder]::ApplicationData)
$dataDir = Join-Path $localAppData "CertExpiryMonitor"
$installDir = Join-Path $localAppData "Programs\CertExpiryMonitor-BugHunt"
$shortcutPath = Join-Path $appData "Microsoft\Windows\Start Menu\Programs\CertExpiryMonitor.lnk"
$protocolRegistryPath = "HKCU:\Software\Classes\cert-expiry-monitor"
$protocolRegExePath = "HKCU\Software\Classes\cert-expiry-monitor"
$taskXmlPath = Join-Path $snapshotDir "CertExpiryMonitor-task.xml"
$runValuePath = Join-Path $snapshotDir "HKCU-Run-CertExpiryMonitor.txt"
$shortcutBackupPath = Join-Path $snapshotDir "CertExpiryMonitor.lnk"
$protocolBackupPath = Join-Path $snapshotDir "cert-expiry-monitor-protocol.reg"
$dataBackupDir = Join-Path $snapshotDir "CertExpiryMonitor-data"
$createdThumbprints = New-Object System.Collections.Generic.List[string]
$existingProcesses = @()
$taskExisted = $false
$runValueExisted = $false
$runValue = $null
$shortcutExisted = $false
$protocolExisted = $false
$dataExisted = $false

New-Item -ItemType Directory -Path $artifactDir, $snapshotDir, $publishDir, $captureRoot -Force | Out-Null
Start-Transcript -Path $logPath -Force | Out-Null

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class BugHuntNativeMethods {
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
'@ -ErrorAction SilentlyContinue

function Write-Step([string]$Message) {
    Write-Host ""
    Write-Host "== $Message =="
}

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) {
        throw $Message
    }
}

function Test-ContainsIgnoreCase([string]$Text, [string]$Needle) {
    if ($null -eq $Text) {
        return $false
    }

    return $Text.IndexOf($Needle, [System.StringComparison]::OrdinalIgnoreCase) -ge 0
}

function Invoke-External([string]$FilePath, [string[]]$ArgumentList, [string]$Description) {
    Write-Step $Description
    Write-Host "$FilePath $($ArgumentList -join ' ')"
    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "$Description falhou com exit code $LASTEXITCODE"
    }
}

function Invoke-Schtasks([string[]]$ArgumentList) {
    $oldErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = & schtasks.exe @ArgumentList 2>$null
        return [pscustomobject]@{
            ExitCode = $LASTEXITCODE
            Output = $output
        }
    }
    finally {
        $ErrorActionPreference = $oldErrorActionPreference
    }
}

function Get-AppProcesses {
    @(Get-CimInstance Win32_Process -Filter "Name='CertExpiryMonitor.exe'" -ErrorAction SilentlyContinue |
        Select-Object ProcessId, ExecutablePath, CommandLine)
}

function Stop-AppProcesses {
    Get-Process -Name $appName -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue

    $deadline = (Get-Date).AddSeconds(10)
    do {
        $remaining = @(Get-Process -Name $appName -ErrorAction SilentlyContinue)
        if ($remaining.Count -eq 0) {
            return
        }

        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)

    $remainingIds = (@(Get-Process -Name $appName -ErrorAction SilentlyContinue).Id -join ", ")
    throw "Nao foi possivel encerrar processos $appName antes do cleanup. PIDs: $remainingIds"
}

function Get-OriginalArguments([string]$CommandLine, [string]$ExecutablePath) {
    if ([string]::IsNullOrWhiteSpace($CommandLine)) {
        return ""
    }

    $trimmed = $CommandLine.Trim()
    $quotedExecutable = '"' + $ExecutablePath + '"'
    if ($trimmed.StartsWith($quotedExecutable, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $trimmed.Substring($quotedExecutable.Length).Trim()
    }

    if ($trimmed.StartsWith($ExecutablePath, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $trimmed.Substring($ExecutablePath.Length).Trim()
    }

    if ($trimmed.StartsWith('"')) {
        $closingQuote = $trimmed.IndexOf('"', 1)
        if ($closingQuote -ge 0 -and $closingQuote + 1 -lt $trimmed.Length) {
            return $trimmed.Substring($closingQuote + 1).Trim()
        }
        return ""
    }

    $firstSpace = $trimmed.IndexOf(' ')
    if ($firstSpace -lt 0) {
        return ""
    }

    return $trimmed.Substring($firstSpace + 1).Trim()
}

function Test-IsUnderDirectory([string]$Path, [string]$Directory) {
    $resolvedPath = [IO.Path]::GetFullPath($Path)
    $resolvedDirectory = [IO.Path]::GetFullPath($Directory).TrimEnd('\')
    $resolvedDirectoryWithSeparator = $resolvedDirectory + '\'
    return $resolvedPath.StartsWith($resolvedDirectoryWithSeparator, [System.StringComparison]::OrdinalIgnoreCase)
}

function Test-ProcessPathEquals([string]$Actual, [string]$Expected) {
    -not [string]::IsNullOrWhiteSpace($Actual) -and
        -not [string]::IsNullOrWhiteSpace($Expected) -and
        [IO.Path]::GetFullPath($Actual).Equals([IO.Path]::GetFullPath($Expected), [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-ShortcutTarget([string]$Path) {
    try {
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($Path)
        return [string]$shortcut.TargetPath
    }
    catch {
        return $null
    }
    finally {
        if ($shortcut) {
            [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) | Out-Null
        }
        if ($shell) {
            [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
        }
    }
}

function Get-ShortcutArguments([string]$Path) {
    try {
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($Path)
        return [string]$shortcut.Arguments
    }
    catch {
        return $null
    }
    finally {
        if ($shortcut) {
            [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) | Out-Null
        }
        if ($shell) {
            [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
        }
    }
}

function Get-ShortcutExtendedProperty([string]$Path, [string]$PropertyName) {
    try {
        $shell = New-Object -ComObject Shell.Application
        $folder = $shell.Namespace((Split-Path $Path -Parent))
        if (-not $folder) {
            return $null
        }

        $item = $folder.ParseName((Split-Path $Path -Leaf))
        if (-not $item) {
            return $null
        }

        return [string]$item.ExtendedProperty($PropertyName)
    }
    catch {
        return $null
    }
    finally {
        if ($item) {
            [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($item) | Out-Null
        }
        if ($folder) {
            [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($folder) | Out-Null
        }
        if ($shell) {
            [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
        }
    }
}

function Remove-DirectoryUnder([string]$Path, [string]$AllowedParent) {
    if (-not (Test-Path $Path)) {
        return
    }

    $resolvedPath = [IO.Path]::GetFullPath((Resolve-Path $Path).Path)
    $resolvedParent = [IO.Path]::GetFullPath((Resolve-Path $AllowedParent).Path)
    $resolvedParentWithSeparator = $resolvedParent.TrimEnd('\') + '\'
    if ($resolvedPath.Equals($resolvedParent.TrimEnd('\'), [System.StringComparison]::OrdinalIgnoreCase) -or
        -not $resolvedPath.StartsWith($resolvedParentWithSeparator, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Recusando remover '$resolvedPath' fora de '$resolvedParent'"
    }

    $lastError = $null
    for ($attempt = 1; $attempt -le 8; $attempt++) {
        try {
            Remove-Item -LiteralPath $resolvedPath -Recurse -Force
            return
        }
        catch {
            $lastError = $_
            Start-Sleep -Milliseconds (200 * $attempt)
        }
    }

    throw $lastError
}

function Save-Snapshot {
    Write-Step "Snapshot do estado atual"

    $script:existingProcesses = Get-AppProcesses
    foreach ($process in $script:existingProcesses) {
        if ([string]::IsNullOrWhiteSpace($process.ExecutablePath)) {
            throw "Processo CertExpiryMonitor existente sem ExecutablePath; abortando antes de alterar estado."
        }
    }
    $script:existingProcesses | ConvertTo-Json -Depth 4 |
        Set-Content -Path (Join-Path $snapshotDir "processes.json") -Encoding UTF8

    $taskQuery = Invoke-Schtasks @("/query", "/tn", $taskName, "/xml")
    $taskXml = $taskQuery.Output
    if ($taskQuery.ExitCode -eq 0 -and $taskXml) {
        if (Test-ContainsIgnoreCase ($taskXml | Out-String) $installDir) {
            Write-Warning "Removendo tarefa residual de BugHunt antes do snapshot."
            Invoke-Schtasks @("/delete", "/tn", $taskName, "/f") | Out-Null
        }
        else {
            $script:taskExisted = $true
            $taskXml | Set-Content -Path $taskXmlPath -Encoding Unicode
        }
    }

    if (Test-Path $runKey) {
        $item = Get-ItemProperty -Path $runKey -Name $runValueName -ErrorAction SilentlyContinue
        $property = $item.PSObject.Properties[$runValueName]
        if ($property) {
            $script:runValueExisted = $true
            $script:runValue = [string]$property.Value
            $script:runValue | Set-Content -Path $runValuePath -Encoding UTF8
        }
    }

    if (Test-Path $shortcutPath) {
        $shortcutTarget = Get-ShortcutTarget $shortcutPath
        if (-not [string]::IsNullOrWhiteSpace($shortcutTarget) -and
            (Test-IsUnderDirectory $shortcutTarget $installDir)) {
            Write-Warning "Removendo atalho residual de BugHunt antes do snapshot."
            Remove-Item -LiteralPath $shortcutPath -Force -ErrorAction SilentlyContinue
        }
        else {
            $script:shortcutExisted = $true
            Copy-Item -LiteralPath $shortcutPath -Destination $shortcutBackupPath -Force
        }
    }

    if (Test-Path $protocolRegistryPath) {
        $script:protocolExisted = $true
        & reg.exe export $protocolRegExePath $protocolBackupPath /y | Out-Null
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $protocolBackupPath)) {
            throw "Falha ao exportar protocolo existente $protocolRegExePath"
        }
    }

    if (Test-Path $dataDir) {
        $script:dataExisted = $true
        Copy-Item -LiteralPath $dataDir -Destination $dataBackupDir -Recurse -Force
    }

    $existingBugHuntCerts = @(Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $subject })
    if ($existingBugHuntCerts.Count -gt 0) {
        throw "Ja existe certificado com subject '$subject'. Remova manualmente antes da rodada."
    }
}

function Restore-Snapshot {
    Write-Step "Cleanup e restauracao"

    Stop-AppProcesses

    foreach ($thumbprint in $createdThumbprints) {
        $certPath = "Cert:\CurrentUser\My\$thumbprint"
        if (Test-Path $certPath) {
            Remove-Item -LiteralPath $certPath -Force -ErrorAction SilentlyContinue
        }
    }

    Remove-DirectoryUnder -Path $installDir -AllowedParent (Join-Path $localAppData "Programs")

    Invoke-Schtasks @("/delete", "/tn", $taskName, "/f") | Out-Null
    if ($taskExisted -and (Test-Path $taskXmlPath)) {
        $taskCreate = Invoke-Schtasks @("/create", "/tn", $taskName, "/xml", $taskXmlPath, "/f")
        if ($taskCreate.ExitCode -ne 0) {
            Write-Warning "Falha ao restaurar tarefa agendada original. XML preservado em $taskXmlPath"
        }
    }

    if ($runValueExisted) {
        if (-not (Test-Path $runKey)) {
            New-Item -Path $runKey -Force | Out-Null
        }
        New-ItemProperty -Path $runKey -Name $runValueName -Value $runValue -PropertyType String -Force | Out-Null
    }
    elseif (Test-Path $runKey) {
        Remove-ItemProperty -Path $runKey -Name $runValueName -ErrorAction SilentlyContinue
    }

    if ($shortcutExisted -and (Test-Path $shortcutBackupPath)) {
        New-Item -ItemType Directory -Path (Split-Path $shortcutPath -Parent) -Force | Out-Null
        try {
            Copy-Item -LiteralPath $shortcutBackupPath -Destination $shortcutPath -Force
        }
        catch {
            Write-Warning "Falha ao restaurar atalho original. Backup preservado em $shortcutBackupPath"
        }
    }
    elseif (Test-Path $shortcutPath) {
        Remove-Item -LiteralPath $shortcutPath -Force -ErrorAction SilentlyContinue
    }

    Remove-Item -Path $protocolRegistryPath -Recurse -Force -ErrorAction SilentlyContinue
    if ($protocolExisted -and (Test-Path $protocolBackupPath)) {
        & reg.exe import $protocolBackupPath | Out-Null
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Falha ao restaurar protocolo original. Backup preservado em $protocolBackupPath"
        }
    }

    Remove-DirectoryUnder -Path $dataDir -AllowedParent $localAppData
    if ($dataExisted -and (Test-Path $dataBackupDir)) {
        Copy-Item -LiteralPath $dataBackupDir -Destination $dataDir -Recurse -Force
    }

    foreach ($process in $existingProcesses) {
        if ([string]::IsNullOrWhiteSpace($process.ExecutablePath)) {
            continue
        }
        if (-not (Test-Path $process.ExecutablePath)) {
            Write-Warning "Nao foi possivel reiniciar processo original; exe ausente: $($process.ExecutablePath)"
            continue
        }
        if (Test-IsUnderDirectory $process.ExecutablePath $installDir) {
            continue
        }

        try {
            $arguments = Get-OriginalArguments $process.CommandLine $process.ExecutablePath
            if ([string]::IsNullOrWhiteSpace($arguments)) {
                Start-Process -FilePath $process.ExecutablePath | Out-Null
            }
            else {
                Start-Process -FilePath $process.ExecutablePath -ArgumentList $arguments | Out-Null
            }
        }
        catch {
            Write-Warning "Falha ao reiniciar processo original $($process.ExecutablePath): $_"
        }
    }

}

function Write-BugHuntSettings {
    New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
    $dailyCheck = (Get-Date).AddMinutes(-1).ToString("HH:mm:ss")
    $settingsPath = Join-Path $dataDir "settings.json"
    $settings = @"
{
  "version": 1,
  "settings": {
    "DailyCheckTime": "$dailyCheck",
    "InitialDelayMinutes": 1,
    "StartupEnabled": true,
    "NotificationSoundEnabled": false,
    "LastCertificateSnapshotHash": "",
    "Thresholds": {
      "Level1": 1,
      "Level7": 7,
      "Level15": 15,
      "Level30": 30
    },
    "LogFormat": 1,
    "EventLogEnabled": false,
    "TelemetryEnabled": true
  }
}
"@
    Set-Content -Path $settingsPath -Value $settings -Encoding UTF8
}

function New-BugHuntCertificate {
    Write-Step "Criando certificado A1 de teste"
    # CertificatePolicies ::= SEQUENCE { SEQUENCE { policyIdentifier 2.16.76.1.2.1.1 } }
    # 2.16.76.1.2.1.x e a familia ICP-Brasil A1 usada pelo filtro do app.
    $a1PolicyRaw = [byte[]](0x30,0x0A,0x30,0x08,0x06,0x06,0x60,0x4C,0x01,0x02,0x01,0x01)
    $a1PolicyExtension = [System.Security.Cryptography.X509Certificates.X509Extension]::new(
        [System.Security.Cryptography.Oid]::new("2.5.29.32"),
        $a1PolicyRaw,
        $false)
    $cert = New-SelfSignedCertificate `
        -Subject $subject `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -KeyAlgorithm RSA `
        -KeyLength 2048 `
        -HashAlgorithm SHA256 `
        -KeyExportPolicy NonExportable `
        -Extension $a1PolicyExtension `
        -NotAfter (Get-Date).AddDays(5)

    $createdThumbprints.Add($cert.Thumbprint) | Out-Null
    Write-Host "Certificado criado: $($cert.Thumbprint)"
}

function Get-ProtocolCommand {
    $protocolKey = "HKCU:\Software\Classes\cert-expiry-monitor\shell\open\command"
    if (-not (Test-Path $protocolKey)) {
        return $null
    }

    return [string](Get-Item -Path $protocolKey).GetValue("")
}

function Wait-ForCondition([scriptblock]$Condition, [int]$TimeoutSeconds, [string]$FailureMessage) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        if (& $Condition) {
            return
        }
        Start-Sleep -Seconds 1
    } while ((Get-Date) -lt $deadline)

    throw $FailureMessage
}

function Find-AppWindow([int]$ProcessId, [string]$WindowName) {
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes

    $windowCondition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
            $ProcessId),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            $WindowName))
    $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        $windowCondition)
    for ($index = $windows.Count - 1; $index -ge 0; $index--) {
        $candidate = $windows.Item($index)
        if ($candidate.Current.NativeWindowHandle -ne 0 -and -not $candidate.Current.IsOffscreen) {
            return $candidate
        }
    }
    return $null
}

function Invoke-AppWindowButton([int]$ProcessId, [string]$WindowName, [string]$ButtonName, [int]$TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $window = Find-AppWindow $ProcessId $WindowName
        if ($null -ne $window) {
            $buttonCondition = [System.Windows.Automation.AndCondition]::new(
                [System.Windows.Automation.PropertyCondition]::new(
                    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                    [System.Windows.Automation.ControlType]::Button),
                [System.Windows.Automation.PropertyCondition]::new(
                    [System.Windows.Automation.AutomationElement]::NameProperty,
                    $ButtonName))
            $button = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition)
            if ($null -eq $button) {
                $buttonTypeCondition = [System.Windows.Automation.PropertyCondition]::new(
                    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                    [System.Windows.Automation.ControlType]::Button)
                $button = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonTypeCondition) |
                    Where-Object { $_.Current.Name.StartsWith($ButtonName, [StringComparison]::OrdinalIgnoreCase) } |
                    Select-Object -First 1
            }
            if ($null -ne $button) {
                $buttonHandle = [IntPtr]$button.Current.NativeWindowHandle
                if ($buttonHandle -ne [IntPtr]::Zero) {
                    # BM_CLICK assincrono evita que ShowDialog bloqueie InvokePattern.Invoke().
                    if (-not [BugHuntNativeMethods]::PostMessage($buttonHandle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)) {
                        throw "PostMessage(BM_CLICK) falhou para '$ButtonName'."
                    }
                    return $true
                }

                $invoke = $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
                try {
                    ([System.Windows.Automation.InvokePattern]$invoke).Invoke()
                }
                catch [System.Runtime.InteropServices.COMException] {
                    # WinForms ShowDialog pode manter Invoke() bloqueado mesmo apos despachar o clique.
                    # Os asserts seguintes confirmam que o popup realmente abriu e foi fechado.
                    if ($_.Exception.HResult -ne -2146233083) { throw }
                }
                return $true
            }
        }

        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)

    return $false
}

function Get-LogOccurrenceCount([string]$Needle) {
    $monitorLog = Join-Path $dataDir "monitor.log"
    if (-not (Test-Path $monitorLog)) { return 0 }
    $content = Get-Content -Path $monitorLog -Raw
    return ([regex]::Matches($content, [regex]::Escape($Needle), [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)).Count
}

function Get-StartupRegisteredForInstall {
    $taskQuery = Invoke-Schtasks @("/query", "/tn", $taskName, "/fo", "LIST", "/v")
    $taskOutput = $taskQuery.Output | Out-String
    if ($taskQuery.ExitCode -eq 0 -and (Test-ContainsIgnoreCase $taskOutput $installDir)) {
        return $true
    }

    if (Test-Path $runKey) {
        $item = Get-ItemProperty -Path $runKey -Name $runValueName -ErrorAction SilentlyContinue
        $property = $item.PSObject.Properties[$runValueName]
        if ($property -and (Test-ContainsIgnoreCase ([string]$property.Value) $installDir)) {
            return $true
        }
    }

    return $false
}

function Assert-CaptureArtifacts([string]$OutDir) {
    $pngs = @(Get-ChildItem -Path $OutDir -Filter "*.png" -ErrorAction SilentlyContinue |
        Where-Object { $_.Length -gt 1000 })
    Assert-True ($pngs.Count -gt 0) "Nenhum PNG util capturado em $OutDir"

    $uiaPath = Join-Path $OutDir "03-uia-tree.txt"
    Assert-True (Test-Path $uiaPath) "UIA tree nao gerada em $uiaPath"
    $uia = Get-Content -Path $uiaPath -Raw
    Assert-True ((Test-ContainsIgnoreCase $uia "Certificados") -or
        (Test-ContainsIgnoreCase $uia "Configur")) "UIA tree sem controles esperados em $OutDir"
}

function Save-RunArtifacts {
    $runDataDir = Join-Path $artifactDir "run-data"
    if (Test-Path $runDataDir) {
        Remove-Item -LiteralPath $runDataDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    if (Test-Path $dataDir) {
        Copy-Item -LiteralPath $dataDir -Destination $runDataDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    $taskQuery = Invoke-Schtasks @("/query", "/tn", $taskName, "/fo", "LIST", "/v")
    $taskQuery.Output |
        Set-Content -Path (Join-Path $artifactDir "task-after-run.txt") -Encoding UTF8

    if (Test-Path $runKey) {
        $item = Get-ItemProperty -Path $runKey -Name $runValueName -ErrorAction SilentlyContinue
        $property = $item.PSObject.Properties[$runValueName]
        if ($property) {
            [string]$property.Value |
                Set-Content -Path (Join-Path $artifactDir "hkcu-run-after-run.txt") -Encoding UTF8
        }
    }

    Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $subject } |
        Select-Object Subject, Thumbprint, NotAfter |
        Format-List |
        Out-File -FilePath (Join-Path $artifactDir "certs-after-run.txt") -Encoding UTF8

    if (Test-Path $shortcutPath) {
        @(
            "Path: $shortcutPath"
            "Target: $(Get-ShortcutTarget $shortcutPath)"
            "Arguments: $(Get-ShortcutArguments $shortcutPath)"
            "AppUserModelID: $(Get-ShortcutExtendedProperty $shortcutPath 'System.AppUserModel.ID')"
        ) | Set-Content -Path (Join-Path $artifactDir "shortcut-after-run.txt") -Encoding UTF8
    }
}

try {
    Save-Snapshot
    Stop-AppProcesses

    Remove-DirectoryUnder -Path $installDir -AllowedParent (Join-Path $localAppData "Programs")
    Remove-DirectoryUnder -Path $dataDir -AllowedParent $localAppData

    Invoke-External $DotNetPath @(
        "restore",
        "CertExpiryMonitor.csproj",
        "--locked-mode"
    ) "Restaurando pacotes em locked mode"

    Invoke-External $DotNetPath @(
        "publish",
        "CertExpiryMonitor.csproj",
        "--configuration", "Release",
        "--runtime", "win-x64",
        "--self-contained", "true",
        "--no-restore",
        "/p:PublishSingleFile=true",
        "/p:IncludeNativeLibrariesForSelfExtract=true",
        "--output", $publishDir
    ) "Publicando build Release win-x64"

    Write-Step "Instalando build isolado"
    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
    Copy-Item -Path (Join-Path $publishDir "*") -Destination $installDir -Recurse -Force
    $installedExe = Join-Path $installDir "CertExpiryMonitor.exe"
    Assert-True (Test-Path $installedExe) "Executavel instalado nao encontrado em $installedExe"

    Write-BugHuntSettings
    New-BugHuntCertificate

    Write-Step "Validando background, startup e notificacao"
    $background = Start-Process -FilePath $installedExe -ArgumentList "--background" -PassThru
    Start-Sleep -Seconds 5
    Assert-True (-not $background.HasExited) "Processo background encerrou cedo demais"

    Wait-ForCondition { Get-StartupRegisteredForInstall } 20 "Startup nao registrado via Task Scheduler nem HKCU\Run"
    Wait-ForCondition { Test-Path $shortcutPath } 20 "Atalho do Start Menu para Toast nao foi criado"
    $shortcutTarget = Get-ShortcutTarget $shortcutPath
    $shortcutArguments = Get-ShortcutArguments $shortcutPath
    $shortcutAppUserModelId = Get-ShortcutExtendedProperty $shortcutPath "System.AppUserModel.ID"
    Assert-True (Test-ProcessPathEquals $shortcutTarget $installedExe) "Atalho do Toast aponta para alvo inesperado: $shortcutTarget"
    Assert-True ($shortcutArguments -eq "--background") "Atalho do Toast nao usa --background: $shortcutArguments"
    Assert-True ($shortcutAppUserModelId -eq "CertExpiryMonitor.Windows") "Atalho do Toast sem AppUserModelID esperado: $shortcutAppUserModelId"

    Wait-ForCondition { -not [string]::IsNullOrWhiteSpace((Get-ProtocolCommand)) } 20 "Protocolo cert-expiry-monitor nao foi registrado"
    $protocolCommand = Get-ProtocolCommand
    Assert-True (Test-ContainsIgnoreCase $protocolCommand $installDir) "Protocolo cert-expiry-monitor nao aponta para install isolado: $protocolCommand"
    Assert-True (Test-ContainsIgnoreCase $protocolCommand "--details") "Protocolo cert-expiry-monitor nao abre detalhes: $protocolCommand"
    Assert-True (Test-ContainsIgnoreCase $protocolCommand '"%1"') "Protocolo cert-expiry-monitor nao repassa URI: $protocolCommand"

    $second = Start-Process -FilePath $installedExe -ArgumentList "--details" -PassThru
    Assert-True ($second.WaitForExit(10000)) "Segunda instancia nao encerrou apos sinalizar primeira instancia"

    Start-Process -FilePath "cert-expiry-monitor://details" | Out-Null
    Wait-ForCondition {
        $monitorLog = Join-Path $dataDir "monitor.log"
        if (-not (Test-Path $monitorLog)) { return $false }
        $content = Get-Content -Path $monitorLog -Raw
        return Test-ContainsIgnoreCase $content "User opened certificate details window."
    } 20 "Ativacao via protocolo cert-expiry-monitor nao abriu detalhes"
    Assert-True (Invoke-AppWindowButton $background.Id "Certificados A1 monitorados" "Fechar janela de detalhes" 20) "Janela aberta pelo protocolo nao foi fechada via UI Automation"
    Wait-ForCondition {
        $null -eq (Find-AppWindow $background.Id "Certificados A1 monitorados")
    } 20 "Janela aberta pelo protocolo permaneceu visivel antes do teste de fallback"
    $detailsBeforeFallback = Get-LogOccurrenceCount "User opened certificate details window."

    Wait-ForCondition {
        $monitorLog = Join-Path $dataDir "monitor.log"
        if (-not (Test-Path $monitorLog)) { return $false }
        $content = Get-Content -Path $monitorLog -Raw
        return Test-ContainsIgnoreCase $content "Certificate check completed"
    } 80 "Check automatico nao apareceu no monitor.log"

    Wait-ForCondition {
        $monitorLog = Join-Path $dataDir "monitor.log"
        if (-not (Test-Path $monitorLog)) { return $false }
        $content = Get-Content -Path $monitorLog -Raw
        return (Test-ContainsIgnoreCase $content "Notification dispatched") -or
            (Test-ContainsIgnoreCase $content "Failed to show toast notification") -or
            (Test-ContainsIgnoreCase $content "app popup fallback was used")
    } 30 "Nenhum caminho de toast/fallback foi observado no log"

    $notificationLog = Get-Content -Path (Join-Path $dataDir "monitor.log") -Raw
    if (Test-ContainsIgnoreCase $notificationLog "app popup fallback was used") {
        Assert-True (Invoke-AppWindowButton $background.Id "Certificados digitais" "Ver detalhes" 20) "Popup de fallback nao abriu detalhes via UI Automation"
        Wait-ForCondition {
            (Get-LogOccurrenceCount "User opened certificate details window.") -gt $detailsBeforeFallback
        } 20 "Clique em Ver detalhes no fallback nao abriu a janela"
        Assert-True ($null -eq (Find-AppWindow $background.Id "Certificados digitais")) "Popup de fallback permaneceu aberto apos Ver detalhes"
        $isolatedProcesses = @(Get-Process -Name $appName -ErrorAction SilentlyContinue | Where-Object { Test-ProcessPathEquals $_.Path $installedExe })
        Assert-True ($isolatedProcesses.Count -eq 1) "Fallback abriu instancia duplicada do app"

        $configure = Start-Process -FilePath $installedExe -ArgumentList "--configure" -PassThru
        Assert-True ($configure.WaitForExit(10000)) "Segunda instancia --configure nao encerrou"
        $fallbackCountBeforeTest = Get-LogOccurrenceCount "app popup fallback was used"
        Assert-True (Invoke-AppWindowButton $background.Id "Certificados A1 monitorados" "Testar popup agora" 20) "Botao Testar popup agora nao foi acionado via UI Automation"
        Wait-ForCondition {
            (Get-LogOccurrenceCount "app popup fallback was used") -gt $fallbackCountBeforeTest
        } 20 "Popup de teste nao percorreu o fallback esperado"
        $testPopupLog = Get-Content -Path (Join-Path $dataDir "monitor.log") -Raw
        Assert-True (-not (Test-ContainsIgnoreCase $testPopupLog "Failed to show fallback notification")) "Popup de teste registrou falha de exibicao"
    }

    $telemetryPath = Join-Path $dataDir "telemetry.json"
    Wait-ForCondition { Test-Path $telemetryPath } 20 "telemetry.json nao foi criado"
    Assert-True (Test-ContainsIgnoreCase (Get-Content -Path $telemetryPath -Raw) "TotalChecks") "telemetry.json sem TotalChecks"

    $diagnosticsDbPath = Join-Path $dataDir "diagnostics.db"
    Wait-ForCondition { Test-Path $diagnosticsDbPath } 20 "diagnostics.db nao foi criado"
    Assert-True ((Get-Item -LiteralPath $diagnosticsDbPath).Length -gt 0) "diagnostics.db vazio"

    Write-Step "Capturando UI Details e Configure"
    $detailsCapture = Join-Path $captureRoot "details"
    $configureCapture = Join-Path $captureRoot "configure"
    Invoke-External "powershell.exe" @(
        "-ExecutionPolicy", "Bypass",
        "-File", (Join-Path $PSScriptRoot "CaptureUi.ps1"),
        "-ExePath", $installedExe,
        "-OutDir", $detailsCapture,
        "-WaitMs", "4000",
        "-Mode", "details"
    ) "Captura UI --details"
    Assert-CaptureArtifacts $detailsCapture

    Invoke-External "powershell.exe" @(
        "-ExecutionPolicy", "Bypass",
        "-File", (Join-Path $PSScriptRoot "CaptureUi.ps1"),
        "-ExePath", $installedExe,
        "-OutDir", $configureCapture,
        "-WaitMs", "4000",
        "-Mode", "configure"
    ) "Captura UI --configure"
    Assert-CaptureArtifacts $configureCapture

    Write-Step "Validacao final antes do cleanup"
    Assert-True ((Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $subject }).Count -eq 1) "Certificado de teste nao esta no estado esperado"
    Write-Host "Bug hunt concluido. Artifacts: $artifactDir"
}
finally {
    try {
        try {
            Save-RunArtifacts
        }
        catch {
            Write-Warning "Falha ao salvar artefatos da execucao antes do cleanup: $_"
        }

        Restore-Snapshot
    }
    finally {
        Stop-Transcript | Out-Null
        if (-not $KeepArtifacts) {
            try {
                Remove-Item -LiteralPath $artifactDir -Recurse -Force -ErrorAction SilentlyContinue
            }
            catch {
                Write-Warning "Nao foi possivel remover artifacts temporarios: $_"
            }
        }
    }
}
