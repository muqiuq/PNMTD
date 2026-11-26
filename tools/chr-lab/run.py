"""One CHR in QEMU (tools/chr-lab.sh, docs/mikrotik-plan.md §12).

Downloads the image of CHR_VERSION once (into /images, a volume), boots a fresh copy with its serial
console on /run/chr/serial.sock, signs in as admin on that console as a person would (licence prompt,
new password), sets the identity, and writes /run/chr/ready. QEMU's user-mode network forwards the
container's ports 2222 (SSH) and 8291 (WinBox) to the router.
"""
import hashlib
import os
import platform
import shutil
import socket
import subprocess
import sys
import time
import urllib.request
import zipfile

import pexpect
import pexpect.fdpexpect

VERSION = os.environ.get("CHR_VERSION", "7.25.1")
NAME = os.environ.get("CHR_NAME", "chr")
PASSWORD = os.environ.get("CHR_PASSWORD", "pnmt-lab")
ARCH = os.environ.get("CHR_ARCH", "auto")
MEMORY = os.environ.get("CHR_MEMORY", "1024")
SOCK = "/run/chr/serial.sock"
READY = "/run/chr/ready"


def log(message):
    print(f"[chr-lab {NAME}] {message}", flush=True)


def arch():
    if ARCH != "auto":
        return ARCH
    host = platform.machine()
    if host in ("aarch64", "arm64") and os.path.exists("/dev/kvm"):
        # ARM64 images exist from 7.20 on (7.13 has none): fall back to x86_64 under emulation.
        url = f"https://download.mikrotik.com/routeros/{VERSION}/chr-{VERSION}-arm64.img.zip"
        try:
            urllib.request.urlopen(urllib.request.Request(url, method="HEAD"), timeout=15)
            return "arm64"
        except Exception:
            return "x86_64"
    return "x86_64"


def image(a):
    suffix = "-arm64" if a == "arm64" else ""
    path = f"/images/chr-{VERSION}{suffix}.img"
    if not os.path.exists(path):
        url = f"https://download.mikrotik.com/routeros/{VERSION}/chr-{VERSION}{suffix}.img.zip"
        log(f"downloading {url}")
        zpath = path + ".zip"
        urllib.request.urlretrieve(url, zpath)
        with zipfile.ZipFile(zpath) as z:
            inner = [n for n in z.namelist() if n.endswith(".img")][0]
            with z.open(inner) as src, open(path + ".part", "wb") as dst:
                shutil.copyfileobj(src, dst)
        os.rename(path + ".part", path)
        os.remove(zpath)
    return path


LINK = os.environ.get("CHR_LINK", "")


def nic_mac(n):
    """In a pair, MACs of the router's own (from its name): QEMU gives every VM the same ones, and two on one cable clash."""
    h = hashlib.sha256(NAME.encode()).digest()
    return f",mac=52:54:{h[0]:02x}:{h[1]:02x}:{h[2]:02x}:{n:02x}" if LINK else ""


def link_nic():
    """CHR_LINK=<peer address>:<port>: a second NIC (ether2) as a UDP socket to the peer's, a cable between two lab routers."""
    if not LINK:
        return []
    peer, port = LINK.rsplit(":", 1)
    return ["-netdev", f"socket,id=n1,udp={peer}:{port},localaddr=0.0.0.0:{port}", "-device", f"virtio-net-pci,netdev=n1{nic_mac(2)}"]


def qemu_command(a, disk):
    net = "user,id=n0,hostfwd=tcp::2222-:22,hostfwd=tcp::8291-:8291,hostfwd=tcp::8080-:80"
    serial = f"unix:{SOCK},server=on,wait=off"
    if a == "arm64":
        return ["qemu-system-aarch64", "-M", "virt", "-cpu", "host", "-accel", "kvm", "-m", MEMORY,
                "-bios", "/usr/share/qemu-efi-aarch64/QEMU_EFI.fd",
                "-drive", f"file={disk},if=none,format=raw,id=hd", "-device", "virtio-blk-pci,drive=hd",
                "-netdev", net, "-device", f"virtio-net-pci,netdev=n0{nic_mac(1)}", *link_nic(),
                "-serial", serial, "-display", "none", "-monitor", "none"]
    accel = ["-accel", "kvm", "-cpu", "host"] if platform.machine() == "x86_64" and os.path.exists("/dev/kvm") \
        else ["-accel", "tcg"]
    return ["qemu-system-x86_64", *accel, "-m", MEMORY,
            "-drive", f"file={disk},format=raw,if=virtio",
            "-netdev", net, "-device", f"virtio-net-pci,netdev=n0{nic_mac(1)}", *link_nic(),
            "-serial", serial, "-display", "none", "-monitor", "none"]


def console():
    for _ in range(100):
        try:
            s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
            s.connect(SOCK)
            return s
        except OSError:
            time.sleep(0.2)
    raise RuntimeError("the serial console did not appear")


def sign_in(s):
    c = pexpect.fdpexpect.fdspawn(s.fileno(), timeout=600, encoding="utf-8", codec_errors="replace")
    c.logfile_read = sys.stdout
    # Wake the console until the login prompt shows (a slow, emulated boot takes minutes).
    deadline = time.time() + 900
    while True:
        c.send("\r")
        i = c.expect(["Login: ", pexpect.TIMEOUT], timeout=10)
        if i == 0:
            break
        if time.time() > deadline:
            raise RuntimeError("no login prompt")
    c.send("admin\r")
    c.expect("Password: ")
    c.send("\r")
    while True:
        i = c.expect([r"software license\? \[Y/n\]", r"new password>", r"repeat new password>",
                      r"\] > "], timeout=120)
        if i == 0:
            c.send("n")
        elif i in (1, 2):
            c.send(PASSWORD + "\r")
        else:
            break
    c.send(f"/system identity set name={NAME}\r")
    c.expect(r"\] > ")
    c.send("/quit\r")
    time.sleep(1)


def main():
    os.makedirs("/run/chr", exist_ok=True)
    for p in (SOCK, READY):
        if os.path.exists(p):
            os.remove(p)
    a = arch()
    base = image(a)
    disk = "/work/disk.img"
    os.makedirs("/work", exist_ok=True)
    shutil.copyfile(base, disk)
    log(f"RouterOS {VERSION} ({a}), booting")
    qemu = subprocess.Popen(qemu_command(a, disk))
    s = console()
    sign_in(s)
    s.close()
    with open(READY, "w") as f:
        f.write(f"{VERSION} {a}\n")
    log(f"ready: admin / {PASSWORD}, SSH on the container's port 2222")
    sys.exit(qemu.wait())


if __name__ == "__main__":
    main()
