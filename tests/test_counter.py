"""Integration tests against an isolated, freshly seeded PostgreSQL-backed API."""
from concurrent.futures import ThreadPoolExecutor
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
        sql("""INSERT INTO trip(id,departure) SELECT 2,departure FROM trip WHERE id=1;
            INSERT INTO seat SELECT 2,number FROM seat WHERE trip_id=1;
            INSERT INTO fare SELECT 2,origin,destination,label,xof FROM fare WHERE trip_id=1;
            INSERT INTO trip_stop SELECT 2,position,name FROM trip_stop WHERE trip_id=1""")
        _, shift=request("/caisse",{"agent":"gare","opening":0})

        def reserve(number,origin):
            return request("/options",dict(tripId=2,number=number,origin=origin,destination=2,passenger=f"Client {number}"))

        def pay(option):
            return request(f"/options/{option['id']}/especes?session={shift['id']}",{})

        docs=[]
        for seat,origin in [(1,0),(2,1)]:
            status,option=reserve(seat,origin)
            self.assertEqual(200,status)
            status,sale=pay(option)
            self.assertEqual(200,status)
            docs.append(request("/billets/"+sale["ticket"])[1])
        _,racing=reserve(3,0)
        _,unused=reserve(4,0)
        barrier=Barrier(2,timeout=15)
        def racing_payment():
            barrier.wait()
            return pay(racing)
        def dispatch():
            barrier.wait()
            return request("/voyages/2/depart",{"agent":"Chef de gare"})

        with ThreadPoolExecutor(max_workers=2) as pool:
            sale_future=pool.submit(racing_payment)
            depart_future=pool.submit(dispatch)
            sale_status,_=sale_future.result()
            status,manifest=depart_future.result()
        self.assertEqual(200,status)
        self.assertIn(sale_status,[200,409])
        self.assertEqual(2+int(sale_status==200),len(manifest["passengers"]))
        self.assertEqual(409,pay(unused)[0])
        self.assertEqual(409,reserve(5,0)[0])
        self.assertEqual([], request("/sieges?tripId=2&origin=0&destination=2")[1])
        self.assertEqual(409, request("/embarquement",dict(tripId=2,stop=0,token=docs[0]["token"],agent="agent"))[0])
        self.assertEqual(200, request("/embarquement",dict(tripId=2,stop=1,token=docs[1]["token"],agent="agent"))[0])
        self.assertEqual(manifest,request("/voyages/2/manifeste-officiel")[1])
        self.assertEqual(manifest,request("/voyages/2/depart",{"agent":"Autre agent"})[1])
        live=request("/voyages/2/manifeste")[1]
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

    def test_invalid_legs_and_seats(self):
        self.assertEqual(400, hold(15, 1, 0)[0])
        self.assertEqual(400, hold(999)[0])


if __name__ == "__main__":
    unittest.main(verbosity=2)
