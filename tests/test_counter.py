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
