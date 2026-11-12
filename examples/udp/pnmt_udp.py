#!/usr/bin/env python3
"""A PNMT-UDP v1 client with nothing but Python's standard library (docs/udp-protocol.md).

Suites: NONE, TOKEN32, HMAC-SHA256/32 and /64, HMAC-MD5/32 and /64, HMAC-SHA1/32 and /64.
AES (CMAC and CCM) needs a crypto library and is left out on purpose.

    pnmt_udp.py --server pnmt.example.com:5028 --id 7 --key 000102…0F heartbeat --ack
    pnmt_udp.py … data --text "21.5C"
    pnmt_udp.py … loop --interval 600          heartbeats, answering commands with CMD_RESULT
    pnmt_udp.py --selftest                      the vectors of docs/udp-protocol.md §3.14

The counter is persisted with block reservation (§3.7): the file holds a value at least as high as
any counter used, written every 16 packets, so a restart never reuses one.
"""
import argparse
import hashlib
import hmac
import os
import socket
import struct
import sys
import time

VERSION = 1
TYPES = {"heartbeat": 0x0, "data": 0x1, "event": 0x2, "cmd_result": 0x3, "boot": 0x4, "ota": 0x6}
DOWNLINK = {0x8: "ACK", 0x9: "COMMAND", 0xA: "JOIN_ACCEPT", 0xB: "OTA_OFFER", 0xC: "OTA_CHUNK"}
# suite: (code, tag length, hash or None)
SUITES = {
    "none": (0, 0, None),
    "token32": (1, 4, None),
    "hmac32": (2, 4, hashlib.sha256),
    "hmac64": (3, 8, hashlib.sha256),
    "md5-32": (8, 4, hashlib.md5),
    "md5-64": (9, 8, hashlib.md5),
    "sha1-32": (10, 4, hashlib.sha1),
    "sha1-64": (11, 8, hashlib.sha1),
}
BY_CODE = {code: (name, tag, alg) for name, (code, tag, alg) in SUITES.items()}
RESERVE = 16


def id_length(device_id):
    return 1 if device_id <= 0xFF else 2 if device_id <= 0xFFFF else 3 if device_id <= 0xFFFFFF else 4


def header(ptype, suite_code, device_id, counter, ack=False, more=False):
    n = id_length(device_id)
    b0 = (VERSION << 6) | (ptype << 2) | (n - 1)
    b1 = (0x80 if ack else 0) | (0x40 if more else 0) | suite_code
    return bytes([b0, b1]) + device_id.to_bytes(n, "big") + struct.pack(">H", counter & 0xFFFF)


def tag(suite, key, token, prefix, signed):
    """§3.6: the token, or the first bytes of HMAC(K, PREFIX(4) ‖ every byte before the tag)."""
    code, length, alg = SUITES[suite]
    if code == 0:
        return b""
    if code == 1:
        return token
    return hmac.new(key, struct.pack(">I", prefix) + signed, alg).digest()[:length]


def build(ptype, suite, device_id, fcnt, key, token, payload=b"", ack=False, more=False):
    h = header(ptype, SUITES[suite][0], device_id, fcnt, ack, more)
    return h + payload + tag(suite, key, token, fcnt, h + payload)


def parse(datagram):
    """The header of a datagram, or None when its structure is invalid."""
    if len(datagram) < 5 or datagram[0] >> 6 != VERSION or datagram[1] & 0x30:
        return None
    ptype, n, code = (datagram[0] >> 2) & 0xF, (datagram[0] & 3) + 1, datagram[1] & 0xF
    if code not in BY_CODE:
        return None
    name, tag_length, _ = BY_CODE[code]
    head = 2 + n + 2
    if len(datagram) < head + tag_length:
        return None
    return {
        "type": ptype,
        "ack": bool(datagram[1] & 0x80),
        "more": bool(datagram[1] & 0x40),
        "suite": name,
        "device_id": int.from_bytes(datagram[2:2 + n], "big"),
        "counter": struct.unpack(">H", datagram[2 + n:head])[0],
        "payload": datagram[head:len(datagram) - tag_length],
        "tag": datagram[len(datagram) - tag_length:],
        "signed": datagram[:len(datagram) - tag_length],
    }


def open_downlink(datagram, uplink_fcnt, uplink_suite, device_id, key, token, accepted=()):
    """§3.15: a downlink type, our suite (or one configured), our counter echoed, a valid tag. Else None."""
    p = parse(datagram)
    if not p or not p["type"] & 0x8 or p["device_id"] != device_id:
        return None
    if p["suite"] != uplink_suite and p["suite"] not in accepted:
        return None
    if p["counter"] != uplink_fcnt & 0xFFFF:
        return None
    expected = tag(p["suite"], key, token, uplink_fcnt, p["signed"])
    if not hmac.compare_digest(expected, p["tag"]):
        return None
    p["name"] = DOWNLINK.get(p["type"], hex(p["type"]))
    return p


class Counter:
    """FCNT with block reservation (§3.7)."""

    def __init__(self, path):
        self.path = path
        stored = int(open(path).read().strip()) if path and os.path.exists(path) else 0
        self.next = max(1, stored)
        self.reserved = self.next
        self._reserve()

    def _reserve(self):
        self.reserved = self.next + RESERVE
        if self.path:
            with open(self.path + ".tmp", "w") as f:
                f.write(str(self.reserved))
            os.replace(self.path + ".tmp", self.path)

    def take(self):
        value = self.next
        self.next += 1
        if self.next >= self.reserved:
            self._reserve()
        return value


def exchange(sock, server, datagram, wait, retries):
    """Sends, waits for one answer, sends the identical datagram again when none comes (§3.9)."""
    sock.settimeout(wait)
    for _ in range(retries + 1):
        sock.sendto(datagram, server)
        try:
            return sock.recvfrom(1024)[0]
        except socket.timeout:
            continue
    return None


