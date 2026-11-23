"""One hundred distinct customers compete on one company trip and one seat."""
from concurrent.futures import ThreadPoolExecutor
from threading import Barrier
import subprocess
import time
import unittest
from uuid import uuid4

from test_companies import call, enroll, psql


def restart_api(company, port):
    subprocess.run(["docker", "compose", "-p", company, "restart", "api"], check=True)
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        try:
            if call(port, "/trajets")[0] == 200:
                return
        except OSError:
            pass
        time.sleep(0.3)
    raise AssertionError("L’API de compagnie ne répond pas après redémarrage.")


class MixedInventoryTests(unittest.TestCase):
    def test_one_hundred_requests_one_sale(self):
        company, port = "transport-beta", 5182
        # Begin with an unused per-IP window, keeping the public limit in force.
        restart_api(company, port)
        desk = enroll(company, 0, "Guichet de qualification")
        trip = int(psql(company, "SELECT schedule_trip(1,now()+interval '2 days',1)"))
        psql(company, f"DELETE FROM seat WHERE trip_id={trip} AND number<>1")
        gate = Barrier(100, timeout=30)

        def attempt(index):
            passenger = "Voyageur fictif " + str(index)
            payload = dict(tripId=trip, number=1, origin=0, destination=2, passenger=passenger)
            web = index % 13 == 0
            if web:
                payload["reference"] = str(uuid4())
            gate.wait()
            if web:
                status, result = call(port, "/reservations", data=payload)
            else:
                status, result = call(port, "/options", desk, payload)
            return status, web, result

        with ThreadPoolExecutor(max_workers=100) as pool:
            responses = list(pool.map(attempt, range(100)))
        statuses = [r[0] for r in responses]
        self.assertCountEqual([200] + [409] * 99, statuses)
        _, web, winner = next(r for r in responses if r[0] == 200)
        if web:
            path = "/reservations/" + winner["reference"] + "/simulation"
            status, charge = call(port, path, data={})
            self.assertEqual(200, status)
            payment = path + "/" + charge["payment"]
            settle = lambda: call(port, payment, data={})
        else:
            status, shift = call(port, "/caisse", desk, dict(agent="Qualification", opening=0))
            self.assertEqual(200, status)
            payment = "/options/" + winner["id"] + "/especes?session=" + shift["id"]
            settle = lambda: call(port, payment, desk, {})
        first = settle()
        self.assertEqual(200, first[0])
        restart_api(company, port)
        self.assertEqual(first, settle())
        self.assertEqual("1|1|6000", psql(company, f"""
            SELECT count(b.id),count(e.ticket),sum(e.xof) FROM booking b
            LEFT JOIN cash_entry e ON e.booking_id=b.id WHERE b.trip_id={trip} AND b.state='sold'"""))


if __name__ == "__main__":
    unittest.main(verbosity=2)
