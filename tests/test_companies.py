"""Two fresh, separately named Compose projects, three agency counters, one seat."""
from concurrent.futures import ThreadPoolExecutor
from browser import capture, http, mobile_page
import csv
import io
import json
import re
import secrets
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


def sms(port, path, key, method="GET"):
    return http(f"http://127.0.0.1:{port}/api/sms-test/"+path,method,
                headers={"X-Transport-Telephone":key})


def movements(company):
    target=psql(company,"SELECT id FROM company")
    day=psql(company,"SELECT (statement_timestamp() AT TIME ZONE 'UTC')::date")
    output=psql(company,Path("ops/exporter-mouvements.sql").read_text(),
                ["-v","company="+target,"-v","jour="+day])
    return list(csv.DictReader(io.StringIO(output)))


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
    return provision_desk(company, agency, name, rights)


def provision_desk(company, agency, name, rights="vente"):
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
        cls.first_agency = {}
        cls.targets = {}
        for company, agency in (("transport-alpha", "Abidjan A"), ("transport-beta", "Abidjan B")):
            target = psql(company, "SELECT id FROM company")
            cls.targets[company] = target
            with ThreadPoolExecutor(max_workers=2) as pool:
                ids = list(pool.map(lambda _: cls.open_company(company, target, agency), range(2)))
            if ids[0] != ids[1]:
                raise AssertionError("Une reprise a créé deux agences.")
            cls.first_agency[company] = ids[0]

    @staticmethod
    def open_company(company, target, agency):
        return psql(company, Path("ops/ouvrir-compagnie.sql").read_text(), [
            "-v", "company_id="+target, "-v", "company_name="+company,
            "-v", "entity_name=Exploitation CI", "-v", "country=CI",
            "-v", "agency_name="+agency, "-v", "template=1", "-v", "stop=0"])

    def test_virtual_phone_lifecycle(self):
        port=5181
        key=secrets.token_hex(32)
        with ThreadPoolExecutor(2) as pool:
            attempts=list(pool.map(lambda _:sms(port,"ligne",key,"POST"),range(2)))
        self.assertEqual(200,attempts[0][0])
        self.assertEqual(attempts[0],attempts[1])
        phone=attempts[0][1]["phone"]
        self.assertEqual(200,sms(port,"messages",key)[0])
        self.assertEqual(204,sms(port,"ligne",key,"DELETE")[0])
        self.assertEqual(401,sms(port,"messages",key)[0])
        next_line=sms(port,"ligne",key,"POST")[1]["phone"]
        self.assertNotEqual(phone,next_line)
        psql("transport-alpha",f"UPDATE sandbox_phone SET expires_at=now() WHERE phone='{next_line}'")
        self.assertEqual(401,sms(port,"messages",key)[0])

    def test_opening_guards(self):
        alpha = "transport-alpha"
        for target, agency in [(self.targets["transport-beta"], "Abidjan A"),
                               (self.targets[alpha], "Autre agence")]:
            with self.assertRaises(subprocess.CalledProcessError):
                self.open_company(alpha, target, agency)
        self.assertEqual("1", psql(alpha, "SELECT count(*) FROM company_opening"))
        self.assertEqual("1", psql(alpha, "SELECT count(*) FROM legal_entity"))
        self.assertEqual("0", psql(alpha, "SELECT count(*) FROM agency WHERE name='Autre agence'"))

    def test_counter_and_public_isolation(self):
        a = provision_desk("transport-alpha", self.first_agency["transport-alpha"], "Abidjan A")
        y = enroll("transport-alpha", 1, "Yamoussoukro A")
        b = provision_desk("transport-beta", self.first_agency["transport-beta"], "Abidjan B")
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
        self.assertEqual(403,post("/billets/"+ticket+"/bagages",board,session=shift,grams=21000)[0])
        self.assertEqual(404,post("/billets/"+ticket+"/bagages",other,session=other_shift,grams=21000)[0])
        self.assertEqual(404,post("/billets/"+ticket+"/bagages",desk,session=other_shift,grams=21000)[0])
        bag = post("/billets/"+ticket+"/bagages",desk,session=shift,grams=21000)[1]["id"]
        self.assertEqual(404,post("/bagages/"+bag+"/confirmer",other)[0])
        self.assertFalse(any(r["reference"]==bag for r in movements(company)))
        for _ in range(2):
            self.assertEqual(200,post("/bagages/"+bag+"/confirmer",desk)[0])
        self.assertEqual([("bagage","500",ticket,shift)],[(r["nature"],r["montant"],r["piece"],r["caisse"])
                         for r in movements(company) if r["reference"]==bag])
        prints="/billets/"+ticket+"/impressions"
        operation=prints+"/"+str(uuid4())
        self.assertEqual(404,post(operation,other)[0])
        with ThreadPoolExecutor(2) as pool:
            copies=list(pool.map(lambda _:post(operation,desk),range(2)))
        self.assertEqual(copies[0],copies[1])
        self.assertEqual((200,0),(copies[0][0],copies[0][1]["sequence"]))
        original=copies[0][1]["document"]
        self.assertEqual(("Voyageur",6000,1),(original["passenger"],original["xof"],original["version"]))
        with ThreadPoolExecutor(2) as pool:
            duplicates=list(pool.map(lambda _:post(prints+"/"+str(uuid4()),desk)[1],range(2)))
        self.assertEqual([1,2],sorted(d["sequence"] for d in duplicates))
        self.assertTrue(all(d["document"]==original for d in duplicates))
        self.assertEqual([2,1,0],[r["sequence"] for r in get(prints,desk)[1]])
        self.assertNotEqual(200,get(prints,board)[0])
        self.assertEqual("1",psql(company,f"SELECT count(*) FROM cash_entry WHERE ticket='{ticket}'"))
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
        phone_url = f"http://127.0.0.1:{port}/sms-test/index.html"
        js, wait_for, session = self.enterContext(mobile_page(self, phone_url))
        js("document.getElementById('create').click()")
        wait_for("!document.getElementById('inbox').hidden")
        phone = js("return document.getElementById('phone').textContent")
        key = js("return sessionStorage.getItem('transport.smsTest.line')")
        self.assertRegex(phone, r"^\+999[0-9]{12}$")
        other_key = secrets.token_hex(32)
        self.assertEqual(200,sms(port,"ligne",other_key,"POST")[0])
        self.assertEqual(401,sms(5182,"messages",key)[0])
        receipt = dict(session=freight_shift,grams=1000,lengthCm=10,widthCm=10,heightCm=10,sender="Expéditeur",
                       senderPhone="0102030405",recipient="Destinataire",recipientPhone=phone,
                       nature="Standard",payment="dû",agreedXof=500)
        self.assertEqual(403,post(parcel+"/depot",desk,**receipt)[0])
        self.assertEqual(404,post(parcel+"/depot",destination,**receipt)[0])
        self.assertEqual(404,post(parcel+"/depot",freight,**dict(receipt,session=other_shift))[0])
        psql(company,"INSERT INTO freight_goods VALUES ('Échantillon bloqué',false),('Livres',true)")
        self.assertEqual(["Livres","Échantillon bloqué"], sorted(get(parcel,freight)[1]["goodsCatalog"]))
        self.assertEqual(["Échantillon bloqué"],get(parcel,freight)[1]["blockedGoods"])
        for declaration in [{},{"goods":"Échantillon bloqué"},{"goods":"Hors liste"}]:
            self.assertEqual(409,post(parcel+"/depot",freight,**receipt,**declaration)[0])
        self.assertIsNone(get(parcel,freight)[1]["depositedAt"])
        receipt["goods"]="Livres"
        self.assertEqual(200,post(parcel+"/depot",freight,**receipt)[0])
        psql(company,"UPDATE freight_goods SET permitted=false WHERE label='Livres'")
        self.assertEqual(200,post(parcel+"/depot",freight,**receipt)[0])
        self.assertEqual("Livres",get(parcel,freight)[1]["goods"])
        lookup = "/fret/contacts/recherche"
        for role, phone, field in [(False, "0102030405", "sender"), (True, phone, "recipient")]:
            search = dict(recipient=role, phone=phone)
            self.assertEqual((200, {"name": receipt[field]}), call(port, lookup, freight, search))
            self.assertEqual(404, call(port, lookup, destination, search)[0])
            self.assertEqual(403, call(port, lookup, desk, search)[0])
            self.assertEqual(401, call(port, lookup, data=search)[0])
        status, archived = get(parcel, freight)
        self.assertEqual(200, status)
        self.assertEqual(("transport-alpha","dû",1,500),
                         (archived["company"], archived["payment"], archived["billedKg"], archived["xof"]))
        self.assertEqual(archived, get(parcel, freight)[1])
        self.assertEqual(404, get(parcel, destination)[0])
        self.assertEqual(403, get(parcel, desk)[0])
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
        js("location.reload()")
        wait_for("document.querySelector('#messages strong')")
        code = js("return document.querySelector('#messages strong').textContent")
        self.assertRegex(code,r"^[0-9A-F]{12}$")
        message = js("return document.getElementById('messages').textContent")
        self.assertIn(ref,message)
        self.assertIn("500 FCFA",message)
        self.assertEqual([],sms(port,"messages",other_key)[1]["messages"])
        self.assertEqual(401,sms(port,"messages",phone)[0])
        dest_shift = post("/caisse",destination,agent="agent",opening=0)[1]["id"]
        proof = dict(tripId=trip,stop=2,session=dest_shift,code=code,idSuffix="AB12",amount=500)
        self.assertEqual(404,post(parcel+"/retrait",freight,**proof)[0])
        self.assertEqual(404,post(parcel+"/retrait",destination,**dict(proof,session=freight_shift))[0])
        pickup_receipt = parcel + "/recu-retrait"
        self.assertEqual(404, get(pickup_receipt, destination)[0])
        status, handover = post(parcel+"/retrait",destination,**proof)
        self.assertEqual(200, status)
        js("document.getElementById('refresh').click()")
        wait_for("document.getElementById('status').textContent.includes('Aucun colis')")
        self.assertEqual(0,js("return document.querySelectorAll('#messages strong').length"))
        status, receipt_data = get(pickup_receipt, destination)
        self.assertEqual(200, status)
        self.assertEqual({"company", "destination", "reference", "recipient", "handedAt", "collected"}, set(receipt_data))
        self.assertEqual((ref, "Destinataire", handover["handedAt"], 500),
                         tuple(receipt_data[key] for key in ("reference","recipient","handedAt","collected")))
        self.assertEqual((200, receipt_data), get(pickup_receipt, destination))
        self.assertEqual(404, get(pickup_receipt, freight)[0])
        self.assertEqual(403, get(pickup_receipt, desk)[0])
        self.assertEqual(401, call(port, pickup_receipt)[0])
        self.assertEqual(500,post(f"/caisse/{dest_shift}/cloture",destination,counted=500)[1]["expected"])
        self.assertEqual([("fret_retrait","500",dest_shift)],[(r["nature"],r["montant"],r["caisse"])
                         for r in movements(company) if r["reference"]==ref])
        path = "/fret/suivi/" + ref + "?historique=true"
        self.assertEqual(404, call(5182, path)[0])
        status, public = call(port, path)
        self.assertEqual(200, status)
        labels = ["Préenregistré", "Déposé", "Chargé", "En transit", "En attente de retrait", "Livré"]
        self.assertEqual({"state", "area", "events"}, set(public))
        self.assertEqual(labels, [event["state"] for event in public["events"]])
        times = []
        for event in public["events"]:
            self.assertEqual({"state", "at"}, set(event))
            times.append(datetime.fromisoformat(event["at"].replace("Z", "+00:00")))
        self.assertEqual(sorted(times), times)
        url = f"http://127.0.0.1:{port}/fret/index.html#colis={ref}"
        self.assertEqual(200,http(session+"/url","POST",{"url":url})[0])
        wait_for("document.querySelectorAll('#timeline li').length === 6")
        js("document.querySelector('#suivi').scrollIntoView()")
        capture(session, "suivi-fret")
        self.assertEqual(url, js("return document.querySelector('#tracking-link').href"))
        self.assertTrue(js("return document.querySelector('#tracking-result').textContent.startsWith('Livré')"))
        js("document.querySelector('#tracking-ref').value=arguments[0];document.querySelector('#tracking').requestSubmit()", str(uuid4()))
        wait_for("document.querySelector('#tracking-result').textContent.includes('introuvable')")
        self.assertEqual(0, js("return document.querySelectorAll('#timeline li').length"))
        self.assertTrue(js("return document.querySelector('#tracking-link').hidden"))
        staff_id = get("/poste",board)[1]["staffId"]
        psql(company,f"UPDATE staff_account SET enabled=false WHERE id='{staff_id}'")
        self.assertEqual(401,get("/poste",board)[0])
        self.assertEqual(200,get("/poste",dispatch)[0])


if __name__ == "__main__":
    unittest.main(verbosity=2)
