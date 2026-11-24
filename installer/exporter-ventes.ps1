[CmdletBinding()]
param([Parameter(Mandatory)][guid]$Compagnie,
      [Parameter(Mandatory)][datetime]$Jour,
      [Parameter(Mandatory)][string]$Fichier,
      [switch]$Mouvements)
$ErrorActionPreference="Stop"
$dest=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Fichier)
$tmp=Join-Path ([IO.Path]::GetDirectoryName($dest)) ([guid]::NewGuid().ToString("N")+".tmp")
if ([IO.File]::Exists($dest)) { throw "Choisir un nouveau fichier." }
Push-Location $PSScriptRoot
try {
    $utf8=New-Object System.Text.UTF8Encoding($false)
    $OutputEncoding=$utf8
    $writer=New-Object System.IO.StreamWriter($tmp,$false,$utf8)
    try {
        $source=if ($Mouvements) { 'ops/exporter-mouvements.sql' } else { 'ops/exporter-ventes.sql' }
        $sql=Get-Content -LiteralPath $source -Encoding UTF8 -Raw
        $sql | docker compose exec -T db psql -XqAt -U postgres -d transport_demo -v "company=$Compagnie" -v "jour=$($Jour.ToString('yyyy-MM-dd'))" | ForEach-Object { $writer.WriteLine($_) }
        if ($LASTEXITCODE -ne 0) { throw "Export refuse ou interrompu." }
    } finally { $writer.Dispose() }
    [IO.File]::Move($tmp,$dest)
} finally {
    if ([IO.File]::Exists($tmp)) { [IO.File]::Delete($tmp) }
    Pop-Location
}
