"""Vente marchande fictive sur le serveur extrait de l’archive, puis reprise."""
import json
from pathlib import Path
import sys
from urllib.request import Request, urlopen
from uuid import uuid4


def post(path, body):
    request = Request("http://127.0.0.1:5080/api/" + path,
                      data=json.dumps(body).encode(), headers={"Content-Type":"application/json"})
    with urlopen(request, timeout=15) as response:
        return json.load(response)


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
    if action == "payer":
        state.write_text(json.dumps(purchase()))
    elif action == "reprendre":
        saved = json.loads(state.read_text())
        receipt = post(saved["path"], {})
        if receipt["outcome"] != "issued" or receipt["token"] != saved["token"]:
            raise ValueError("La reprise doit restituer le billet initial")
    else:
        raise ValueError("Action inconnue")
