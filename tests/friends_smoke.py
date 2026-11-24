import hashlib
import http.client
import json
import os
from pathlib import Path
import secrets
import ssl
import subprocess
import sys
import tempfile
import time
from urllib.parse import parse_qs, urlsplit

app = Path(sys.argv[1]).resolve()
ports = (5195, 5196)
root = Path(tempfile.mkdtemp(prefix="OpenDrop-friend-test-"))
children = []
files = []

def call(port, path, payload=None, *, remote=None, pin=None, token=None, data=None):
    headers = {"X-OpenDrop-UI": "nearby"}
    body = None
    if payload is not None:
        headers["Content-Type"] = "application/json"
        body = json.dumps(payload).encode()
    if data is not None:
        headers["Content-Type"] = "application/octet-stream"
        body = data
    if token:
        headers["Authorization"] = "Bearer " + token
    if remote:
        ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
        ctx.check_hostname = False
        ctx.verify_mode = ssl.CERT_NONE
        connection = http.client.HTTPSConnection(remote, port, timeout=6, context=ctx)
    else:
        connection = http.client.HTTPConnection("127.0.0.1", port, timeout=6)
    try:
        if remote:
            connection.connect()
            assert hashlib.sha256(connection.sock.getpeercert(binary_form=True)).hexdigest().upper() == pin
        connection.request("POST" if body is not None else "GET", path, body, headers)
        response = connection.getresponse()
        content = response.read()
        return response.status, json.loads(content) if content else None
    finally:
        connection.close()

def wait_for(check):
    for _ in range(65):
        try:
            result = check()
            if result:
                return result
        except (OSError, http.client.HTTPException):
            pass
        time.sleep(.3)
    raise AssertionError("Timed out waiting for server or multicast peer")

def state(port):
    return call(port, "/owner/nearby")[1]

def peer(port, target_port, *, friend):
    return next((device for device in state(port)["devices"]
                 if device["origin"] and urlsplit(device["origin"]).port == target_port
                 and device["isFriend"] is friend), None)

def offer(port, target, *, add_friend):
    secret = secrets.token_hex(32).upper()
    payload = {"address": target["address"], "secret": secret, "addFriend": add_friend}
    result = call(port, "/owner/request", payload)[1]
    assert result["state"] == "pending", result
    return result, payload | {"id": result["id"]}

def launch(port):
    env = os.environ.copy()
    env.update(OpenDrop__Port=str(port), OpenDrop__Inbox=str(root / f"inbox-{port}"),
               OpenDrop__FriendDirectory=str(root / f"friends-{port}"),
               WEBVIEW2_USER_DATA_FOLDER=str(root / f"profile-{port}"))
    startup = subprocess.STARTUPINFO()
    startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
    startup.wShowWindow = 0
    log = (root / f"{port}-{len(files)}.log").open("w")
    files.append(log)
    child = subprocess.Popen([str(app)], env=env, cwd=app.parent, startupinfo=startup, stdout=log, stderr=log)
    children.append(child)
    wait_for(lambda: isinstance(state(port), dict))
    return child

try:
    sender, receiver = ports
    launch(sender)
    old_receiver = launch(receiver)
    subprocess.run([sys.executable, str(Path(__file__).with_name("smoke.py")), str(sender)], check=True)

    for port in ports:
        assert not state(port)["visible"] and not state(port)["friendsVisible"]
        assert call(port, "/owner/nearby", {"enabled": True})[0] == 200

    target = wait_for(lambda: peer(sender, receiver, friend=False))
    original_friend_status, outgoing = offer(sender, target, add_friend=True)
    pending = call(receiver, "/owner/requests")[1][0]
    assert pending["addFriend"] and pending["code"] == original_friend_status["code"]
    result = call(receiver, "/owner/requests/respond", {"id": pending["id"], "accept": True, "addFriend": True})[1]
    assert result["friendAdded"]
    result = call(sender, "/owner/request/status", outgoing)[1]
    assert result["state"] == "accepted" and result["friendAdded"]

    for port in ports:
        assert len(state(port)["friends"]) == 1
        assert call(port, "/owner/nearby", {"enabled": False})[0] == 200
        assert state(port)["devices"] == []
        assert call(port, "/owner/friends/visible", {"enabled": True})[0] == 200

    target = wait_for(lambda: peer(sender, receiver, friend=True))
    origin = urlsplit(target["origin"])
    blocked = call(receiver, "/peer/request", {"name": "Unknown", "secret": secrets.token_hex(32).upper()},
                   remote=origin.hostname, pin=target["pin"])
    assert blocked[0] == 404, blocked

    _, outgoing = offer(sender, target, add_friend=False)
    pending = call(receiver, "/owner/requests")[1][0]
    assert call(receiver, "/owner/requests/respond", {"id": pending["id"], "accept": True})[1]["state"] == "accepted"
    answer = call(sender, "/owner/request/status", outgoing)[1]
    grant = parse_qs(urlsplit(answer["invitation"]).fragment)["pair"][0]
    assert call(receiver, "/api/upload?name=friend.txt", remote=origin.hostname, pin=target["pin"],
                token=grant, data=b"Hello friend")[0] == 201

    old_receiver.terminate()
    old_receiver.wait(10)
    launch(receiver)
    assert len(state(receiver)["friends"]) == 1
    assert not state(receiver)["friendsVisible"] and not state(receiver)["visible"]
    call(receiver, "/owner/friends/visible", {"enabled": True})
    target = wait_for(lambda: peer(sender, receiver, friend=True))
    assert call(sender, "/owner/friends/remove", {"id": state(sender)["friends"][0]["id"]})[0] == 200
    assert state(sender)["devices"] == [] and state(sender)["friends"] == []
    print("OK: two Windows processes, mutual friendship, private discovery, stranger rejected, HTTPS file transfer, restart and removal")
finally:
    for process in children:
        if process.poll() is None:
            process.terminate()
            process.wait(10)
    for log in files:
        log.close()
    print(f"Logs: {root}")
