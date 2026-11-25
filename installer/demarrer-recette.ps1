# Serveur local de recette. Windows PowerShell 5.1+ ou PowerShell 7, Docker en mode Linux.
$ErrorActionPreference = "Stop"
Push-Location $PSScriptRoot
try {
    foreach ($line in Get-Content -LiteralPath SHA256SUMS) {
        if ($line -notmatch '^([a-f0-9]{64})  \./(.+)$') { throw "Manifeste invalide." }
        $expected = $Matches[1]
        $file = $Matches[2]
        if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $expected) {
            throw "Fichier de livraison incomplet : $file"
        }
    }
    & docker compose version | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Docker Compose est requis." }
    $engine = & docker info --format '{{.OSType}}'
    if ($LASTEXITCODE -ne 0 -or $engine -ne 'linux') { throw "Demarrer Docker avec les conteneurs Linux." }
    function New-Secret {
        $bytes = New-Object byte[] 32
        $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
        try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
        [BitConverter]::ToString($bytes).Replace('-','').ToLowerInvariant()
    }
    if (!(Test-Path -LiteralPath .env)) {
        $settings = @(
            "COMPOSE_PROJECT_NAME=transport-recette-$([guid]::NewGuid().ToString('N'))"
            "TRANSPORT_DB_PASSWORD=$(New-Secret)"
            "TRANSPORT_ADMIN_SEAL=$(New-Secret)"
            "TRANSPORT_ACCESS=Company"
            "TRANSPORT_SANDBOX_PAYMENTS=true"
            "TRANSPORT_SANDBOX_SMS=true"
        )
        $tmp = ".env.$([guid]::NewGuid().ToString('N')).tmp"
        try {
            [IO.File]::WriteAllLines((Join-Path $PSScriptRoot $tmp),$settings,[Text.Encoding]::ASCII)
            [IO.File]::Move((Join-Path $PSScriptRoot $tmp),(Join-Path $PSScriptRoot '.env'))
        } finally { if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp } }
    }
    & docker load -i images.tar.gz
    if ($LASTEXITCODE -ne 0) { throw "Chargement des images impossible." }
    & docker compose up -d --no-build --pull never --wait
    if ($LASTEXITCODE -ne 0) { throw "Demarrage du serveur impossible." }
    $published = & docker compose port api 8080
    if ($LASTEXITCODE -ne 0 -or $published -notmatch '^127\.0\.0\.1:(\d+)$') { throw "Adresse de recette inattendue." }
    $url = "http://127.0.0.1:$($Matches[1])"
    foreach ($attempt in 1..40) {
        try { $null = Invoke-RestMethod "$url/api/trajets" -TimeoutSec 2; break }
        catch { if ($attempt -eq 40) { throw }; Start-Sleep -Seconds 1 }
    }
    $company = & docker compose exec -T db psql -XqAt -U postgres -d transport_demo -c "SELECT id FROM company WHERE access_mode='Company'"
    if ($LASTEXITCODE -ne 0 -or !$company) { throw "Compagnie locale introuvable." }
    Write-Host "Serveur de recette disponible : $url"
    Write-Host "Compagnie : $company"
    Write-Host "Portail editeur : $url/editeur/index.html"
} finally { Pop-Location }
