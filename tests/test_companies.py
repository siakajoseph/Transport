"""Two fresh, separately named Compose projects, three agency counters, one seat."""
import json
import re
import subprocess
import unittest
from datetime import datetime, timezone
from pathlib import Path
from urllib.error import HTTPError
from urllib.request import Request, urlopen
from uuid import uuid4


def psql(company, query, variables=()):
    result = subprocess.run(
        ["docker", "compose", "-p", company, "exec", "-T", "db", "psql", "-XqAt",
         "-v", "ON_ERROR_STOP=1", "-U", "postgres", "-d", "transport_demo", *variables],
        input=query, capture_output=True, text=True, check=True)
    return result.stdout.strip()


def new_agent(company, agency, name, rights):
    output = psql(company, Path("ops/inscrire-agent.sql").read_text(),
                  ["-v", "agency="+agency, "-v", "name="+name, "-v", "rights="+rights])
    return re.search(r"Clé : ([0-9a-f]{64})", output).group(1)


def enroll(company, stop, name, rights="vente"):
    # Values here are generated exclusively by the test fixture.
    agency = str(uuid4())
    psql(company, f"""
        INSERT INTO agency(id,entity_id,name) SELECT '{agency}',id,'{name}' FROM legal_entity;
        INSERT INTO agency_origin VALUES(1,{stop},'{agency}');
    """)
    output = psql(company, Path("ops/inscrire-poste.sql").read_text(),
                  ["-v", "agency="+agency, "-v", "label="+name])
    desk = re.search(r"Clé : ([0-9a-f]{64})", output).group(1)
    return desk, new_agent(company, agency, name, rights)


def call(port, path, key=None, data=None):
    headers = {"Content-Type": "application/json"}
    if key:
        desk, staff = key
        headers["Authorization"] = "Bearer " + desk
        if staff:
            headers["X-Transport-Agent"] = staff
    request = Request(f"http://127.0.0.1:{port}/api"+path,
                      data=None if data is None else json.dumps(data).encode(), headers=headers)
    try:
        with urlopen(request, timeout=8) as res:
            return res.status, json.load(res)
    except HTTPError as exc:
        with exc:
            return exc.code, None


class CompanyTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        for company in ("transport-alpha", "transport-beta"):
            psql(company, f"""UPDATE company SET name='{company}';
                INSERT INTO legal_entity(name,country) VALUES ('Exploitation CI','CI');""")

    def test_counter_and_public_isolation(self):
        a = enroll("transport-alpha", 0, "Abidjan A")
        y = enroll("transport-alpha", 1, "Yamoussoukro A")
        b = enroll("transport-beta", 0, "Abidjan B")
        alpha, beta = 5181, 5182
        self.assertEqual(401, call(beta, "/poste", a)[0])
        self.assertEqual(401, call(alpha, "/poste", b)[0])
        self.assertEqual(401, call(alpha, "/caisse?agent=inconnu")[0])
        profile = call(alpha, "/poste", a)[1]
        self.assertEqual("Abidjan A", profile["agency"])
        self.assertNotEqual(profile["companyId"],call(beta, "/poste", b)[1]["companyId"])
        self.assertEqual({0}, {r["origin"] for r in call(alpha,"/trajets",a)[1]})
        self.assertEqual({1}, {r["origin"] for r in call(alpha,"/trajets",y)[1]})
        self.assertEqual(403, call(alpha, "/voyages/1/manifeste", a)[0])
        params = dict(tripId=1,number=4,origin=0,destination=2,passenger="Voyageur")
        self.assertEqual(404,call(alpha,"/options",y,params)[0])
        opt = call(alpha,"/options",a,params)[1]["id"]
        other = call(beta,"/options",b,params)[1]["id"]
        self.assertNotEqual(opt,other)
        def open_shift(port,key):
            return call(port,"/caisse",key,{"agent":"valeur fournie par le client","opening":100})[1]["id"]
        shift, second, foreign = open_shift(alpha,a),open_shift(alpha,y),open_shift(beta,b)
        def collect(port,key,option,session):
            return call(port,f"/options/{option}/especes?session={session}",key,{})
        for port,key,option,session in [(alpha,y,opt,second),(alpha,a,opt,second),(beta,b,opt,foreign)]:
            self.assertEqual(404,collect(port,key,option,session)[0])
        self.assertEqual(404,call(alpha,f"/caisse/{shift}/cloture",y,{"counted":0})[0])
        sale = collect(alpha,a,opt,shift)[1]
        self.assertEqual(sale,collect(alpha,a,opt,shift)[1])
        ticket = "/billets/"+sale["ticket"]
        self.assertEqual(200,call(alpha,ticket,a)[0])
        self.assertEqual(404,call(alpha,ticket,y)[0])
        self.assertEqual(404,call(beta,ticket,b)[0])
        self.assertEqual(200,collect(beta,b,other,foreign)[0])
        day = datetime.now(timezone.utc).date().isoformat()
        report = "/caisse/rapport?agent=ignored&date="+day
        self.assertEqual([shift],[r["id"] for r in call(alpha,report,a)[1]])
        self.assertEqual([second],[r["id"] for r in call(alpha,report,y)[1]])
        self.assertEqual(6100,call(alpha,f"/caisse/{shift}/cloture",a,{"counted":6100})[1]["expected"])
        parcel = dict(tripId=1,origin=0,destination=2,grams=1000,lengthCm=10,widthCm=10,heightCm=10,requestId=str(uuid4()))
        ref = call(alpha,"/fret/preinscriptions",data=parcel)[1]["reference"]
        self.assertEqual(200,call(alpha,"/fret/suivi/"+ref)[0])
        self.assertEqual(404,call(beta,"/fret/suivi/"+ref)[0])
        self.assertEqual(401,call(alpha,"/fret/dossiers/"+ref)[0])
        psql("transport-alpha",f"UPDATE counter_desk SET enabled=false WHERE id='{profile['deskId']}'")
        self.assertEqual(401,call(alpha,"/poste",a)[0])

    def test_personal_sales_and_operational_roles(self):
        company, port = "transport-alpha", 5181
        desk = enroll(company, 0, "Gare sud")
        destination = enroll(company, 2, "Gare nord", "fret,embarquement,depart")
        agency = psql(company, "SELECT id FROM agency WHERE name='Gare sud'")
        def colleague(name, rights):
            return desk[0], new_agent(company, agency, name, rights)
        other = colleague("Deuxième vendeur", "vente")
        freight = colleague("Awa Fret", "fret")
        board = colleague("Moussa Contrôle", "embarquement")
        dispatch = colleague("Fatou Départ", "depart")
        def get(path, actor):
            return call(port,path,actor)
        def post(path, actor, **body):
            return call(port,path,actor,body)
        self.assertEqual(401,get("/poste",(desk[0],None))[0])
        self.assertEqual(401,get("/poste",(desk[0],destination[1]))[0])
        trip = int(psql(company, "SELECT schedule_trip(1,now()+interval '1 day',9)"))
        base = f"/voyages/{trip}"
        shift = post("/caisse",desk,agent="nom fourni",opening=0)[1]["id"]
        other_shift = post("/caisse",other,agent="nom fourni",opening=0)[1]["id"]
        self.assertNotEqual(shift,other_shift)
        option = post("/options",desk,tripId=trip,number=6,origin=0,destination=2,passenger="Voyageur")[1]["id"]
        self.assertEqual(404,post(f"/options/{option}/especes?session={other_shift}",other)[0])
        self.assertEqual(404,post(f"/options/{option}/especes?session={other_shift}",desk)[0])
        self.assertEqual(404,post(f"/caisse/{shift}/cloture",other,counted=0)[0])
        ticket = post(f"/options/{option}/especes?session={shift}",desk)[1]["ticket"]
        self.assertEqual(404,get("/billets/"+ticket,other)[0])
        token = get("/billets/"+ticket,desk)[1]["token"]
        scan = dict(tripId=trip,stop=0,token=token,agent="Nom du formulaire")
        self.assertEqual(403,post("/embarquement",desk,**scan)[0])
        self.assertEqual(404,post("/embarquement",destination,**scan)[0])
        self.assertEqual(200,post("/embarquement",board,**scan)[0])
        self.assertEqual(409,post("/embarquement",board,**scan)[0])
        self.assertEqual([],get(base+"/manifeste",destination)[1])
        self.assertEqual(1,len(get(base+"/manifeste",board)[1]))
        self.assertTrue(psql(company,f"SELECT agent FROM boarding WHERE ticket='{ticket}'").startswith("Moussa Contrôle ["))

        quote = dict(tripId=trip,origin=0,destination=2,grams=1000,lengthCm=10,widthCm=10,heightCm=10,requestId=str(uuid4()))
        ref = post("/fret/preinscriptions",None,**quote)[1]["reference"]
        parcel = "/fret/dossiers/"+ref
        freight_shift = post("/caisse",freight,agent="agent",opening=0)[1]["id"]
        receipt = dict(session=freight_shift,grams=1000,lengthCm=10,widthCm=10,heightCm=10,sender="Expéditeur",
                       senderPhone="0102030405",recipient="Destinataire",recipientPhone="0504030201",
                       nature="Standard",payment="dû",agreedXof=500)
        self.assertEqual(403,post(parcel+"/depot",desk,**receipt)[0])
        self.assertEqual(404,post(parcel+"/depot",destination,**receipt)[0])
        self.assertEqual(404,post(parcel+"/depot",freight,**dict(receipt,session=other_shift))[0])
        self.assertEqual(200,post(parcel+"/depot",freight,**receipt)[0])
        move = dict(tripId=trip,stop=0,agent="inconnu")
        self.assertEqual(404,post(parcel+"/mouvements/charger",destination,**move)[0])
        self.assertEqual(200,post(parcel+"/mouvements/charger",freight,**move)[0])
        self.assertEqual(403,post(base+"/depart",board,agent="agent")[0])
        self.assertEqual(404,post(base+"/depart",destination,agent="agent")[0])
        manifest = post(base+"/depart",dispatch,agent="inconnu")[1]
        self.assertTrue(manifest["agent"].startswith("Fatou Départ ["))
        self.assertEqual(manifest,get(base+"/manifeste-officiel",dispatch)[1])
        self.assertEqual(403,get(base+"/manifeste-officiel",board)[0])
        self.assertEqual(404,post(parcel+"/mouvements/arriver",freight,**dict(move,stop=2))[0])
        self.assertEqual(200,post(parcel+"/mouvements/arriver",destination,**dict(move,stop=2))[0])
        code = psql(company,f"SELECT code FROM freight_pickup_message WHERE reference='{ref}'")
        dest_shift = post("/caisse",destination,agent="agent",opening=0)[1]["id"]
        proof = dict(tripId=trip,stop=2,session=dest_shift,code=code,idSuffix="AB12",amount=500)
        self.assertEqual(404,post(parcel+"/retrait",freight,**proof)[0])
        self.assertEqual(404,post(parcel+"/retrait",destination,**dict(proof,session=freight_shift))[0])
        self.assertEqual(200,post(parcel+"/retrait",destination,**proof)[0])
        self.assertEqual(500,post(f"/caisse/{dest_shift}/cloture",destination,counted=500)[1]["expected"])
        staff_id = get("/poste",board)[1]["staffId"]
        psql(company,f"UPDATE staff_account SET enabled=false WHERE id='{staff_id}'")
        self.assertEqual(401,get("/poste",board)[0])
        self.assertEqual(200,get("/poste",dispatch)[0])


if __name__ == "__main__":
    unittest.main(verbosity=2)
