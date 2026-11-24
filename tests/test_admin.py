"""Qualification de l’administration de compagnie, avec inscription TOTP réelle."""
import base64
from concurrent.futures import ThreadPoolExecutor
from hashlib import sha1, sha256
import hmac
import json
from pathlib import Path
import re
import secrets
import subprocess
import time
import unittest
from browser import http, mobile_page
from uuid import uuid4

from test_companies import call, psql


def otp(secret, step):
    code = hmac.new(secret, step.to_bytes(8, "big"), sha1).digest()
    offset = code[-1] & 15
    return f"{(int.from_bytes(code[offset:offset+4], 'big') & 0x7fffffff) % 1000000:06d}"


def bootstrap(company, login, command_name="admin-init"):
    password = secrets.token_urlsafe(24)
    command = ["docker","compose","-p",company,"exec","-T","api","dotnet","Transport.Api.dll",
               command_name,login,"--stdin"]
    with subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                          text=True, bufsize=1) as cli:
        cli.stdin.write(password+"\n")
        cli.stdin.flush()
        for line in cli.stdout:
            if line.startswith("Clé TOTP : "):
                secret = base64.b32decode(line.split()[-1])
                break
        else:
            raise RuntimeError("Échec de l’inscription administrateur.")
        if time.time()%30>28: time.sleep(2)
        cli.stdin.write(otp(secret,int(time.time()//30)-1)+"\n")
        cli.stdin.flush()
        if cli.wait(timeout=20):
            raise RuntimeError("Le second facteur n’a pas été inscrit.")
    return password,secret


def admin(path, method="GET", data=None, token=None, port=5181):
    return http(f"http://127.0.0.1:{port}/api/admin/"+path,method,data,
                {"Authorization":"Bearer "+token} if token else None)


class AdministrationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.agency=str(uuid4())
        psql("transport-alpha",f"""
            WITH entity AS (INSERT INTO legal_entity(name,country) VALUES ('Entité administration','CI') RETURNING id)
            INSERT INTO agency(id,entity_id,name) SELECT '{cls.agency}',id,'Gare administration' FROM entity;
        """)
        result=psql("transport-alpha",Path("ops/inscrire-poste.sql").read_text(),
                    ["-v","agency="+cls.agency,"-v","label=Poste administration"])
        cls.desk=re.search(r"Clé : ([a-f0-9]{64})",result).group(1)

    def test_two_factors_replay_scope_and_audit(self):
        password,secret=bootstrap("transport-alpha","admin.ci")
        code=otp(secret,int(time.time()//30))
        credentials=dict(login="admin.ci",password=password,code=code)
        self.assertEqual(401,admin("annuaire")[0])
        self.assertEqual(401,admin("session","POST",dict(credentials,password="incorrect"))[0])
        self.assertEqual(401,admin("session","POST",dict(credentials,code="xxxxxx"))[0])
        with ThreadPoolExecutor(2) as pool:
            replies=list(pool.map(lambda _:admin("session","POST",credentials),range(2)))
        self.assertEqual([200,401],sorted(x[0] for x in replies))
        token=next(body["token"] for status,body in replies if status==200)
        self.assertEqual(401,admin("annuaire",token=token,port=5182)[0])
        self.assertEqual(401,http("http://127.0.0.1:5181/api/editeur/mandat",
                                  headers={"Authorization":"Bearer "+token})[0])
        rule=dict(label="Carton fictif",permitted=True,revision=0)
        self.assertEqual(401,admin("marchandises","POST",rule,token,port=5182)[0])
        self.assertEqual(204,admin("marchandises","POST",rule,token)[0])
        update=dict(rule,permitted=False,revision=1)
        with ThreadPoolExecutor(2) as pool:
            replies=list(pool.map(lambda _:admin("marchandises","POST",update,token)[0],range(2)))
        self.assertEqual([204,409],sorted(replies))
        self.assertEqual("2",psql("transport-alpha","SELECT count(*) FROM freight_goods_event WHERE label='Carton fictif'"))
        history="marchandises/historique?label=Carton%20fictif"
        self.assertEqual(401,admin(history)[0])
        self.assertEqual(401,admin(history,token=token,port=5182)[0])
        entries=admin(history,token=token)[1]
        self.assertFalse(entries["more"])
        self.assertEqual([(2,True,False),(1,None,True)],
                         [(e["revision"],e["previous"],e["permitted"]) for e in entries["events"]])
        self.assertTrue(all(e["actor"]=="admin.ci" and e["at"] for e in entries["events"]))
        self.assertEqual(entries["events"][1:],admin(history+"&before=2",token=token)[1]["events"])
        key=secrets.token_hex(32)
        item=dict(id=str(uuid4()),agency=self.agency,name="Agent recette",rights=["vente"],
                  keyHash=sha256(key.encode()).hexdigest())
        self.assertEqual(200,admin("agents","POST",item,token)[0])
        self.assertEqual(200,admin("agents","POST",item,token)[0])
        self.assertEqual(401,admin("annuaire",token=self.desk)[0])
        self.assertEqual(401,call(5181,"/editeur/mandat",(self.desk,key))[0])
        profile=call(5181,"/poste",(self.desk,key))[1]
        self.assertEqual(["vente"],profile["rights"])
        path="agents/"+item["id"]
        change=dict(name="Agent recette",rights=["fret"],enabled=True,revision=1)
        with ThreadPoolExecutor(2) as pool:
            changes=list(pool.map(lambda _:admin(path,"PUT",change,token)[0],range(2)))
        self.assertEqual([200,409],sorted(changes))
        self.assertEqual(["fret"],call(5181,"/poste",(self.desk,key))[1]["rights"])
        self.assertEqual(200,admin(path,"PUT",dict(change,enabled=False,revision=2),token)[0])
        self.assertEqual(401,call(5181,"/poste",(self.desk,key))[0])
        events=[e for e in admin("historique",token=token)[1] if e["current"]["id"]==item["id"]]
        self.assertEqual([3,2,1],[e["current"]["revision"] for e in events])
        self.assertTrue(all(e["admin"]=="admin.ci" for e in events))
        self.assertIsNone(events[-1]["previous"])
        self.assertNotIn(item["keyHash"],json.dumps(events))
        self.assertEqual(204,admin("session","DELETE",token=token)[0])
        self.assertEqual(401,admin("annuaire",token=token)[0])

    def test_browser_agent_lifecycle(self):
        password,secret=bootstrap("transport-alpha","direction.ci")
        with mobile_page(self, "http://127.0.0.1:5181/administration/index.html") as (js, wait_for, session):
            js("const f=document.getElementById('login');for(const [k,v] of Object.entries(arguments[0])) f.elements[k].value=v;f.requestSubmit();",
               dict(login="direction.ci",password=password,code=otp(secret,int(time.time()//30))))
            wait_for("!document.getElementById('office').hidden && document.getElementById('agency').options.length>0 && !document.getElementById('office').inert")
            js("document.getElementById('add').click();document.getElementById('name').value='Agent interface';document.getElementById('agency').value=arguments[0];document.querySelector('[value=vente]').checked=true;document.getElementById('edit').requestSubmit();",self.agency)
            wait_for("!document.getElementById('delivery').hidden && !document.getElementById('office').inert")
            self.assertTrue(js("return document.documentElement.scrollWidth===window.innerWidth"))
            key=js("return document.getElementById('personal').textContent")
            self.assertEqual(200,call(5181,"/poste",(self.desk,key))[0])
            js("document.getElementById('dismiss').click();[...document.querySelectorAll('article')].find(e=>e.textContent.includes('Agent interface')).querySelector('button').click();document.getElementById('active').checked=false;")
            # ChromeDriver pauses script execution at native dialogs: submit and accept via WebDriver.
            js("setTimeout(()=>document.getElementById('edit').requestSubmit(),50)")
            time.sleep(.3)
            self.assertEqual(200,http(session+"/alert/accept","POST",{})[0])
            wait_for("document.getElementById('status').textContent==='Compte mis à jour.' && !document.getElementById('office').inert")
            self.assertEqual(401,call(5181,"/poste",(self.desk,key))[0])
            js("document.getElementById('goods').open=true")
            wait_for("document.querySelector('#goodsList li') && !document.getElementById('office').inert")
            js("document.getElementById('goodsName').value='Textile fictif';document.getElementById('newGoods').requestSubmit()")
            wait_for("document.getElementById('goodsList').textContent.includes('Textile fictif · Interdite') && !document.getElementById('office').inert")
            js("[...document.querySelectorAll('#goodsList > li')].find(e=>e.textContent.startsWith('Textile fictif')).querySelector('button.quiet').click()")
            wait_for("document.getElementById('goodsList').textContent.includes('direction.ci · Création → interdite') && !document.getElementById('office').inert")
            js("document.getElementById('logout').click()")
            wait_for("document.getElementById('office').hidden")



if __name__=="__main__":
    unittest.main(verbosity=2)
