[CmdletBinding()]
param(
    [Parameter(Mandatory)][guid]$Compagnie,
    [Parameter(Mandatory)][guid]$Agence,
    [Parameter(Mandatory)][ValidateLength(1,90)][string]$Poste,
    [Parameter(Mandatory)][ValidateLength(1,90)][string]$Agent
)
$ErrorActionPreference="Stop"
if (!$Poste.Trim() -or !$Agent.Trim()) { throw "Renseigner le poste et l'agent." }
Push-Location $PSScriptRoot
try {
    $OutputEncoding = New-Object System.Text.UTF8Encoding($false)
    $sql = Get-Content -LiteralPath ops/premier-guichet.sql -Encoding UTF8 -Raw
    $reply = $sql | docker compose exec -T db psql -XqAt -U postgres -d transport_demo -v "company=$Compagnie" -v "agency=$Agence" -v "label=$Poste" -v "name=$Agent"
    if ($LASTEXITCODE -ne 0) { throw "Creation refusee. Verifier la compagnie, l'agence et les postes existants." }
    $reply | ConvertFrom-Json
} finally { Pop-Location }
