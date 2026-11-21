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


def enroll(company, stop, name):
    # Values here are generated exclusively by the test fixture.
    agency = str(uuid4())
    psql(company, f"""
        INSERT INTO agency(id,entity_id,name) SELECT '{agency}',id,'{name}' FROM legal_entity;
        INSERT INTO agency_origin VALUES(1,{stop},'{agency}');
    """)
    output = psql(company, Path("ops/inscrire-poste.sql").read_text(),
                  ["-v", "agency="+agency, "-v", "label="+name])
    return re.search(r"Clé : ([0-9a-f]{64})", output).group(1)


def call(port, path, key=None, data=None):
    headers = {"Content-Type": "application/json"}
    if key:
        headers["Authorization"] = "Bearer " + key
    request = Request(f"http://127.0.0.1:{port}/api"+path,
                      data=None if data is None else json.dumps(data).encode(), headers=headers)
    try:
        with urlopen(request, timeout=8) as res:
            return res.status, json.load(res)
    except HTTPError as exc:
        with exc:
            return exc.code, None


class CompanyTests(unittest.TestCase):
    def test_counter_and_public_isolation(self):
        for company in ("transport-alpha", "transport-beta"):
            psql(company, f"""UPDATE company SET name='{company}';
                INSERT INTO legal_entity(name,country) VALUES ('Exploitation CI','CI');""")
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


if __name__ == "__main__":
    unittest.main(verbosity=2)
