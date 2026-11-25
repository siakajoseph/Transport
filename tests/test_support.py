"""Diagnostic éditeur à usage unique, approuvé dans la compagnie concernée."""
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
import re
import subprocess
import time
import unittest

from browser import http, mobile_page
from test_admin import admin, bootstrap, otp
from test_companies import psql


def request_diagnostic(reason, company_id=None, minutes=15, technician="tech.support"):
    result = psql("transport-alpha", Path("ops/demander-diagnostic.sql").read_text(),
                  ["-v", "technician="+technician, "-v", "reason="+reason, "-v", "station=Poste test 01",
                   "-v", "company_id="+(company_id or psql("transport-alpha","SELECT id FROM company")), "-v", "minutes="+str(minutes)])
    return re.search(r"Demande : ([0-9a-f-]+)", result)[1], re.search(r"Clé : ([0-9a-f]{64})", result)[1]


def consult(key, token=None, port=5181):
    return http(f"http://127.0.0.1:{port}/api/editeur/support/diagnostic", "POST",
                headers={"X-Transport-Support":key, **({"Authorization":"Bearer "+token} if token else {})})


class SupportTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.password, cls.secret=bootstrap("transport-alpha","tech.support","support-init")
        cls.web_password, cls.web_secret=bootstrap("transport-alpha","tech.portail","support-init")

    def test_company_and_deadline_at_issue(self):
        other=psql("transport-beta","SELECT id FROM company")
        for arguments in ({"company_id":other},{"minutes":0},{"minutes":61}):
            with self.assertRaises(subprocess.CalledProcessError):
                request_diagnostic("Demande non valable", **arguments)
        self.assertEqual("0",psql("transport-alpha","SELECT count(*) FROM support_diagnostic WHERE reason='Demande non valable'"))
        ref, _=request_diagnostic("Durée demandée",minutes=7)
        self.assertEqual("t",psql("transport-alpha",f"SELECT expires_at-requested_at=interval '7 minutes' FROM support_diagnostic WHERE id='{ref}'"))

    def test_consent_and_audit(self):
        password, secret = bootstrap("transport-alpha", "direction.support")
        auth = dict(login="direction.support", password=password, code=otp(secret, int(time.time()//30)))
        token = admin("session","POST",auth)[1]["token"]
        ref, key = request_diagnostic("Contrôle des postes")
        approve = "support/"+ref+"/autoriser"
        endpoint="http://127.0.0.1:5181/api/editeur/support/session"
        credentials=dict(login="tech.support",password=self.password,code=otp(self.secret,int(time.time()//30)))
        self.assertEqual(401,admin("session","POST",credentials)[0])
        self.assertEqual(401,http(endpoint,"POST",dict(credentials,code="xxxxxx"))[0])
        status, session=http(endpoint,"POST",credentials)
        self.assertEqual(200,status)
        tech=session["token"]
        self.assertEqual(401,http(endpoint,"POST",credentials)[0])
        self.assertEqual(401,admin("annuaire",token=tech)[0])
        self.assertEqual(401,consult(key,tech)[0])
        self.assertEqual(401, consult(key)[0])
        self.assertEqual(401, admin(approve, "POST", token=key)[0])
        self.assertEqual(401, admin(approve, "POST", token=token, port=5182)[0])
        self.assertEqual(204, admin(approve, "POST", token=token)[0])
        self.assertEqual(401, consult(key,tech,5182)[0])
        self.assertEqual(401, consult(key,token)[0])
        self.assertEqual(401, consult(key)[0])
        foreign_ref, foreign_key=request_diagnostic("Autre technicien",technician="tech.portail")
        self.assertEqual(204,admin("support/"+foreign_ref+"/autoriser","POST",token=token)[0])
        self.assertEqual(401,consult(foreign_key,tech)[0])
        with ThreadPoolExecutor(2) as pool:
            results = list(pool.map(lambda _: consult(key,tech), range(2)))
        self.assertEqual([200,401], sorted(status for status,_ in results))
        report = next(body for status,body in results if status==200)
        self.assertEqual({"request","consultedAt","company","mode","agencies","enabledDesks"}, set(report))
        self.assertEqual((ref,"Company"), (report["request"],report["mode"]))
        self.assertEqual(int(psql("transport-alpha","SELECT count(*) FROM counter_desk WHERE enabled")), report["enabledDesks"])
        entry = next(row for row in admin("support",token=token)[1] if row["id"]==ref)
        self.assertEqual(("Consulté","direction.support",report["consultedAt"]),
                         (entry["state"],entry["approvedBy"],entry["consultedAt"]))
        self.assertEqual("Poste test 01",entry["station"])
        self.assertTrue(entry["approvedIp"] and entry["consultedIp"])
        self.assertIsNone(entry["revokedIp"])
        self.assertEqual(409, admin(approve, "POST", token=token)[0])

        for action in ("revoquer","expirer"):
            ref, key = request_diagnostic(action)
            prefix = "support/"+ref+"/"
            self.assertEqual(204, admin(prefix+"autoriser","POST",token=token)[0])
            if action=="revoquer":
                self.assertEqual(204, admin(prefix+action,"POST",token=token)[0])
            else:
                psql("transport-alpha",f"UPDATE support_diagnostic SET expires_at=now() WHERE id='{ref}'")
            self.assertEqual(401, consult(key,tech)[0])
            self.assertEqual(409, admin(prefix+"autoriser","POST",token=token)[0])
            entry = next(row for row in admin("support",token=token)[1] if row["id"]==ref)
            self.assertEqual("Révoqué" if action=="revoquer" else "Expiré", entry["state"])
            self.assertIsNone(entry["consultedAt"])
            self.assertEqual(action=="revoquer", bool(entry["revokedIp"]))

    def test_browser_diagnostic(self):
        ref, key = request_diagnostic("Contrôle navigateur",technician="tech.portail")
        password, secret = bootstrap("transport-alpha","support.portail")
        with mobile_page(self,"http://127.0.0.1:5181/administration/index.html") as (js, wait_for, session):
            js("const f=document.getElementById('login');for(const [k,v] of Object.entries(arguments[0])) f.elements[k].value=v;f.requestSubmit();",
               dict(login="support.portail",password=password,code=otp(secret,int(time.time()//30))))
            wait_for("!document.getElementById('office').hidden && !document.getElementById('office').inert")
            js("document.getElementById('support').open=true")
            wait_for("document.querySelector('#supportList button') && !document.getElementById('office').inert")
            js("[...document.querySelectorAll('#supportList li')].find(e=>e.textContent.includes('Contrôle navigateur')).querySelector('button').click()")
            wait_for("document.getElementById('supportList').textContent.includes('Accord de support.portail')")
            self.assertEqual(200,http(session+"/url","POST",{"url":"http://127.0.0.1:5181/editeur/support.html"})[0])
            js("for(const [id,v] of Object.entries(arguments[0])) document.getElementById(id).value=v;document.querySelector('form').requestSubmit()",
               dict(key=key,login="tech.portail",password=self.web_password,code=otp(self.web_secret,int(time.time()//30))))
            wait_for("document.getElementById('result').textContent.includes('Postes actifs')")
            self.assertIn(ref,js("return document.getElementById('result').textContent"))
            self.assertEqual(401,consult(key)[0])


if __name__=="__main__":
    unittest.main(verbosity=2)
