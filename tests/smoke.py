"""Run against a locally started OpenDrop server using an empty test inbox."""
from contextlib import closing
import hashlib
import http.client
import json
import pathlib
import socket
import sys
import time
import urllib.parse

port = int(sys.argv[1]) if len(sys.argv) > 1 else 5180


def request(method, path, body=None, headers=None):
    with_connection = http.client.HTTPConnection("127.0.0.1", port, timeout=10)
    try:
        with_connection.request(method, path, body=body, headers=headers or {})
        response = with_connection.getresponse()
        return response.status, response.read()
    finally:
        with_connection.close()


assert request("GET", "/", headers={"Host": f"outside.invalid:{port}"})[0] == 400
for path in ("/", "/owner", "/send.js", "/owner.js", "/style.css"):
    assert request("GET", path)[0] == 200, path
assert request("GET", "/owner.html")[0] == 404
code, png = request("GET", "/owner/qr.png")
assert code == 200 and png.startswith(b"\x89PNG\r\n\x1a\n")
code, dashboard = request("GET", "/owner/invitation")
assert code == 200
info = json.loads(dashboard)
token = urllib.parse.parse_qs(urllib.parse.urlsplit(info["invitation"]).fragment)["pair"][0]
headers = {"Authorization": f"Bearer {token}", "Content-Type": "application/octet-stream"}
inbox = pathlib.Path(info["inbox"])
name = "Prüfung 📄.txt"
url = "/api/upload?name=" + urllib.parse.quote(name)
payload = bytes(range(256)) * 8192

assert request("POST", url, payload)[0] == 401
assert request("POST", url, b"test", headers | {"Authorization": "Bearer " + "0" * 64})[0] == 401
assert request("POST", url, b"test", headers | {"Content-Type": "text/plain"})[0] == 415
assert request("POST", "/api/upload?name=..%2Fout.txt", b"test", headers)[0] == 400

with closing(http.client.HTTPConnection("127.0.0.1", port, timeout=10)) as oversized:
    oversized.putrequest("POST", url)
    for key, value in (headers | {"Content-Length": str(100 * 1024 * 1024 + 1)}).items():
        oversized.putheader(key, value)
    oversized.endheaders()
    assert oversized.getresponse().status == 413

for size in (0, len(payload), len(payload)):
    code, body = request("POST", url, payload[:size], headers)
    assert code == 201, (code, body)
    receipt = json.loads(body)
    assert receipt["name"] == name and receipt["bytes"] == size
    assert hashlib.sha256((inbox / receipt["storedName"]).read_bytes()).digest() == (
        hashlib.sha256(payload[:size]).digest()
    )

with socket.create_connection(("127.0.0.1", port), timeout=10) as partial:
    partial.sendall((
        f"POST {url} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n"
        f"Authorization: Bearer {token}\r\nContent-Type: application/octet-stream\r\n"
        "Content-Length: 9999\r\n\r\nunfinished"
    ).encode("ascii"))
    for attempt in range(30):
        if list(inbox.glob("*.part")):
            break
        time.sleep(0.05)
    else:
        raise AssertionError("Partial upload did not start")
    assert request("POST", url, b"busy", headers)[0] == 429

for attempt in range(30):
    if not list(inbox.glob("*.part")):
        break
    time.sleep(0.05)
else:
    raise AssertionError("Aborted upload left an incomplete file")

_, dashboard = request("GET", "/owner/invitation")
receipts = json.loads(dashboard)["receipts"]
assert len(receipts) == 3
assert len({entry["storedName"] for entry in receipts}) == 3
assert len(list(inbox.iterdir())) == 3
print("OK: pages, QR, host check, pairing, upload integrity, duplicates, limits, concurrency, abort cleanup, receipts")
