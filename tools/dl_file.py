"""Download a file via short-lived Azure URL (DNS broken, use IP+SNI).
Usage: args: <urlfile> <outpath> <ip> ; prints size on completion."""
import socket
import ssl
import sys

URL = open(sys.argv[1]).read().strip()
OUT = sys.argv[2]
IP = sys.argv[3]
HOST = URL.split("//", 1)[1].split("/", 1)[0]
path = "/" + URL.split("//", 1)[1].split("/", 1)[1]

s = socket.create_connection((IP, 443), timeout=30)
t = ssl.create_default_context().wrap_socket(s, server_hostname=HOST)
t.settimeout(180)
t.sendall(f"GET {path} HTTP/1.1\r\nHost: {HOST}\r\nConnection: close\r\n\r\n".encode())

total = 0
with open(OUT, "wb") as f:
    first = True
    buf = b""
    while True:
        try:
            c = t.recv(65536)
        except socket.timeout:
            break
        if not c:
            break
        if first:
            buf += c
            if b"\r\n\r\n" in buf:
                head, rest = buf.split(b"\r\n\r\n", 1)
                print(head.decode("utf-8", "replace").split("\r\n")[0], flush=True)
                f.write(rest)
                total += len(rest)
                first = False
        else:
            f.write(c)
            total += len(c)
print(f"bytes={total}", flush=True)
