"""Reconciliation against the company sandbox ledger on two independent databases."""
from concurrent.futures import ThreadPoolExecutor
import base64
import io
import unittest

from PIL import Image
import zxingcpp
from uuid import uuid4

import pypdfium2 as pdfium
from browser import http, mobile_page
from test_companies import call, enroll, psql


class MerchantTests(unittest.TestCase):
    def open_order(self, seat):
        ref = str(uuid4())
        status, _ = call(5181, "/reservations", data=dict(reference=ref, tripId=1,
            number=seat, origin=0, destination=2, passenger="Aïcha Test"))
        self.assertEqual(200, status)
        path = f"/reservations/{ref}/simulation"
        status, charge = call(5181, path, data={})
        self.assertEqual(200, status)
        self.assertEqual((status, charge), call(5181, path, data={}))
        return ref, path + "/" + charge["payment"]

    def test_concurrent_confirmation_and_boarding(self):
        ref, path = self.open_order(27)
        self.assertEqual(404, call(5182, path, data={})[0])
        self.assertEqual(404, call(5181, path.replace(ref, str(uuid4())), data={})[0])
        with ThreadPoolExecutor(4) as pool:
            replies = list(pool.map(lambda _: call(5181, path, data={}), range(4)))
        self.assertTrue(all(reply == replies[0] for reply in replies))
        status, result = replies[0]
        self.assertEqual(200, status)
        self.assertEqual("issued", result["outcome"])
        desk = enroll("transport-alpha", 0, "Contrôle billet web", "embarquement")
        with Image.open(io.BytesIO(base64.b64decode(result["qr"]))) as image:
            scanned = zxingcpp.read_barcode(image).text
        self.assertEqual(result["token"], scanned)
        scan = dict(tripId=1, stop=0, token=scanned, agent="Contrôle")
        self.assertEqual(200, call(5181, "/embarquement", desk, scan)[0])
        self.assertEqual(409, call(5181, "/embarquement", desk, scan)[0])
        self.assertEqual("1", psql("transport-alpha", f"""
            SELECT count(*) FROM sandbox_receipt r JOIN cash_entry e ON e.ticket=r.ticket
            WHERE r.reference='{ref}' AND r.amount=e.xof AND r.amount=6000
            AND e.channel='sandbox' AND e.session_id IS NULL;"""))

    def test_settlement_exceptions(self):
        for number, change in [(28, "expiry"), (29, "amount"), (30, "currency")]:
            ref, path = self.open_order(number)
            if change == "expiry":
                psql("transport-alpha", f"""UPDATE booking SET expires_at=now()-interval '1 minute'
                    WHERE id=(SELECT booking_id FROM web_order WHERE reference='{ref}');""")
                desk = enroll("transport-alpha", 0, "Guichet de reprise")
                self.assertEqual(200, call(5181, "/options", desk, dict(tripId=1,
                    origin=0, destination=2, number=number, passenger="Autre voyageur"))[0])
            else:
                assignment = "amount=1" if change == "amount" else "currency='EUR'"
                psql("transport-alpha", f"UPDATE sandbox_receipt SET {assignment} WHERE reference='{ref}';")
            status, result = call(5181, path, data={})
            self.assertEqual(200, status)
            self.assertEqual("review", result["outcome"])
            self.assertIsNone(result["token"])
            self.assertIsNone(result["qr"])
            self.assertEqual((status, result), call(5181, path, data={}))

    def test_mobile_payment(self):
        with mobile_page(self, "http://127.0.0.1:5181/reservation/index.html") as (js, wait_for, session):
            wait_for("!document.querySelector('form button').disabled")
            js("document.querySelector('#passenger').value='Aminata Test';document.querySelector('form').requestSubmit()")
            wait_for("!document.querySelector('#pay').hidden")
            js("document.querySelector('#pay').click()")
            wait_for("!document.querySelector('#proof').hidden")
            token = js("return document.querySelector('code').textContent")
            self.assertEqual(101, len(token))
            js("location.reload()")
            wait_for("!document.querySelector('#pay').hidden")
            js("document.querySelector('#pay').click()")
            wait_for("!document.querySelector('#proof').hidden")
            self.assertEqual(token, js("return document.querySelector('code').textContent"))
            wait_for("document.querySelector('#qr').naturalWidth > 0")
            self.assertIn("Aminata Test",js("return document.querySelector('#journey').textContent"))
            status, printed = http(session+"/print","POST",{})
            self.assertEqual(200,status)
            with pdfium.PdfDocument(base64.b64decode(printed["value"])) as document:
                self.assertEqual(1,len(document))
                page=document[0]
                self.assertIn("Aminata Test",page.get_textpage().get_text_range())
                scanned=zxingcpp.read_barcode(page.render(scale=2).to_pil()).text
                self.assertEqual(token,scanned)
            js("document.querySelector('#finish').click()")
            wait_for("!document.querySelector('form button').disabled")
            self.assertTrue(js("return document.querySelector('#proof').hidden"))
            self.assertIsNone(js("return sessionStorage.getItem('transport.pendingSandboxPayment')"))


if __name__ == "__main__":
    unittest.main(verbosity=2)
