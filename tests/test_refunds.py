"""A company administrator explicitly authorized to refund simulated review receipts."""
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
import time
import unittest
from uuid import uuid4

from browser import http, mobile_page
from test_admin import bootstrap, otp
from test_companies import call, psql


class RefundTests(unittest.TestCase):
    def test_supervised_refund(self):
        company, port = "transport-beta", 5182
        root = f"http://127.0.0.1:{port}/api/admin/"
        login = "controle.test"
        password, secret = bootstrap(company, login)
        def credentials():
            return dict(login=login,password=password,code=otp(secret,int(time.time()//30)))
        status, reply = http(root+"session", "POST", credentials())
        self.assertEqual(200, status)
        auth = {"Authorization":"Bearer "+reply["token"]}
        self.assertEqual(403,http(root+"rapprochement",headers=auth)[0])
        self.assertEqual(401,http(root.replace("5182","5181")+"rapprochement",headers=auth)[0])
        target = psql(company, "SELECT id FROM company")
        def enable(value, operator=login):
            psql(company,Path("ops/habiliter-rapprochement-test.sql").read_text(),
                 ["-v","company_id="+target,"-v","login="+operator,"-v","enabled="+value])
        enable("true")
        def mismatch(seat):
            ref = str(uuid4())
            order = dict(reference=ref,tripId=1,origin=0,destination=2,number=seat,passenger="Cas simulé")
            self.assertEqual(200,call(port,"/reservations",data=order)[0])
            path = "/reservations/"+ref+"/simulation"
            payment = call(port,path,data={})[1]["payment"]
            psql(company,f"UPDATE sandbox_receipt SET amount=500 WHERE id='{payment}'")
            proof = path+"/"+payment
            self.assertEqual("review",call(port,proof,data={})[1]["outcome"])
            return payment,proof
        payment, proof = mismatch(18)
        url = root+"rapprochement/"+payment+"/remboursement"
        reason = dict(reason="Écart confirmé sur le simulateur")
        self.assertEqual(400,http(url,"POST",dict(reason=" "),auth)[0])
        with ThreadPoolExecutor(2) as pool:
            results=list(pool.map(lambda _: http(url,"POST",reason,auth),range(2)))
        self.assertEqual(results[0],results[1])
        self.assertEqual(200,results[0][0])
        refund=results[0][1]
        self.assertEqual((500,"XOF"),(refund["amount"],refund["currency"]))
        customer=call(port,proof,data={})[1]
        self.assertEqual(("refunded",refund["id"],None), (customer["outcome"],customer["refundId"],customer["qr"]))
        self.assertEqual(409,http(url,"POST",dict(reason="Autre décision"),auth)[0])
        self.assertEqual("expired", psql(company,f"""
            SELECT b.state FROM booking b JOIN web_order w ON w.booking_id=b.id
            JOIN sandbox_receipt r USING(reference) WHERE r.id='{payment}';"""))
        self.assertEqual("1",psql(company,f"SELECT count(*) FROM sandbox_refund WHERE payment_id='{payment}'"))
        enable("false")
        self.assertEqual(403,http(url,"POST",reason,auth)[0])
        self.assertEqual(403,http(root+"rapprochement",headers=auth)[0])

        # Use a separate named operator so the browser can sign in immediately with a fresh TOTP.
        operator = "finance.test"
        browser_password, browser_secret = bootstrap(company,operator)
        enable("true",operator)
        second, second_proof=mismatch(19)
        with mobile_page(self,root.replace("/api/admin/","/administration/index.html")) as (js,wait_for,_):
            js("const f=document.getElementById('login');for(const [k,v] of Object.entries(arguments[0])) f.elements[k].value=v;f.requestSubmit()",
               dict(login=operator,password=browser_password,code=otp(browser_secret,int(time.time()//30))))
            wait_for("!document.querySelector('#refunds').hidden && !document.querySelector('#office').inert")
            js("document.querySelector('#refunds summary').click()")
            wait_for("document.querySelector('#cases form') && !document.querySelector('#office').inert")
            js("const f=[...document.querySelectorAll('#cases form')].find(x=>x.textContent.includes(arguments[0]));f.querySelector('input').value='Double contrôle simulé';f.requestSubmit()",second)
            wait_for("document.querySelector('#status').textContent.includes('Remboursement simulé enregistré')")
        self.assertEqual("refunded",call(port,second_proof,data={})[1]["outcome"])
        self.assertEqual(operator,psql(company,f"""SELECT a.login FROM sandbox_refund f
            JOIN company_admin a ON a.id=f.admin_id WHERE f.payment_id='{second}';"""))


if __name__ == "__main__":
    unittest.main(verbosity=2)
