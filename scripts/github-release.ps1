# Cria release no GitHub e anexa o instalador .exe como asset.
# Uso: pwsh -NoProfile -File .\scripts\github-release.ps1 -Tag v1.0.1
param(
    [Parameter(Mandatory = $true)]
    [string]$Tag,

    [string]$ExePath = "D:\projetos_cli\escritorio\Vencimento dos Certificados\installer-output\CertExpiryMonitorSetup.exe",
    [string]$Owner   = "andrejipa",
    [string]$Repo    = "CertExpiryMonitor",
    [string]$Title   = "",
    [string]$BodyMarkdown = ""
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $ExePath)) {
    Write-Error "Instalador nao encontrado: $ExePath"
    exit 1
}

if ([string]::IsNullOrWhiteSpace($Title)) { $Title = $Tag }

Write-Host "[1/3] Lendo credenciais..."
$credInput = "protocol=https`nhost=github.com`n"
$credLines = $credInput | & git credential fill 2>$null
$token = ($credLines | Where-Object { $_ -like "password=*" } | Select-Object -First 1) -replace "^password=", ""

$h = @{
    Authorization = "Bearer $token"
    "User-Agent"  = "release-publisher"
    Accept        = "application/vnd.github+json"
}

Write-Host "[2/3] Criando release $Tag..."
$body = @{
    tag_name        = $Tag
    name            = $Title
    body            = $BodyMarkdown
    draft           = $false
    prerelease      = $false
} | ConvertTo-Json -Depth 4

try {
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Owner/$Repo/releases" -Headers $h -Method Post -Body $body -ContentType "application/json"
    Write-Host "      Release criada: $($release.html_url)"
} catch {
    $resp = $_.Exception.Response
    if ($resp) {
        $reader = New-Object System.IO.StreamReader($resp.GetResponseStream())
        Write-Host "      Resposta: $($reader.ReadToEnd())"
    }
    throw
}

Write-Host "[3/3] Upload do instalador (~70 MB)..."
$uploadUrl = $release.upload_url -replace '\{.*\}$', '?name=CertExpiryMonitorSetup.exe'
$uploadHeaders = @{
    Authorization  = "Bearer $token"
    "User-Agent"   = "release-publisher"
    "Content-Type" = "application/octet-stream"
}
$bytes = [System.IO.File]::ReadAllBytes($ExePath)
$asset = Invoke-RestMethod -Uri $uploadUrl -Headers $uploadHeaders -Method Post -Body $bytes
Write-Host "      Asset URL: $($asset.browser_download_url)"

Write-Host ""
Write-Host "==============================================================="
Write-Host "Release publicada: $($release.html_url)"
Write-Host "Download direto:   $($asset.browser_download_url)"
Write-Host "==============================================================="
