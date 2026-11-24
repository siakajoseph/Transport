#!/usr/bin/env bash
set -euo pipefail
# Assemble into a new directory. The MSI comes from the Windows job of this run.
msi=$(realpath "${1:?MSI attendu}")
target=${2:?Dossier de sortie attendu}
revision=$(git rev-parse HEAD)
mkdir "$target"
cp "$msi" "$target/Transport-Guichet-x64.msi"
cp -r db ops "$target/"
cp installer/demarrer-recette.ps1 installer/preparer-mandat.ps1 "$target/"
printf '%s\n' "$revision" > "$target/revision.txt"

api="transport-recette-api:$revision"
db="transport-recette-postgres:$revision"
docker build -t "$api" src/Transport.Api
docker pull postgres:17
docker tag postgres:17 "$db"
docker save "$api" "$db" | gzip -1 > "$target/images.tar.gz"
python3 - "$target" "$api" "$db" <<'PY'
from pathlib import Path
import sys
target, api, db = sys.argv[1:]
source = Path("compose.yaml").read_text()
for old, new in [("build: ./src/Transport.Api", "image: "+api), ("image: postgres:17", "image: "+db)]:
    if source.count(old) != 1:
        raise SystemExit("Composition de livraison à vérifier.")
    source = source.replace(old,new)
Path(target,"compose.yaml").write_text(source)
PY
(cd "$target" && find . -type f -print0 | LC_ALL=C sort -z | xargs -0 sha256sum > ../transport-checksums.tmp && mv ../transport-checksums.tmp SHA256SUMS)
