$ErrorActionPreference = "Stop"
$credFile = Join-Path $env:TEMP ("cert-expiry-git-credential-{0}.txt" -f [Guid]::NewGuid().ToString("N"))
try {
    [System.IO.File]::WriteAllText(
        $credFile,
        "protocol=https`nhost=github.com`n`n",
        [System.Text.UTF8Encoding]::new($false))

    $cmd = '/c git credential fill < "' + $credFile + '"'
    $credLines = & cmd.exe $cmd 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "Nao foi possivel ler credencial do Git para github.com."
    }
}
finally {
    Remove-Item -LiteralPath $credFile -Force -ErrorAction SilentlyContinue
}
$token = ($credLines | Where-Object { $_ -like "password=*" } | Select-Object -First 1) -replace "^password=", ""
if ([string]::IsNullOrWhiteSpace($token)) {
    throw "Credencial do Git para github.com nao retornou token/senha."
}

$h = @{
    Authorization = "Bearer $token"
    "User-Agent"  = "check-ci"
    Accept        = "application/vnd.github+json"
}
$r = Invoke-RestMethod "https://api.github.com/repos/andrejipa/CertExpiryMonitor/actions/runs?per_page=5" -Headers $h

if ($r.workflow_runs.Count -eq 0) {
    Write-Host "Nenhum workflow run encontrado ainda. Pode levar ate 1 minuto."
    return
}

$r.workflow_runs | ForEach-Object {
    $title = ($_.head_commit.message -split "`n")[0]
    $conclusion = $_.conclusion
    if ([string]::IsNullOrWhiteSpace($conclusion)) {
        $conclusion = "running"
    }
    Write-Host ("[{0,-10}] [{1,-10}] {2} | {3}" -f $_.status, $conclusion, $_.name, $title)
    Write-Host ("           URL: {0}" -f $_.html_url)
}
