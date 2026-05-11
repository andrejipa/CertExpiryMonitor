# Cria o repositorio github.com/andrejipa/CertExpiryMonitor (se nao existir) e
# faz push da branch main. Usa o token armazenado no Windows Credential Manager
# (extraido via "git credential fill"), nunca o expoe em log.
# Uso: pwsh -NoProfile -File .\scripts\push-to-github.ps1

$ErrorActionPreference = "Stop"

$repoPath = "D:\projetos_cli\escritorio\Vencimento dos Certificados"
$owner    = "andrejipa"
$repo     = "CertExpiryMonitor"

Set-Location $repoPath

# 1. Extrai credenciais armazenadas (input via pipeline; PowerShell nao tem heredoc <<<)
Write-Host "[1/4] Lendo credenciais do Windows Credential Manager..."
$credInput = "protocol=https`nhost=github.com`n"
$credLines = $credInput | & git credential fill 2>$null
$user  = ($credLines | Where-Object { $_ -like "username=*" } | Select-Object -First 1) -replace "^username=", ""
$token = ($credLines | Where-Object { $_ -like "password=*" } | Select-Object -First 1) -replace "^password=", ""

if (-not $user -or -not $token) {
    Write-Error "Nao foi possivel ler credenciais. Rode 'git push' uma vez para popular o Credential Manager."
    exit 1
}
Write-Host "      Usuario: $user (token: $(if ($token.Length -gt 0) { '[OK, ' + $token.Length + ' chars]' } else { 'FALTANDO' }))"

# 2. Verifica se o repositorio existe
Write-Host "[2/4] Verificando se $owner/$repo ja existe..."
$headers = @{
    Authorization = "Bearer $token"
    "User-Agent"  = "CertExpiryMonitor-AutoPush"
    Accept        = "application/vnd.github+json"
}
$exists = $false
try {
    $null = Invoke-RestMethod -Uri "https://api.github.com/repos/$owner/$repo" -Headers $headers -Method Get -ErrorAction Stop
    $exists = $true
    Write-Host "      Repositorio ja existe."
} catch {
    if ($_.Exception.Response.StatusCode.value__ -eq 404) {
        Write-Host "      Repositorio NAO existe. Sera criado."
    } else {
        Write-Error "Erro inesperado ao verificar repo: $_"
        exit 1
    }
}

# 3. Cria o repositorio se nao existir
if (-not $exists) {
    Write-Host "[3/4] Criando repositorio publico $owner/$repo ..."
    $body = @{
        name         = $repo
        description  = "Monitor de certificados digitais A1 para Windows (.NET 8 WinForms, sem rede, sem admin)"
        homepage     = ""
        private      = $false
        has_issues   = $true
        has_projects = $false
        has_wiki     = $false
        auto_init    = $false
    } | ConvertTo-Json
    try {
        $created = Invoke-RestMethod -Uri "https://api.github.com/user/repos" -Headers $headers -Method Post -Body $body -ContentType "application/json"
        Write-Host "      Criado: $($created.html_url)"
    } catch {
        Write-Error "Falha ao criar repositorio: $($_.Exception.Message)"
        if ($_.Exception.Response) {
            $stream = $_.Exception.Response.GetResponseStream()
            $reader = New-Object System.IO.StreamReader($stream)
            $body = $reader.ReadToEnd()
            Write-Host "      Resposta: $body"
        }
        exit 1
    }
} else {
    Write-Host "[3/4] Pulando criacao (ja existe)."
}

# 4. Push (credenciais ja estao no Credential Manager — git usa automaticamente)
Write-Host "[4/4] Fazendo push de main para origin..."
& git push -u origin main 2>&1 | ForEach-Object { Write-Host "      $_" }
if ($LASTEXITCODE -ne 0) {
    Write-Error "git push falhou com exit code $LASTEXITCODE"
    exit 1
}

Write-Host ""
Write-Host "==============================================================="
Write-Host "Push concluido! Acesse: https://github.com/$owner/$repo"
Write-Host "CI vai rodar automaticamente — acompanhe em:"
Write-Host "  https://github.com/$owner/$repo/actions"
Write-Host "==============================================================="
