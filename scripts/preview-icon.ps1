Add-Type -AssemblyName System.Drawing
$ico = New-Object System.Drawing.Icon "D:\projetos_cli\escritorio\Vencimento dos Certificados\assets\CertExpiryMonitor.ico", 256, 256
$bmp = $ico.ToBitmap()
$bmp.Save("D:\projetos_cli\escritorio\Vencimento dos Certificados\assets\preview-256.png", [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host "preview-saved: $($bmp.Width)x$($bmp.Height)"
