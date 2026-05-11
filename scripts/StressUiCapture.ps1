# Stress test visual: roda o app, abre DetailsForm, simula digitacao de
# caracteres adversariais na busca via UIA, captura screenshot pra cada um.
# Se algum caractere disparar crash, o processo morre e screenshot fica vazio.

param(
    [string]$ExePath = "D:\projetos_cli\escritorio\Vencimento dos Certificados\bin\Release\net8.0-windows10.0.19041.0\CertExpiryMonitor.exe",
    [string]$OutDir  = "D:\projetos_cli\escritorio\Vencimento dos Certificados\ui-shots-stress"
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$signature = @'
using System;
using System.Runtime.InteropServices;
public class Win32S {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@
Add-Type -TypeDefinition $signature -ErrorAction SilentlyContinue

function Capture-Window([IntPtr]$hwnd, [string]$path) {
    $rect = New-Object Win32S+RECT
    if (-not [Win32S]::GetWindowRect($hwnd, [ref]$rect)) { return $false }
    $w = $rect.Right - $rect.Left; $h = $rect.Bottom - $rect.Top
    if ($w -le 0 -or $h -le 0) { return $false }
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $gfx = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $gfx.GetHdc()
    $ok = [Win32S]::PrintWindow($hwnd, $hdc, 2)
    $gfx.ReleaseHdc($hdc)
    if ($ok) { $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png) }
    $gfx.Dispose(); $bmp.Dispose()
    return $ok
}

if (!(Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }
Get-Process CertExpiryMonitor -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

Write-Host "Iniciando app..."
$proc = Start-Process -FilePath $ExePath -ArgumentList "--details" -PassThru
Start-Sleep -Milliseconds 4500

$proc.Refresh()
$hwnd = $proc.MainWindowHandle
if ($hwnd -eq [IntPtr]::Zero) {
    Write-Error "Janela nao apareceu"
    exit 1
}
[Win32S]::SetForegroundWindow($hwnd) | Out-Null

$auto = [System.Windows.Automation.AutomationElement]::RootElement
$processCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$mainWin = $auto.FindFirst([System.Windows.Automation.TreeScope]::Children, $processCondition)
if (-not $mainWin) {
    Write-Error "UIA nao achou janela do app"
    exit 1
}

# Acha o campo "Buscar" pelo AccessibleName
$searchCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "Buscar por titular ou documento")
$search = $mainWin.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $searchCond)
if (-not $search) {
    Write-Warning "Campo de busca nao encontrado por AccessibleName"
    # tenta achar qualquer Edit
    $editCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)
    $search = $mainWin.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $editCond)
}

if (-not $search) {
    Write-Error "Nenhum campo Edit encontrado"
    exit 1
}

$valuePattern = $search.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)

# Inputs adversariais — antes do fix, "[" crashava o app
$inputs = @(
    @{ Tag = "01-bracket-open";  Text = "[" },
    @{ Tag = "02-bracket-close"; Text = "]" },
    @{ Tag = "03-percent";       Text = "%" },
    @{ Tag = "04-star";          Text = "*" },
    @{ Tag = "05-bracket-pair";  Text = "[abc]" },
    @{ Tag = "06-mixed";         Text = "PORTAL%[*]" },
    @{ Tag = "07-quote";         Text = "O'Brien" },
    @{ Tag = "08-clear";         Text = "" }
)

$results = @()
foreach ($i in $inputs) {
    Write-Host "  Testando: $($i.Tag) -> '$($i.Text)'"
    try {
        $valuePattern.SetValue($i.Text)
        Start-Sleep -Milliseconds 300

        # Se o app crashou, MainWindowHandle vira zero
        $proc.Refresh()
        if ($proc.HasExited) {
            Write-Host "    !!! CRASH detectado no input '$($i.Text)'"
            $results += "$($i.Tag): CRASH"
            break
        }

        # Captura screenshot
        $imgPath = Join-Path $OutDir "$($i.Tag).png"
        $ok = Capture-Window $proc.MainWindowHandle $imgPath
        $results += "$($i.Tag): " + $(if ($ok) { "OK" } else { "captura falhou" })
    } catch {
        Write-Host "    EXCEPTION: $_"
        $results += "$($i.Tag): EXCEPTION $_"
    }
}

Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
Get-Process CertExpiryMonitor -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host ""
Write-Host "Resumo:"
$results | ForEach-Object { Write-Host "  $_" }
