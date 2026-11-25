"""Chrome mobile via le protocole WebDriver fourni par le runner Windows/Linux."""
from contextlib import contextmanager
import base64
from pathlib import Path
import json
import shutil
import subprocess
import time
from urllib.error import HTTPError
from urllib.request import Request, urlopen


def http(url, method="GET", data=None, headers=None, timeout=12):
    request = Request(url,method=method,headers={"Content-Type":"application/json",**(headers or {})},
                      data=json.dumps(data).encode() if data is not None else None)
    try:
        with urlopen(request,timeout=timeout) as response:
            raw=response.read()
            return response.status,json.loads(raw) if raw else None
    except HTTPError as error:
        with error:
            return error.code,json.loads(error.read() or b"null")


def capture(session, name):
    status, data = http(session + "/screenshot")
    if status != 200:
        raise RuntimeError("Capture du parcours indisponible.")
    path = Path("artifacts/apercus") / (name + ".png")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(base64.b64decode(data["value"], validate=True))


@contextmanager
def mobile_page(self, url):
    chromedriver=shutil.which("chromedriver")
    self.assertIsNotNone(chromedriver,"ChromeDriver requis pour la qualification du portail.")
    driver=subprocess.Popen([chromedriver,"--port=9595"],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
    root="http://127.0.0.1:9595"
    session=None
    try:
        for _ in range(100):
            try:
                if http(root+"/status")[0]==200: break
            except OSError: time.sleep(.1)
        chrome=shutil.which("google-chrome") or shutil.which("chromium")
        self.assertIsNotNone(chrome,"Navigateur Chrome requis pour la qualification.")
        status,data=http(root+"/session","POST",{"capabilities":{"alwaysMatch":{"browserName":"chrome",
            "goog:chromeOptions":{"binary":chrome,
                "mobileEmulation":{"deviceMetrics":{"width":390,"height":844,"pixelRatio":2}},
                "args":["--headless","--remote-debugging-pipe","--no-sandbox","--disable-dev-shm-usage",
                        "--disable-background-networking","--no-first-run"]}}}},timeout=90)
        self.assertEqual(200,status,data)
        session=root+"/session/"+data["value"]["sessionId"]
        status,_=http(session+"/url","POST",{"url":url})
        self.assertEqual(200,status)
        def js(source,*args):
            status,result=http(session+"/execute/sync","POST",{"script":source,"args":list(args)})
            self.assertEqual(200,status)
            return result["value"]
        def wait_for(condition):
            for _ in range(120):
                if js("return "+condition): return
                time.sleep(.1)
            self.fail("Condition non atteinte : "+condition)
        yield js, wait_for, session
    finally:
        if session: http(session,"DELETE")
        driver.terminate()
        driver.wait(timeout=10)
