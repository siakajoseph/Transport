#!/usr/bin/env bash
# Restore a trusted logical archive into a NEW candidate database in the selected cluster.
set -euo pipefail
archive=${1:?archive pg_dump -Fc requise}
expected=${2:?UUID de compagnie requis}
candidate=${3:?nom de la nouvelle base requis}
[[ -r "$archive" && "$expected" =~ ^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$ ]]
[[ "$candidate" =~ ^transport_reprise_[a-z0-9_]{1,40}$ ]]
createdb --template=template0 "$candidate"
# A failed candidate is retained for diagnosis. A retry needs a fresh database.
pg_restore --exit-on-error --single-transaction --dbname="$candidate" "$archive"
actual=$(psql -XAt --set=ON_ERROR_STOP=1 --dbname="$candidate" -c 'SELECT id FROM public.company')
if [[ "$actual" != "$expected" ]]; then
  echo "Compagnie inattendue. Base candidate à isoler." >&2
  exit 1
fi
echo "Restauration vérifiée pour la compagnie $actual dans $candidate."
