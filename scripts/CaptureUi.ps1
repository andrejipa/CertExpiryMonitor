# Captura screenshots do CertExpiryMonitor em execucao.
# Usa PrintWindow (Win32) em vez de CopyFromScreen — funciona mesmo em sessoes
# sem desktop interativo (RDP, agent, sessao bloqueada).
# Uso: pwsh -NoProfile -File .\scripts\CaptureUi.ps1

param(
    [string]$ExePath  = "D:\projetos_cli\escritorio\Vencimento dos Certificados\bin\Release\net8.0-windows10.0.19041.0\CertExpiryMonitor.exe",
    [string]$OutDir   = "D:\projetos_cli\escritorio\Vencimento dos Certificados\ui-shots",
    [int]   $WaitMs   = 3000,
    # --configure abre aba Configuracoes; --details abre aba Certificados (default).
    [string]$Mode     = "details"
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

# Win32 helpers — usados ao inves de CopyFromScreen para evitar dependencia de desktop interativo.
$signature = @'
using System;
using System.Runtime.InteropServices;
public class Win32 {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@
Add-Type -TypeDefinition $signature -ErrorAction SilentlyContinue

function Capture-Window([IntPtr]$hwnd, [string]$path) {
    $rect = New-Object Win32+RECT
    if (-not [Win32]::GetWindowRect($hwnd, [ref]$rect)) { return $false }
    $w = $rect.Right - $rect.Left
    $h = $rect.Bottom - $rect.Top
    if ($w -le 0 -or $h -le 0) { return $false }

    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $gfx = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $gfx.GetHdc()
    # PW_RENDERFULLCONTENT = 0x00000002 (Windows 8.1+) — captura conteudo composto inclusive controles.
    $ok = [Win32]::PrintWindow($hwnd, $hdc, 2)
    $gfx.ReleaseHdc($hdc)
    if ($ok) {
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    $gfx.Dispose(); $bmp.Dispose()
    return $ok
}

if (!(Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }

Get-Process CertExpiryMonitor -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

$flag = "--$Mode"
Write-Host "[1/4] Iniciando app com $flag ..."
$proc = Start-Process -FilePath $ExePath -ArgumentList $flag -PassThru
Start-Sleep -Milliseconds $WaitMs

if ($proc.HasExited) {
    Write-Error "Processo encerrou antes de capturar (ExitCode=$($proc.ExitCode))."
    exit 1
}

$proc.Refresh()
$hwnd = $proc.MainWindowHandle

if ($hwnd -eq [IntPtr]::Zero) {
    Write-Warning "MainWindowHandle = 0 — janela nao detectada."
} else {
    [Win32]::ShowWindow($hwnd, 5) | Out-Null  # SW_SHOW
    [Win32]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 400

    Write-Host "[2/4] Capturando janela principal (PrintWindow) ..."
    if (Capture-Window $hwnd (Join-Path $OutDir "02-mainwindow.png")) {
        Write-Host "      OK"
    } else {
        Write-Warning "      PrintWindow falhou"
    }
}

# UIA tree
Write-Host "[3/4] Inspecionando arvore UIA ..."
$auto = [System.Windows.Automation.AutomationElement]::RootElement
$processCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$processWindows = $auto.FindAll([System.Windows.Automation.TreeScope]::Children, $processCondition)

$uiaReport = @()
foreach ($win in $processWindows) {
    $title = $win.Current.Name
    $class = $win.Current.ClassName
    $uiaReport += "WINDOW: '$title' (class=$class)"
    $descendants = $win.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($el in $descendants) {
        $name = $el.Current.Name
        $ctrl = $el.Current.LocalizedControlType
        $aname = $el.Current.AutomationId
        if ([string]::IsNullOrWhiteSpace($name) -and [string]::IsNullOrWhiteSpace($aname)) { continue }
        $uiaReport += "  - [$ctrl] name='$name' autoId='$aname'"
    }
}
$uiaReport | Out-File -FilePath (Join-Path $OutDir "03-uia-tree.txt") -Encoding utf8

# Alterna abas e captura
Write-Host "[4/4] Alternando para cada aba ..."
$tabControl = $null
foreach ($win in $processWindows) {
    $tabCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Tab)
    $tabControl = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $tabCondition)
    if ($tabControl) { break }
}

if ($tabControl) {
    $tabItems = $tabControl.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::TabItem)))

    foreach ($tab in $tabItems) {
        $tabName = $tab.Current.Name
        Write-Host "  - Aba: '$tabName'"
        try {
            # SelectionItemPattern para TabItem em WinForms
            $sel = $tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
            if ($sel) { $sel.Select() }
            Start-Sleep -Milliseconds 700
            $proc.Refresh()
            $hwnd = $proc.MainWindowHandle
            [Win32]::SetForegroundWindow($hwnd) | Out-Null
            Start-Sleep -Milliseconds 300
            $safeName = ($tabName -replace '[^a-zA-Z0-9çÇãáàâéêíóôõúüÁÀÂÃÉÊÍÓÔÕÚÜ]','_')
            $okCap = Capture-Window $hwnd (Join-Path $OutDir "04-tab-$safeName.png")
            if (-not $okCap) { Write-Warning "    PrintWindow retornou false para '$tabName'" }
        } catch {
            Write-Warning "    Falha em '$tabName': $_"
        }
    }
}

Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
Get-Process CertExpiryMonitor -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Capturas em: $OutDir"
Get-ChildItem $OutDir | Format-Table Name, Length -AutoSize
