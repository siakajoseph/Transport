# Outil de l'exploitant ayant acces a la base locale du paquet de recette.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateLength(1,90)][string]$Operateur,
    [Parameter(Mandatory)][guid]$Compagnie,
    [switch]$SansVenteWeb
)
$ErrorActionPreference = "Stop"
Push-Location $PSScriptRoot
try {
    if (!(Test-Path -LiteralPath .env)) { throw "Demarrez d'abord le serveur de recette." }
    $published = & docker compose port api 8080
    if ($LASTEXITCODE -ne 0 -or $published -notmatch '^127\.0\.0\.1:(\d+)$') { throw "API locale indisponible." }
    $url = "http://127.0.0.1:$($Matches[1])/editeur/index.html"
    $web = if ($SansVenteWeb) { "false" } else { "true" }
    $OutputEncoding = New-Object System.Text.UTF8Encoding($false)
    $sql = Get-Content -LiteralPath ops/mandater-editeur.sql -Encoding UTF8 -Raw
    $reply = $sql | docker compose exec -T db psql -XqAt -U postgres -d transport_demo -v "company_id=$Compagnie" -v "operator_name=$($Operateur.Trim())" -v "web_included=$web"
    if ($LASTEXITCODE -ne 0) { throw "Mandat non emis. Verifiez la compagnie et son etat d'ouverture." }
    $key = [regex]::Match(($reply -join [Environment]::NewLine),'(?m)([a-f0-9]{64})\s*$')
    if (!$key.Success) { throw "Reponse du serveur incomplete." }
    [pscustomobject]@{ Portail=$url; Compagnie=$Compagnie; Operateur=$Operateur.Trim(); Cle=$key.Groups[1].Value }
} finally { Pop-Location }
