"""Guest and staff share seat inventory, while each company owns its orders."""
from concurrent.futures import ThreadPoolExecutor
import unittest
from uuid import uuid4

from browser import capture, mobile_page
from test_companies import call, enroll, psql


class GuestReservationTests(unittest.TestCase):
    def test_browser_reservation(self):
        with mobile_page(self, "http://127.0.0.1:5181/reservation/index.html") as (js, wait_for, session):
            wait_for("!document.querySelector('button').disabled")
            capture(session, "reservation")
            js("document.getElementById('passenger').value='Mariam Démo';document.querySelector('form').requestSubmit()")
            wait_for("document.querySelector('[role=status]').textContent.includes('Aucun paiement effectué')")
            self.assertIn("transport-alpha", js("return document.querySelector('[role=status]').textContent"))
            self.assertTrue(js("return document.documentElement.scrollWidth === window.innerWidth"))
            self.assertEqual("1", psql("transport-alpha", """
                SELECT count(*) FROM web_order w JOIN booking b ON b.id=w.booking_id
                WHERE b.passenger='Mariam Démo' AND b.state='hold';"""))

    def test_countdown_and_expiry(self):
        # Shorten only this fictitious passenger's hold on the ephemeral CI database.
        company = "transport-beta"
        psql(company, """
            CREATE FUNCTION short_test_hold() RETURNS trigger LANGUAGE plpgsql AS $fixture$
            BEGIN NEW.expires_at=clock_timestamp()+interval '4 seconds'; RETURN NEW; END $fixture$;
            CREATE TRIGGER short_test_hold BEFORE INSERT ON booking FOR EACH ROW
            WHEN (NEW.passenger='Option courte') EXECUTE FUNCTION short_test_hold();
        """)
        self.addCleanup(psql, company, "DROP TRIGGER short_test_hold ON booking; DROP FUNCTION short_test_hold()")
        with mobile_page(self, "http://127.0.0.1:5182/reservation/index.html") as (js, wait_for, _):
            wait_for("!document.querySelector('form button').disabled")
            seat=js("return document.querySelector('#seat').value")
            js("document.querySelector('#passenger').value='Option courte';document.querySelector('form').requestSubmit()")
            wait_for("!document.querySelector('#countdown').hidden")
            wait_for("!document.querySelector('#restart').hidden")
            self.assertTrue(js("return document.querySelector('#pay').hidden"))
            js("document.querySelector('#restart').click()")
            wait_for("!document.querySelector('form button').disabled")
            self.assertTrue(js("return [...document.querySelector('#seat').options].some(o=>o.value===arguments[0])",seat))
            self.assertEqual("0",psql(company,"SELECT count(*) FROM cash_entry e JOIN booking b ON b.id=e.booking_id WHERE b.passenger='Option courte'"))

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
        self.assertEqual(404,call(5181,"/reservations/reprise",data=dict(reference=ref))[0])
        self.assertEqual(409, reserve()[0])
        self.assertEqual(200, call(5181, "/options", desk, request)[0])
        self.assertEqual(409, reserve()[0])
        self.assertEqual("0", psql(company, f"""SELECT count(*) FROM cash_entry e
            JOIN web_order w USING(booking_id) WHERE w.reference='{ref}';"""))


if __name__ == "__main__":
    unittest.main()
