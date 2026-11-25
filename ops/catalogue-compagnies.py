"""Consolider les fiches de souscription de services PostgreSQL explicitement autorisés."""
import argparse
import json
import os
import re
import subprocess
import sys


def catalogue(services):
    entries = {}
    for service in services:
        if not re.fullmatch(r"[A-Za-z0-9_-]+", service):
            raise ValueError("Nom de service PostgreSQL invalide.")
        result = subprocess.run(
            ["psql", "-XqAtw", "-v", "ON_ERROR_STOP=1", "-c",
             "SELECT row_to_json(c) FROM public.publisher_catalog_entry c"],
            env={**os.environ, "PGSERVICE": service, "PGCONNECT_TIMEOUT": "5"},
            capture_output=True, text=True, timeout=15)
        if result.returncode or not result.stdout.strip():
            raise ValueError(f"{service} : fiche indisponible. Vérifier le service, ses droits et l’ouverture.")
        entry = json.loads(result.stdout)
        if entry["company_id"] in entries:
            raise ValueError(f"{service} : cette compagnie figure déjà dans le catalogue.")
        entries[entry["company_id"]] = entry
    return sorted(entries.values(), key=lambda item: (item["company_name"], item["company_id"]))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("services", nargs="+", help="Noms configurés dans pg_service.conf")
    try:
        print(json.dumps(catalogue(parser.parse_args().services), indent=2, ensure_ascii=False))
    except (ValueError, OSError, subprocess.TimeoutExpired) as error:
        sys.exit(str(error))
