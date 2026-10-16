"""Direct HTTPS upload bypassing broken DNS: TCP to a known-good IP,
TLS with proper SNI + Host header, streaming file body with Content-Length.
Usage: GH_TOK env var; args: <release_id> <file> <asset_name>"""
import os
import socket
import ssl
import sys

TOKEN = os.environ["GH_TOK"]
RELEASE_ID, FILE, NAME = sys.argv[1], sys.argv[2], sys.argv[3]
HOST = "uploads.github.com"
IP = sys.argv[4] if len(sys.argv) > 4 else "140.82.121.13"
TARGET = f"/repos/okaygamer795-coder/Spryzen/releases/{RELEASE_ID}/assets?name={NAME}"

size = os.path.getsize(FILE)
print(f"size={size}", flush=True)

s = socket.create_connection((IP, 443), timeout=30)
t = ssl.create_default_context().wrap_socket(s, server_hostname=HOST)
t.settimeout(180)

headers = (
    f"POST {TARGET} HTTP/1.1\r\n"
    f"Host: {HOST}\r\n"
    f"Authorization: Bearer {TOKEN}\r\n"
    f"Content-Type: application/octet-stream\r\n"
    f"Accept: application/vnd.github+json\r\n"
    f"Content-Length: {size}\r\n"
    f"Connection: close\r\n\r\n"
)
t.sendall(headers.encode())

sent = 0
with open(FILE, "rb") as f:
    while True:
        chunk = f.read(4 * 1024 * 1024)
        if not chunk:
            break
        t.sendall(chunk)
        sent += len(chunk)
print(f"sent={sent}/{size}", flush=True)

resp = b""
while True:
    try:
        c = t.recv(65536)
    except socket.timeout:
        break
    if not c:
        break
    resp += c
    if len(resp) > 100000:
        break
print("---- response ----")
print(resp[:2000].decode("utf-8", "replace"))
