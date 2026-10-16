"""Split a big file into 5MB parts and upload each as a release asset.
Retries each part up to 3 times. Progress printed per part.
Usage: GH_TOK env var; args: <release_id> <file> <asset_prefix> [ip]"""
import os
import socket
import ssl
import sys
import time

TOKEN = os.environ["GH_TOK"]
RELEASE_ID, FILE, PREFIX = sys.argv[1], sys.argv[2], sys.argv[3]
HOST = "uploads.github.com"
IP = sys.argv[4] if len(sys.argv) > 4 else "140.82.121.13"
PART = 5 * 1024 * 1024

size = os.path.getsize(FILE)
nparts = (size + PART - 1) // PART
print(f"size={size} parts={nparts}", flush=True)


def upload(name, data):
    target = f"/repos/okaygamer795-coder/Spryzen/releases/{RELEASE_ID}/assets?name={name}"
    for attempt in range(1, 4):
        try:
            s = socket.create_connection((IP, 443), timeout=30)
            t = ssl.create_default_context().wrap_socket(s, server_hostname=HOST)
            t.settimeout(120)
            head = (
                f"POST {target} HTTP/1.1\r\nHost: {HOST}\r\n"
                f"Authorization: Bearer {TOKEN}\r\n"
                f"Content-Type: application/octet-stream\r\n"
                f"Content-Length: {len(data)}\r\nConnection: close\r\n\r\n"
            ).encode()
            t.sendall(head + data)
            resp = b""
            while True:
                try:
                    c = t.recv(65536)
                except socket.timeout:
                    break
                if not c:
                    break
                resp += c
                if len(resp) > 50000:
                    break
            t.close()
            if b"201 Created" in resp:
                return True
            print(f"  attempt {attempt}: unexpected response: {resp[:120]!r}", flush=True)
        except Exception as e:
            print(f"  attempt {attempt}: {type(e).__name__}", flush=True)
            time.sleep(5)
    return False


with open(FILE, "rb") as f:
    for i in range(nparts):
        chunk = f.read(PART)
        name = f"{PREFIX}.part{i + 1:02d}"
        ok = upload(name, chunk)
        print(f"part {i + 1}/{nparts} {name} {'OK' if ok else 'FAILED'}", flush=True)
        if not ok:
            print("ABORTED", flush=True)
            sys.exit(1)
print("ALL PARTS DONE", flush=True)
