# Gera o ícone do app (.ico) com múltiplos tamanhos.
# Desenha um "certificado" estilizado: retângulo branco com selo/timbre dourado e
# um relógio sobreposto indicando "monitoramento de validade". Cor primária azul (#0F4C81).
#
# Output: assets/CertExpiryMonitor.ico contendo PNGs em 16/24/32/48/64/128/256 px.
# Uso: pwsh -NoProfile -File .\scripts\GenerateAppIcon.ps1

param(
    [string]$OutPath = "D:\projetos_cli\escritorio\Vencimento dos Certificados\assets\CertExpiryMonitor.ico"
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing

$outDir = Split-Path -Parent $OutPath
if (!(Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

# Tamanhos padrao para .ico no Windows (Explorer / taskbar / system tray)
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngBytes = @()

function Draw-Icon([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $gfx = [System.Drawing.Graphics]::FromImage($bmp)
    $gfx.SmoothingMode    = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $gfx.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $gfx.PixelOffsetMode  = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $gfx.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias

    # Fundo transparente
    $gfx.Clear([System.Drawing.Color]::Transparent)

    # Proporcoes em fracao do tamanho
    $margin    = [Math]::Max(1, [int]($size * 0.08))
    $cardW     = $size - 2 * $margin
    $cardH     = [int]($cardW * 0.78)   # papel landscape
    $cardX     = $margin
    $cardY     = [int](($size - $cardH) / 2)

    # Sombra leve
    $shadow = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(40, 0, 0, 0))
    $gfx.FillRectangle($shadow, $cardX + 1, $cardY + 2, $cardW, $cardH)

    # Corpo do certificado (papel branco)
    $paper = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(252, 252, 250))
    $gfx.FillRectangle($paper, $cardX, $cardY, $cardW, $cardH)

    # Borda azul corporativa
    $borderPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(15, 76, 129)), ([Math]::Max(1, $size / 32))
    $gfx.DrawRectangle($borderPen, $cardX, $cardY, $cardW - 1, $cardH - 1)

    # Linhas horizontais simulando texto do certificado
    $textPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(140, 140, 140)), ([Math]::Max(1, $size / 64))
    $lineCount = 3
    $lineSpacing = [int]($cardH / 6)
    $lineStartY = $cardY + [int]($cardH * 0.20)
    $lineStartX = $cardX + [int]($cardW * 0.10)
    $lineEndX   = $cardX + [int]($cardW * 0.62)
    for ($i = 0; $i -lt $lineCount; $i++) {
        $y = $lineStartY + $i * $lineSpacing
        $gfx.DrawLine($textPen, $lineStartX, $y, $lineEndX, $y)
    }

    # Selo dourado (medalha) no canto inferior direito do papel
    $sealRadius = [int]($cardW * 0.18)
    $sealCx     = $cardX + $cardW - $sealRadius - [int]($cardW * 0.10)
    $sealCy     = $cardY + $cardH - $sealRadius - [int]($cardH * 0.18)
    $sealBrush  = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(212, 175, 55))
    $gfx.FillEllipse($sealBrush, $sealCx - $sealRadius, $sealCy - $sealRadius, $sealRadius * 2, $sealRadius * 2)
    $sealEdge = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(150, 110, 30)), ([Math]::Max(1, $size / 64))
    $gfx.DrawEllipse($sealEdge, $sealCx - $sealRadius, $sealCy - $sealRadius, $sealRadius * 2, $sealRadius * 2)

    # "A1" no centro do selo (representa certificado A1)
    if ($size -ge 32) {
        $fontSize = [Math]::Max(4, [int]($sealRadius * 0.85))
        $font = New-Object System.Drawing.Font "Segoe UI", $fontSize, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
        $textBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(80, 50, 10))
        $format = New-Object System.Drawing.StringFormat
        $format.Alignment     = [System.Drawing.StringAlignment]::Center
        $format.LineAlignment = [System.Drawing.StringAlignment]::Center
        $rect = New-Object System.Drawing.RectangleF (($sealCx - $sealRadius), ($sealCy - $sealRadius), ($sealRadius * 2), ($sealRadius * 2))
        $gfx.DrawString("A1", $font, $textBrush, $rect, $format)
        $font.Dispose()
        $textBrush.Dispose()
    }

    $shadow.Dispose(); $paper.Dispose(); $borderPen.Dispose(); $textPen.Dispose(); $sealBrush.Dispose(); $sealEdge.Dispose()
    $gfx.Dispose()
    return $bmp
}

# Gera cada tamanho como PNG em memoria
$pngStreams = @()
foreach ($s in $sizes) {
    $bmp = Draw-Icon $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngStreams += $ms
    $bmp.Dispose()
}

# Compoe o arquivo .ico (formato ICONDIR + ICONDIRENTRY[] + bitmaps)
# Ref: https://en.wikipedia.org/wiki/ICO_(file_format)
$icoStream = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $icoStream

# ICONDIR (6 bytes): Reserved=0, Type=1 (ICO), Count
$writer.Write([UInt16]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]$sizes.Count)

# ICONDIRENTRY (16 bytes cada): width, height, colors, reserved, planes, bits, size, offset
$entriesOffset = 6
$dataOffset    = 6 + 16 * $sizes.Count
$entries       = @()
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $size = $sizes[$i]
    $bytes = $pngStreams[$i].ToArray()
    $entries += [PSCustomObject]@{
        SizeDim = $size
        Bytes   = $bytes
        Offset  = $dataOffset
    }
    $dataOffset += $bytes.Length
}

foreach ($e in $entries) {
    $w = if ($e.SizeDim -eq 256) { [byte]0 } else { [byte]$e.SizeDim }
    $h = $w
    $writer.Write($w)              # width
    $writer.Write($h)              # height
    $writer.Write([byte]0)         # color count (0 = >=256 colors)
    $writer.Write([byte]0)         # reserved
    $writer.Write([UInt16]1)       # color planes
    $writer.Write([UInt16]32)      # bits per pixel
    $writer.Write([UInt32]$e.Bytes.Length)
    $writer.Write([UInt32]$e.Offset)
}

# Bitmap data
foreach ($e in $entries) {
    $writer.Write($e.Bytes)
}

$writer.Flush()
[System.IO.File]::WriteAllBytes($OutPath, $icoStream.ToArray())
$writer.Close()
foreach ($s in $pngStreams) { $s.Dispose() }

Write-Host "Icone gerado: $OutPath ($([math]::Round((Get-Item $OutPath).Length / 1KB, 1)) KB, $($sizes.Count) tamanhos)"
