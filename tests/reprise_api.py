"""Exercise the same company URL after switching the API to its restored database."""
from uuid import uuid4
import unittest

from test_companies import call, psql


class RecoveryTests(unittest.TestCase):
    def test_restored_payment_and_new_freight(self):
        company = "transport-alpha"
        stored = psql(company, """
            SELECT r.reference,r.id,d.token FROM sandbox_receipt r
            JOIN ticket_document d ON d.ticket=r.ticket
            WHERE r.outcome='issued' ORDER BY r.id LIMIT 1
        """)
        reference, payment, token = stored.split("|")
        path = f"/reservations/{reference}/simulation/{payment}"
        status, result = call(5181, path, data={})
        self.assertEqual(200, status)
        self.assertEqual(("issued", token), (result["outcome"], result["token"]))
        self.assertEqual(404, call(5182, path, data={})[0])
        self.assertEqual(401, call(5181, "/poste")[0])

        parcel = dict(tripId=1, origin=0, destination=2, grams=1000, lengthCm=10,
                      widthCm=10, heightCm=10, requestId=str(uuid4()))
        status, result = call(5181, "/fret/preinscriptions", data=parcel)
        self.assertEqual(200, status)
        ref = result["reference"]
        self.assertEqual("Préenregistré", call(5181, "/fret/suivi/"+ref)[1]["state"])
        self.assertEqual(404, call(5182, "/fret/suivi/"+ref)[0])
        self.assertEqual("0", psql(company, f"SELECT count(*) FROM freight_prereg WHERE reference='{ref}'"))


if __name__ == "__main__":
    unittest.main(verbosity=2)
