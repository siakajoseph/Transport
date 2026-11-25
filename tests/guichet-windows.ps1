# Integration fixture for the Windows runner. Use a fresh loopback-only cluster.
param([Parameter(Mandatory)][string]$Executable)
$ErrorActionPreference = "Stop"
$pgRoot = Join-Path $env:ProgramFiles "PostgreSQL"
$pgVersion = Get-ChildItem -Directory $pgRoot | Where-Object { $_.Name -match '^\d+$' } |
    Sort-Object { [int]$_.Name } -Descending | Select-Object -First 1
if (!$pgVersion) { throw "PostgreSQL is required on the Windows test runner." }
$env:PATH = (Join-Path $pgVersion.FullName "bin") + ";" + $env:PATH
$testRoot = Join-Path $env:RUNNER_TEMP ("transport-msi-" + [guid]::NewGuid())
$null = New-Item -ItemType Directory $testRoot
$cluster = Join-Path $testRoot "pgdata"
$apiDir = Join-Path $testRoot "api"
$pgLog = Join-Path $testRoot "postgres.log"
$env:PGHOST = "127.0.0.1"
$env:PGPORT = "55481"
$env:PGUSER = "postgres"
$env:PGDATABASE = "postgres"
$env:PGCLIENTENCODING = "UTF8"
$env:PGPASSWORD = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$pwFile = Join-Path $testRoot "bootstrap-password"
[IO.File]::WriteAllText($pwFile,$env:PGPASSWORD)
$server = $null
$startedPg = $false

function Query {
    $rows = & psql -XqAt -v ON_ERROR_STOP=1 @args
    if ($LASTEXITCODE -ne 0) { throw "Fixture SQL failed." }
    $rows
}
function Read-Key($OutputLines) {
    $match = [regex]::Match(($OutputLines -join [Environment]::NewLine),'(?m)([0-9a-f]{64})\s*$')
    if (!$match.Success) { throw "Could not provision fixture credential." }
    $match.Groups[1].Value
}

try {
    & initdb -D $cluster -U postgres --encoding=UTF8 --locale=C --auth=scram-sha-256 "--pwfile=$pwFile" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "initdb failed." }
    Remove-Item -LiteralPath $pwFile
    & pg_ctl -D $cluster -l $pgLog -o "-p $env:PGPORT -h 127.0.0.1" -w start
    if ($LASTEXITCODE -ne 0) { throw "Private PostgreSQL startup failed." }
    $startedPg = $true
    Get-ChildItem db/*.sql | Sort-Object Name | ForEach-Object { Query -f $_.FullName | Out-Null }

    & dotnet publish src/Transport.Api -c Release -o $apiDir
    if ($LASTEXITCODE -ne 0) { throw "API build failed." }
    $env:Transport__Access = "Company"
    $env:ConnectionStrings__Transport = "Host=127.0.0.1;Port=$env:PGPORT;Database=postgres;Username=postgres;Password=$env:PGPASSWORD"
    $env:TRANSPORT_API_URL = "http://127.0.0.1:55181/"
    $env:ASPNETCORE_URLS = $env:TRANSPORT_API_URL
    $server = Start-Process dotnet -ArgumentList "Transport.Api.dll" -WorkingDirectory $apiDir -PassThru
    $ready = $false
    foreach ($attempt in 1..60) {
        if ($server.HasExited) { throw "Company API exited before becoming ready." }
        try {
            $null = Invoke-RestMethod ($env:TRANSPORT_API_URL+"api/trajets") -TimeoutSec 2
            $ready = $true
            break
        } catch { Start-Sleep -Milliseconds 500 }
    }
    if (!$ready) { throw "Company API did not become ready." }
    $company = Query -c "SELECT id FROM company WHERE access_mode='Company'"
    $agency = Query -v "company_id=$company" -v "company_name=Compagnie MSI" -v "entity_name=Entite MSI" -v "country=CI" -v "agency_name=Gare MSI" -v template=1 -v stop=0 -f ops/ouvrir-compagnie.sql
    $env:TRANSPORT_FIXTURE_DESK = Read-Key (Query -v "agency=$agency" -v "label=Poste MSI" -f ops/inscrire-poste.sql)
    $env:TRANSPORT_FIXTURE_AGENT = Read-Key (Query -v "agency=$agency" -v "name=Agent MSI" -v "rights=vente" -f ops/inscrire-agent.sql)
    $ticket = & powershell.exe -NoProfile -NonInteractive -STA -File tests/vente-windows.ps1 -Executable $Executable
    if ($LASTEXITCODE -ne 0 -or $ticket -notmatch '^[0-9a-f-]{36}$') { throw "Installed counter sale failed." }
    $sql = @"
SELECT count(*) FROM cash_entry e JOIN booking b ON b.id=e.booking_id
JOIN cash_session s ON s.id=e.session_id JOIN counter_desk d ON d.id=s.desk_id
WHERE e.ticket='$([guid]$ticket)' AND b.passenger='Voyageur MSI' AND b.state='sold'
  AND e.xof=b.xof AND e.xof>0 AND d.agency_id='$([guid]$agency)'
"@
    if ((Query -c $sql) -ne "1") { throw "Ticket from the installed client does not match the cash ledger." }
} finally {
    if ($server -and !$server.HasExited) { Stop-Process -Id $server.Id -Force }
    if ($startedPg) { & pg_ctl -D $cluster -m fast -w stop }
    if (Test-Path -LiteralPath $pwFile) { Remove-Item -LiteralPath $pwFile }
}
