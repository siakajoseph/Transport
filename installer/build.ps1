$ErrorActionPreference = "Stop"
Push-Location (Join-Path $PSScriptRoot "..")
try {
    dotnet publish src/Transport.Guichet -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -p:DebugType=None -o artifacts/publish
    if ($LASTEXITCODE -ne 0) { throw "Échec de compilation du guichet." }
    $publish = (Resolve-Path artifacts/publish).Path
    $extra = @(Get-ChildItem $publish -File -Recurse | Where-Object { $_.Name -ne "Transport.Guichet.exe" -and $_.Extension -ne ".pdb" })
    if ($extra.Count) { throw "Fichiers à intégrer dans le MSI : $($extra.Name -join ', ')" }
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw "Échec de restauration WiX." }
    dotnet tool run wix -- build installer/Guichet.wxs -arch x64 -d "PublishDir=$publish" -o artifacts/Transport-Guichet-x64.msi
    if ($LASTEXITCODE -ne 0) { throw "Échec de création du MSI." }
} finally { Pop-Location }
