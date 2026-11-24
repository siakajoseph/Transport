"""Vente marchande fictive sur le serveur extrait de l’archive, puis reprise."""
import json
import os
from pathlib import Path
import sys
import subprocess
from urllib.request import Request, urlopen
from uuid import uuid4


def post(path, body, key=None):
    request = Request("http://127.0.0.1:5080/api/" + path,
                      data=json.dumps(body).encode(), headers={"Content-Type":"application/json", **({"Authorization":"Bearer "+key} if key else {})})
    with urlopen(request, timeout=15) as response:
        return json.load(response)


def open_from_package(company):
    def mandate(target):
        return subprocess.run([
            "pwsh","-NoProfile","-Command",
            "$ErrorActionPreference='Stop'; ./preparer-mandat.ps1 -Operateur 'Agent livraison' "
            "-Compagnie $env:TARGET_COMPANY | ConvertTo-Json -Compress"],
            env={**os.environ,"TARGET_COMPANY":target},capture_output=True,text=True)
    if mandate(str(uuid4())).returncode == 0:
        raise ValueError("Le paquet a accepté un identifiant de compagnie étranger")
    result=mandate(company)
    if result.returncode:
        raise RuntimeError("Émission du mandat de recette impossible")
    grant=json.loads(result.stdout)
    if grant["Portail"] != "http://127.0.0.1:5080/editeur/index.html":
        raise ValueError("Portail du mandat incorrect")
    data=dict(company="Transport Recette",entity="Exploitation CI",country="CI",
              agency="Abidjan",template=1,stop=0)
    opened=post("editeur/ouverture",data,grant["Cle"])
    if not opened["agencyId"] or opened != post("editeur/ouverture",data,grant["Cle"]):
        raise ValueError("Ouverture de compagnie non reproductible")
    if mandate(company).returncode == 0:
        raise ValueError("Nouveau mandat admis après ouverture")


def purchase():
    reference = str(uuid4())
    order = post("reservations", dict(reference=reference, tripId=1, number=1, origin=0,
                                      destination=2, passenger="Voyageur de recette"))
    if order["company"] != "Transport Recette" or order["xof"] != 6000:
        raise ValueError("Compagnie ou montant de l’option incorrect")
    path = f"reservations/{reference}/simulation"
    payment = post(path, {})["payment"]
    path += "/" + payment
    receipt = post(path, {})
    if receipt["outcome"] != "issued" or not receipt["token"]:
        raise ValueError("Billet attendu après le règlement fictif")
    return {"path":path, "token":receipt["token"]}


if __name__ == "__main__":
    action, file = sys.argv[1:]
    state = Path(file)
    if action == "ouvrir":
        open_from_package(file)
    elif action == "payer":
        state.write_text(json.dumps(purchase()))
    elif action == "reprendre":
        saved = json.loads(state.read_text())
        receipt = post(saved["path"], {})
        if receipt["outcome"] != "issued" or receipt["token"] != saved["token"]:
            raise ValueError("La reprise doit restituer le billet initial")
    else:
        raise ValueError("Action inconnue")
