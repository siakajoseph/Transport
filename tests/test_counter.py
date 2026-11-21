"""Integration tests against an isolated, freshly seeded PostgreSQL-backed API."""
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timedelta, timezone
import json
import subprocess
from threading import Barrier
import unittest
from urllib.error import HTTPError
from urllib.request import Request, urlopen

BASE = "http://127.0.0.1:5080/api"


def request(path, body=None):
    data = json.dumps(body).encode() if body is not None else None
    req = Request(BASE + path, data=data, headers={"Content-Type": "application/json"})
    try:
        with urlopen(req, timeout=30) as response:
            return response.status, json.load(response)
    except HTTPError as exc:
        with exc:
            return exc.code, json.load(exc)


def hold(number, origin=0, destination=2):
    return request("/options", dict(tripId=1, number=number, origin=origin,
                                    destination=destination, passenger="Voyageur démo", xof=1))


def sql(query):
    result = subprocess.run(["docker", "compose", "exec", "-T", "db", "psql", "-v", "ON_ERROR_STOP=1",
                             "-U", "postgres", "-d", "transport_demo", "-Atc", query],
                            text=True, capture_output=True, check=True)
    return result.stdout.strip()


class CounterTests(unittest.TestCase):
    def test_concurrent_holds_and_cash_retries(self):
        start = Barrier(100, timeout=20)

        def reserve(_):
            start.wait()
            return hold(12)

        with ThreadPoolExecutor(max_workers=100) as pool:
            results = list(pool.map(reserve, range(100)))
        winners = [item for status, item in results if status == 200]
        self.assertEqual([200] + [409] * 99, sorted(status for status, _ in results))
        option = winners[0]
        self.assertEqual(6000, option["xof"])
        _, shift = request("/caisse", {"agent":"démo","opening":1000})
        session = shift["id"]
        path = f"/options/{option['id']}/especes?session={session}"
        with ThreadPoolExecutor(max_workers=16) as pool:
            payments = list(pool.map(lambda _: request(path, {}), range(24)))
        self.assertTrue(all(status == 200 for status, _ in payments))
        self.assertEqual(1, len({sale["ticket"] for _, sale in payments}))
        self.assertEqual("1|6000", sql(f"SELECT count(*),sum(xof) FROM cash_entry WHERE booking_id='{option['id']}'"))
        _, doc = request("/billets/" + payments[0][1]["ticket"])
        self.assertTrue(request("/billets/verifier", {"token":doc["token"]})[1]["valid"])
        damaged = doc["token"][:-1] + ("1" if doc["token"][-1] == "0" else "0")
        self.assertFalse(request("/billets/verifier", {"token":damaged})[1]["valid"])
        close = f"/caisse/{session}/cloture"
        self.assertEqual({"expected":7000,"counted":6900,"difference":-100},request(close,{"counted":6900})[1])
        self.assertEqual(200,request(path,{})[0])
        self.assertEqual(409, hold(12)[0])

    def test_baggage(self):
        _,shift=request("/caisse",{"agent":"bagages","opening":0})
        _,option=hold(25)
        _,sale=request(f"/options/{option['id']}/especes?session={shift['id']}",{})
        base="/billets/"+sale["ticket"]
        def quote(g):
            status,bag=request(base+"/bagages",{"session":shift["id"],"grams":g})
            self.assertEqual(200,status)
            return bag
        def confirm(b):
            return request("/bagages/"+b["id"]+"/confirmer",{})
        first=quote(19500)
        stale=quote(2000)
        self.assertEqual(0,first["fee"])
        self.assertEqual(200,confirm(first)[0])
        self.assertEqual(409,confirm(stale)[0])
        second=quote(1750)
        self.assertEqual(1000,second["fee"])
        with ThreadPoolExecutor(max_workers=8) as pool:
            self.assertTrue(all(code==200 for code in pool.map(lambda _:confirm(second)[0],range(8))))
        third=quote(500)
        self.assertEqual(0,third["fee"])
        self.assertEqual(200,confirm(third)[0])
        self.assertEqual(3,len(request(base+"/etiquettes")[1]))
        _,balance=request("/caisse/"+shift["id"]+"/cloture",{"counted":7000})
        self.assertEqual(0,balance["difference"])
        self.assertEqual(200,confirm(second)[0])

    def test_freight(self):
        from uuid import uuid4
        parcel=dict(tripId=1,origin=0,destination=2,grams=2000,lengthCm=50,widthCm=40,heightCm=30,requestId=str(uuid4()))
        self.assertEqual({"kg":12,"xof":6000},request("/fret/devis",parcel)[1])
        self.assertEqual({"kg":13,"xof":6500},request("/fret/devis",dict(parcel,grams=12500))[1])
        self.assertEqual(400,request("/fret/devis",dict(parcel,lengthCm=0))[0])
        with ThreadPoolExecutor(max_workers=10) as pool:
            replies=list(pool.map(lambda _:request("/fret/preinscriptions",parcel),range(10)))
        self.assertTrue(all(code==200 for code,_ in replies))
        self.assertEqual(1,len({body["reference"] for _,body in replies}))
        self.assertEqual(409,request("/fret/preinscriptions",dict(parcel,grams=5000))[0])
        with urlopen("http://127.0.0.1:5080/fret/index.html") as page:
            self.assertIn("Expédier un colis",page.read().decode())

    def test_cash_report(self):
        day=datetime.now(timezone.utc).date().isoformat()
        _,shift=request("/caisse",{"agent":"rapport","opening":1000})
        _,option=hold(26)
        _,sale=request(f"/options/{option['id']}/especes?session={shift['id']}",{})
        _,bag=request("/billets/"+sale["ticket"]+"/bagages",{"session":shift["id"],"grams":21000})
        request("/bagages/"+bag["id"]+"/confirmer",{})
        path="/caisse/rapport?agent=rapport&date="+day
        row=request(path)[1][0]
        self.assertEqual((1,6000,500,7500,None),(row["tickets"],row["ticketXof"],row["bagXof"],row["expected"],row["counted"]))
        request("/caisse/"+shift["id"]+"/cloture",{"counted":7450})
        self.assertEqual(-50,request(path)[1][0]["difference"])
        self.assertEqual([],request(path.replace("agent=rapport","agent=inconnu"))[1])
        # A shift can span two dates. Daily receipts follow payment time.
        sql(f"UPDATE cash_session SET opened_at=now()-interval '1 day' WHERE id='{shift['id']}'; UPDATE cash_entry SET paid_at=now()-interval '1 day' WHERE ticket='{sale['ticket']}'")
        row=request(path)[1][0]
        self.assertEqual((0,500,7500),(row["ticketXof"],row["bagXof"],row["expected"]))

    def test_boarding(self):
        _, shift = request("/caisse", {"agent":"embarquement","opening":0})
        _, option = hold(20,1,2)
        _, receipt = request(f"/options/{option['id']}/especes?session={shift['id']}", {})
        _, doc = request("/billets/" + receipt["ticket"])
        scan = dict(tripId=1, stop=1, token=doc["token"], agent="Contrôleur")
        path = "/embarquement"
        self.assertEqual(409, request(path,dict(scan, stop=0))[0])
        self.assertEqual(409, request(path,dict(scan, tripId=2))[0])
        self.assertEqual(400, request(path,dict(scan, token=scan["token"][:-1]+"!"))[0])
        with ThreadPoolExecutor(max_workers=16) as pool:
            results=list(pool.map(lambda _: request(path,scan)[0], range(16)))
        self.assertEqual([200]+[409]*15,sorted(results))
        _, rows=request("/voyages/1/manifeste")
        self.assertIsNotNone(next(row for row in rows if row["seat"]==20)["boardedAt"])

    def test_dispatch_freezes_manifest(self):
        sql("""INSERT INTO trip(id,departure) SELECT 900002,departure FROM trip WHERE id=1;
            INSERT INTO seat SELECT 900002,number FROM seat WHERE trip_id=1;
            INSERT INTO fare SELECT 900002,origin,destination,label,xof FROM fare WHERE trip_id=1;
            INSERT INTO trip_stop SELECT 900002,position,name FROM trip_stop WHERE trip_id=1""")
        _, shift=request("/caisse",{"agent":"gare","opening":0})

        def reserve(number,origin):
            return request("/options",dict(tripId=900002,number=number,origin=origin,destination=2,passenger=f"Client {number}"))

        def pay(option):
            return request(f"/options/{option['id']}/especes?session={shift['id']}",{})

        docs=[]
        for seat,origin in [(1,0),(2,1)]:
            status,option=reserve(seat,origin)
            self.assertEqual(200,status)
            status,sale=pay(option)
            self.assertEqual(200,status)
            docs.append(request("/billets/"+sale["ticket"])[1])
        _,bag=request("/billets/"+docs[0]["ticket"]+"/bagages",{"session":shift["id"],"grams":12500})
        self.assertEqual(200,request("/bagages/"+bag["id"]+"/confirmer",{})[0])
        _,racing=reserve(3,0)
        _,unused=reserve(4,0)
        barrier=Barrier(2,timeout=15)
        def racing_payment():
            barrier.wait()
            return pay(racing)
        def dispatch():
            barrier.wait()
            return request("/voyages/900002/depart",{"agent":"Chef de gare"})

        with ThreadPoolExecutor(max_workers=2) as pool:
            sale_future=pool.submit(racing_payment)
            depart_future=pool.submit(dispatch)
            sale_status,_=sale_future.result()
            status,manifest=depart_future.result()
        self.assertEqual(200,status)
        self.assertIn(sale_status,[200,409])
        self.assertEqual(2+int(sale_status==200),len(manifest["passengers"]))
        self.assertEqual([{"id":bag["id"],"grams":12500}],next(p for p in manifest["passengers"] if p["seat"]==1)["bags"])
        self.assertEqual(409,pay(unused)[0])
        self.assertEqual(409,reserve(5,0)[0])
        self.assertEqual([], request("/sieges?tripId=900002&origin=0&destination=2")[1])
        self.assertEqual(409, request("/embarquement",dict(tripId=900002,stop=0,token=docs[0]["token"],agent="agent"))[0])
        self.assertEqual(200, request("/embarquement",dict(tripId=900002,stop=1,token=docs[1]["token"],agent="agent"))[0])
        self.assertEqual(manifest,request("/voyages/900002/manifeste-officiel")[1])
        self.assertEqual(manifest,request("/voyages/900002/depart",{"agent":"Autre agent"})[1])
        live=request("/voyages/900002/manifeste")[1]
        self.assertIsNotNone(next(p for p in live if p["seat"]==2)["boardedAt"])
        self.assertIsNone(next(p for p in manifest["passengers"] if p["seat"]==2)["boardedAt"])

    def test_disjoint_and_overlapping_legs(self):
        self.assertEqual(200, hold(13, 0, 1)[0])
        self.assertEqual(200, hold(13, 1, 2)[0])
        self.assertEqual(409, hold(13, 0, 2)[0])

    def test_expired_option(self):
        status, item = hold(14)
        self.assertEqual(200, status)
        sql(f"UPDATE booking SET expires_at=now()-interval '1 second' WHERE id='{item['id']}'")
        _, seats = request("/sieges?tripId=1&origin=0&destination=2")
        self.assertTrue(next(s for s in seats if s["number"] == 14)["available"])
        _, shift = request("/caisse", {"agent":"expiration","opening":0})
        self.assertEqual(409, request(f"/options/{item['id']}/especes?session={shift['id']}", {})[0])
        self.assertEqual(200, hold(14)[0])
        self.assertEqual("0", sql(f"SELECT count(*) FROM cash_entry WHERE booking_id='{item['id']}'"))

    def test_programming(self):
        _,templates=request("/programmation/modeles")
        self.assertIn(1,[t["id"] for t in templates])
        future=(datetime.now(timezone.utc)+timedelta(days=2)).replace(second=0,microsecond=0).isoformat()
        data=dict(templateId=1,departureAt=future,coachNo=1)
        with ThreadPoolExecutor(max_workers=12) as pool:
            responses=list(pool.map(lambda _:request("/programmation",data),range(12)))
        self.assertTrue(all(status==200 for status,_ in responses))
        ids={body["id"] for _,body in responses}
        self.assertEqual(1,len(ids))
        trip=ids.pop()
        _,extra=request("/programmation",dict(data,coachNo=2))
        self.assertNotEqual(trip,extra["id"])
        self.assertEqual(400,request("/programmation",dict(data,departureAt="2020-01-01T00:00:00Z"))[0])
        self.assertEqual(404,request("/programmation",dict(data,templateId=987654))[0])
        _,routes=request("/trajets")
        self.assertEqual(3,len([r for r in routes if r["tripId"]==trip]))
        _,seats=request(f"/sieges?tripId={trip}&origin=0&destination=2")
        self.assertEqual(32,len(seats))
        self.assertTrue(all(s["available"] for s in seats))
        self.assertEqual([],request(f"/voyages/{trip}/manifeste")[1])
        status,option=request("/options",dict(tripId=trip,number=12,origin=0,destination=2,passenger="Démo"))
        self.assertEqual(200,status)
        self.assertEqual(6000,option["xof"])
        self.assertTrue(any(s["tripId"]==trip for s in request("/embarquement/arrets")[1]))

    def test_invalid_legs_and_seats(self):
        self.assertEqual(400, hold(15, 1, 0)[0])
        self.assertEqual(400, hold(999)[0])


if __name__ == "__main__":
    unittest.main(verbosity=2)