def hexdump(data):
    return " ".join(f"{b:02X}" for b in data)


def selftest():
    key, token = bytes(range(16)), bytes.fromhex("5A17C39E")
    vectors = [
        (0x0, "hmac32", 7, 42, True, b"", "40 82 07 00 2A DF 27 88 7E"),
        (0x8, "hmac32", 7, 42, False, b"", "60 02 07 00 2A 55 D9 7A 31"),
        (0x1, "hmac64", 7, 43, False, b"21.5C", "44 03 07 00 2B 32 31 2E 35 43 35 0A B3 32 32 76 BB 45"),
        (0x9, "hmac32", 7, 44, False, bytes([1, 2, 2, 0x58]), "64 02 07 00 2C 01 02 02 58 E6 B0 5E F4"),
        (0x3, "hmac32", 7, 45, True, b"\x01\x00OK", "4C 82 07 00 2D 01 00 4F 4B FA A0 DD 20"),
        (0x0, "token32", 300, 1, False, b"", "41 01 01 2C 00 01 5A 17 C3 9E"),
        (0x0, "none", 7, 1, False, b"", "40 00 07 00 01"),
        (0x0, "md5-32", 7, 42, True, b"", "40 88 07 00 2A CD 5C FD B1"),
        (0x0, "md5-64", 7, 42, True, b"", "40 89 07 00 2A 6E 1E 4C 4C A3 C2 F2 A7"),
        (0x0, "sha1-32", 7, 42, True, b"", "40 8A 07 00 2A 68 AB C6 CF"),
        (0x0, "sha1-64", 7, 42, True, b"", "40 8B 07 00 2A B8 25 70 92 47 E1 82 E1"),
    ]
    failures = 0
    for ptype, suite, device_id, fcnt, ack, payload, expected in vectors:
        got = hexdump(build(ptype, suite, device_id, fcnt, key, token, payload, ack))
        ok = got == expected
        failures += not ok
        print(("ok  " if ok else "FAIL") + f"  {suite:8} {got}")
    ack = bytes.fromhex(vectors[1][6].replace(" ", ""))
    assert open_downlink(ack, 42, "hmac32", 7, key, token), "the ACK of vector 2 must verify"
    assert not open_downlink(ack, 43, "hmac32", 7, key, token), "an ACK for another counter must not"
    return 1 if failures else 0


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--selftest", action="store_true")
    parser.add_argument("--server", help="host:port, e.g. pnmt.example.com:5028")
    parser.add_argument("--id", type=int)
    parser.add_argument("--key", default="00" * 16, help="the node's key, 32 hex digits")
    parser.add_argument("--token", default="00000000", help="the node's token, 8 hex digits (TOKEN32)")
    parser.add_argument("--suite", default="hmac32", choices=sorted(SUITES))
    parser.add_argument("--counter-file", default=None, help="where the counter lives (default: none, starts at 1)")
    parser.add_argument("--wait", type=float, default=2.0, help="seconds to wait for an answer")
    parser.add_argument("command", nargs="?", choices=["heartbeat", "data", "event", "boot", "loop"])
    parser.add_argument("--ack", action="store_true")
    parser.add_argument("--text", default="")
    parser.add_argument("--hex", default=None)
    parser.add_argument("--code", type=int, default=200, help="EVENT: the PNMT code")
    parser.add_argument("--channel", type=int, default=0, help="EVENT: the channel")
    parser.add_argument("--interval", type=float, default=600, help="loop: seconds between heartbeats")
    args = parser.parse_args()
    if args.selftest:
        return selftest()
    if not (args.server and args.id and args.command):
        parser.error("--server, --id and a command are needed")
    host, port = args.server.rsplit(":", 1)
    server = (host.strip("[]"), int(port))
    key, token = bytes.fromhex(args.key), bytes.fromhex(args.token)
    counter = Counter(args.counter_file)
    sock = socket.socket(socket.AF_INET6 if ":" in server[0] else socket.AF_INET, socket.SOCK_DGRAM)
    payload = bytes.fromhex(args.hex) if args.hex else args.text.encode()

    def send(ptype, data=b"", ack=False):
        fcnt = counter.take()
        datagram = build(ptype, args.suite, args.id, fcnt, key, token, data, ack)
        answer = exchange(sock, server, datagram, args.wait, 2 if ack else 0) if ack else exchange(sock, server, datagram, 0.5, 0)
        down = answer and open_downlink(answer, fcnt, args.suite, args.id, key, token)
        print(f"-> {hexdump(datagram)}")
        print(f"<- {down['name']} {hexdump(answer)}" if down else ("<- (no valid answer)" if answer else "<- (no answer)"))
        return down

    def handle(down):
        while down and down["name"] == "COMMAND":
            cmd_id, command = down["payload"][0], down["payload"][1:]
            print(f"   COMMAND {cmd_id}: {command!r}")
            # The firmware decides what a command means; this client only echoes it as the result.
            down = send(TYPES["cmd_result"], bytes([cmd_id, 0]) + command, ack=not down["more"])

    if args.command == "event":
        payload = bytes([args.channel]) + struct.pack(">H", args.code) + payload
    if args.command == "boot":
        payload = bytes([1]) + payload
    if args.command != "loop":
        handle(send(TYPES[args.command], payload, args.ack))
        return 0
    handle(send(TYPES["boot"], b"\x01" + b"pnmt_udp.py", ack=True))
    while True:
        time.sleep(args.interval)
        handle(send(TYPES["heartbeat"], ack=True))


if __name__ == "__main__":
    sys.exit(main())
