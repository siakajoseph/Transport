"""Entitlements on two Company databases and the publisher's read-only consolidated catalogue."""
import json
import os
from pathlib import Path
import secrets
import subprocess
import sys
import tempfile
import unittest
from uuid import uuid4

from browser import mobile_page
from test_companies import call, psql


def change(company, revision, enabled):
    return psql(company, Path("ops/modifier-abonnement-web.sql").read_text(), [
        "-v", "company_id="+psql(company, "SELECT id FROM company"), "-v", f"revision={revision}",
        "-v", "enabled="+str(enabled).lower(), "-v", "reason=Recette souscription"])


class Subscriptions(unittest.TestCase):
    def test_web_and_catalogue(self):
        alpha, beta = "transport-alpha", "transport-beta"
        def availability(port):
            return call(port, "/reservations/disponibilite")[1]["open"]
        self.assertFalse(availability(5181))
        self.assertTrue(availability(5182))
        with mobile_page(self, "http://127.0.0.1:5181/reservation/index.html") as (js, wait_for, _):
            wait_for("document.querySelector('form').hidden")
            self.assertIn("fermées", js("return document.querySelector('[role=status]').textContent"))

        self.verify_catalogue([alpha, beta])
        data = dict(reference=str(uuid4()), tripId=1, origin=0, destination=2,
                    number=23, passenger="Voyageur Test")
        self.assertEqual(403, call(5181, "/reservations", data=data)[0])
        self.assertEqual("0", psql(alpha, "SELECT count(*) FROM booking WHERE passenger='Voyageur Test'"))
        order = call(5182, "/reservations", data=data)
        self.assertEqual(200, order[0])
        self.assertEqual("1", change(beta, 0, False))
        with self.assertRaises(subprocess.CalledProcessError):
            change(beta, 0, True)
        self.assertEqual(200, call(5182, "/reservations", data=data)[0])
        self.assertEqual(403, call(5182, "/reservations", data=dict(data,reference=str(uuid4()),number=24))[0])
        path = "/reservations/"+data["reference"]+"/simulation"
        payment = call(5182, path, data={})[1]["payment"]
        self.assertEqual("issued", call(5182, path+"/"+payment, data={})[1]["outcome"])
        self.assertEqual("2", change(beta, 1, True))
        self.assertEqual("2", change(alpha, 1, True))
        self.assertEqual("2", change(alpha, 2, True))
        self.assertEqual("2", psql(alpha, "SELECT count(*) FROM web_subscription_event"))
        self.assertTrue(availability(5181))

    def verify_catalogue(self, companies):
        with tempfile.TemporaryDirectory() as folder:
            service_file = Path(folder, "pg_service.conf")
            services = []
            for company in companies:
                password = secrets.token_hex(24)
                psql(company, f"""CREATE ROLE catalogue_lecture LOGIN PASSWORD '{password}';
                    GRANT SELECT ON publisher_catalog_entry TO catalogue_lecture;""")
                container = subprocess.check_output(["docker","compose","-p",company,"ps","-q","db"], text=True).strip()
                info = json.loads(subprocess.check_output(["docker","inspect",container]))[0]
                network = next(iter(info["NetworkSettings"]["Networks"].values()))
                services.append(f"[{company}]\nhost={network['IPAddress']}\nport=5432\ndbname=transport_demo\n"
                                f"user=catalogue_lecture\npassword={password}\n")
            service_file.write_text("\n".join(services))
            service_file.chmod(0o600)
            env = {**os.environ, "PGSERVICEFILE":str(service_file)}
            command = [sys.executable, "ops/catalogue-compagnies.py"]
            output = subprocess.check_output([*command,*reversed(companies)], env=env)
            rows = json.loads(output)
            self.assertEqual(companies, [row["company_name"] for row in rows])
            self.assertEqual([False,True], [row["web_enabled"] for row in rows])
            self.assertEqual({"company_id","company_name","opened_at","web_enabled","revision"}, set(rows[0]))
            duplicate = subprocess.run([*command,companies[0],companies[0]], env=env, capture_output=True)
            self.assertNotEqual(0, duplicate.returncode)
            self.assertEqual(b"", duplicate.stdout)
            denied = subprocess.run(["psql","-XqAtw","-c","SELECT id FROM booking LIMIT 1"],
                                    env={**env,"PGSERVICE":companies[0]},capture_output=True)
            self.assertNotEqual(0, denied.returncode)


if __name__ == "__main__":
    unittest.main(verbosity=2)
