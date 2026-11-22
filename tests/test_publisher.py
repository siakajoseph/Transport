"""Opening a fresh company from an operator mandate, before counter and web sales."""
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
import re
import unittest

from browser import http, mobile_page
from test_companies import psql


class PublisherTests(unittest.TestCase):
    def test_mandate_to_first_agency(self):
        database = "transport-alpha"
        target = psql(database, "SELECT id FROM company")
        def mandate(operator):
            raw = psql(database, Path("ops/mandater-editeur.sql").read_text(),
                       ["-v", "company_id="+target, "-v", "operator_name="+operator])
            return re.search(r"Clé : ([0-9a-f]{64})", raw).group(1)

        key = mandate("Camille Test")
        second = mandate("Sam Test")
        headers = {"Authorization": "Bearer "+key}
        root = "http://127.0.0.1:5181/api/"
        other = "http://127.0.0.1:5182/api/"
        url = root+"editeur/ouverture"
        self.assertEqual(401, http(root+"editeur/mandat")[0])
        self.assertEqual(401, http(other+"editeur/mandat", headers=headers)[0])
        self.assertEqual(401, http(root+"admin/annuaire", headers=headers)[0])
        self.assertEqual(401, http(root+"poste", headers=headers)[0])

        data = dict(company=database, entity="Exploitation CI", country="CI",
                    agency="Abidjan A", template=1, stop=0)
        self.assertEqual(409, http(url, "POST", dict(data, stop=999), headers)[0])
        self.assertEqual("0", psql(database, "SELECT count(*) FROM legal_entity"))
        with mobile_page(self, "http://127.0.0.1:5181/editeur/index.html") as (js, wait_for, _):
            js("const f=document.querySelector('#access');f.elements.key.value=arguments[0];f.requestSubmit()", key)
            wait_for("!document.querySelector('#opening').hidden && !document.querySelector('#access').inert")
            self.assertIn("Camille Test", js("return document.querySelector('#identity').textContent"))
            js("const f=document.querySelector('#opening');for(const k of ['company','entity','country','agency']) f.elements[k].value=arguments[0][k];f.requestSubmit()", data)
            wait_for("document.querySelector('#status').textContent.includes('Compagnie ouverte')")
            js("document.querySelector('#exit').click()")
            wait_for("!document.querySelector('#access').hidden")

        with ThreadPoolExecutor(2) as pool:
            replies = list(pool.map(lambda _: http(url, "POST", data, headers), range(2)))
        self.assertEqual(200, replies[0][0])
        self.assertEqual(replies[0], replies[1])
        agency = replies[0][1]["agencyId"]
        self.assertEqual(agency, http(root+"editeur/mandat", headers=headers)[1]["agencyId"])
        self.assertEqual(409, http(url, "POST", dict(data, agency="Autre"), headers)[0])
        self.assertEqual(409, http(url, "POST", data, {"Authorization":"Bearer "+second})[0])
        self.assertEqual("Camille Test", psql(database, """
            SELECT operator_name FROM company_opening o JOIN publisher_mandate m ON m.id=o.mandate_id;"""))
        psql(database, "UPDATE publisher_mandate SET revoked_at=now() WHERE operator_name='Camille Test'")
        psql(database, "UPDATE publisher_mandate SET expires_at=now() WHERE operator_name='Sam Test'")
        for inactive in (key, second):
            self.assertEqual(401, http(url, "POST", data, {"Authorization":"Bearer "+inactive})[0])


if __name__ == "__main__":
    unittest.main(verbosity=2)
