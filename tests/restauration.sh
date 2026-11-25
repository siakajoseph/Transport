#!/usr/bin/env bash
set -euo pipefail
project=transport-alpha
db=(docker compose -p "$project" exec -T -u postgres db)
tmp=$("${db[@]}" mktemp -d)
trap '"${db[@]}" rm -rf "$tmp"' EXIT
sql() { "${db[@]}" psql -XAt --set=ON_ERROR_STOP=1 -d "$1" -c "$2"; }
company=$(sql transport_demo 'SELECT id FROM company')
foreign=$(docker compose -p transport-beta exec -T -u postgres db psql -XAt -d transport_demo -c 'SELECT id FROM company')
ledger="SELECT ticket, booking_id, channel, xof FROM cash_entry ORDER BY ticket"
# Quiesce the only application writer for this planned recovery drill.
docker compose -p "$project" stop api
before=$(sql transport_demo "$ledger")
[[ -n "$before" && "$company" != "$foreign" ]]
"${db[@]}" pg_dump -Fc -f "$tmp/company.dump" transport_demo
restore() {
  "${db[@]}" bash -s -- "$tmp/company.dump" "$1" "$2" < ops/restaurer-compagnie.sh
}
restore "$company" transport_reprise_alpha
[[ "$(sql transport_reprise_alpha "$ledger")" == "$before" ]]
if restore "$foreign" transport_reprise_etrangere; then exit 1; fi
if restore "$company" transport_reprise_alpha; then exit 1; fi
[[ "$(sql transport_demo "$ledger")" == "$before" ]]

# Preserve the company URL and Company profile when switching the API.
TRANSPORT_PORT=5181 TRANSPORT_API_DATABASE=transport_reprise_alpha docker compose -p "$project" up -d --no-deps api
curl --retry 25 --retry-delay 1 --retry-all-errors -fsS http://127.0.0.1:5181/api/trajets >/dev/null
python3 tests/reprise_api.py
[[ "$(sql transport_demo "$ledger")" == "$before" ]]
[[ "$(sql transport_reprise_alpha "$ledger")" == "$before" ]]
