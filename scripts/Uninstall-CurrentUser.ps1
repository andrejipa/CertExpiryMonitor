param(
    [string]$InstallDirectory = "$env:LOCALAPPDATA\Programs\CertExpiryMonitor"
)

$ErrorActionPreference = "Stop"

# Remove a tarefa agendada criada pelo aplicativo (preferido sobre HKCU\Run).
schtasks.exe /delete /tn "CertExpiryMonitor" /f 2>$null | Out-Null

# Remove tambem a entrada HKCU\Run para compatibilidade com versoes antigas.
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
Remove-ItemProperty -Path $runKey -Name "CertExpiryMonitor" -ErrorAction SilentlyContinue

Get-Process -Name "CertExpiryMonitor" -ErrorAction SilentlyContinue | Stop-Process -Force

if (Test-Path $InstallDirectory) {
    Remove-Item -Path $InstallDirectory -Recurse -Force
}

Write-Host "CertExpiryMonitor removido do usuario atual."
