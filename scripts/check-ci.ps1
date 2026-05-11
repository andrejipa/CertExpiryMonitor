$ErrorActionPreference = "Stop"
$credInput = "protocol=https`nhost=github.com`n"
$credLines = $credInput | & git credential fill 2>$null
$token = ($credLines | Where-Object { $_ -like "password=*" } | Select-Object -First 1) -replace "^password=", ""

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
    Write-Host ("[{0,-10}] [{1,-10}] {2} | {3}" -f $_.status, ($_.conclusion ?? "running"), $_.name, $title)
    Write-Host ("           URL: {0}" -f $_.html_url)
}
