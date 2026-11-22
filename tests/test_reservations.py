"""Guest and staff share seat inventory, while each company owns its orders."""
from concurrent.futures import ThreadPoolExecutor
import unittest
from uuid import uuid4

from test_companies import call, enroll, psql


class GuestReservationTests(unittest.TestCase):
    def test_guest_hold_retry_expiry_and_company_boundary(self):
        company = "transport-alpha"
        desk = enroll(company, 0, "Vente en concurrence")
        ref = str(uuid4())
        request = dict(reference=ref, tripId=1, origin=0, destination=2, number=31, passenger="Awa Démo")

        def reserve():
            return call(5181, "/reservations", data=request)

        with ThreadPoolExecutor(max_workers=2) as pool:
            first, retry = pool.map(lambda _: reserve(), range(2))
        self.assertEqual(200, first[0])
        self.assertEqual(first, retry)
        self.assertEqual(6000, first[1]["xof"])
        self.assertEqual("transport-alpha", first[1]["company"])
        self.assertEqual(409, call(5181, "/reservations", data=dict(request, number=32))[0])
        self.assertEqual(409, call(5181, "/options", desk, request)[0])
        self.assertEqual(401, call(5181, "/options", data=request)[0])
        beta = call(5182, "/reservations", data=request)
        self.assertEqual(200, beta[0])
        self.assertEqual("transport-beta", beta[1]["company"])

        psql(company, f"""UPDATE booking SET expires_at=now()-interval '1 second'
            WHERE id=(SELECT booking_id FROM web_order WHERE reference='{ref}');""")
        self.assertEqual(409, reserve()[0])
        self.assertEqual(200, call(5181, "/options", desk, request)[0])
        self.assertEqual(409, reserve()[0])
        self.assertEqual("0", psql(company, f"""SELECT count(*) FROM cash_entry e
            JOIN web_order w USING(booking_id) WHERE w.reference='{ref}';"""))


if __name__ == "__main__":
    unittest.main()
