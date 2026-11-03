"""Development-only fixture provisioning. Never shipped or invoked by SSH Explorer.

Creates independent OpenSSH instances and a small tmpfs in the disposable QEMU guest.
Does not reload, restart or reconfigure the primary sshd or sshx-test account.
Usage: python provision-modes.py <workspace/.tools/vm>
       python provision-modes.py <workspace/.tools/vm> --relay
"""
import base64
import hashlib
import json
import secrets
import socket
import sys
import threading
import time
import queue
from pathlib import Path

base = Path(sys.argv[1]).resolve()
sys.path.insert(0, str(base.parent / "python-libs"))
import paramiko

main = json.loads((base / "integration.json").read_text(encoding="utf-8-sig"))

if "--relay" in sys.argv:
    waiting = {22224: queue.Queue(), 22225: queue.Queue()}

    def forward(client, local_port):
        channel = None
        try:
            channel = waiting[local_port].get(timeout=15)
            channel.sendall(b"CONNECT\n")

            def pump(source, destination):
                try:
                    while True:
                        data = source.recv(65536)
                        if not data:
                            break
                        destination.sendall(data)
                except Exception:
                    pass
                finally:
                    try:
                        destination.shutdown(socket.SHUT_WR)
                    except Exception:
                        pass

            thread = threading.Thread(target=pump, args=(client, channel), daemon=True)
            thread.start()
            pump(channel, client)
        except Exception as error:
            print(type(error).__name__ + ': ' + str(error), file=sys.stderr, flush=True)
        finally:
            client.close()
            if channel:
                channel.close()
    def listener(host_port, connector_port):
        with socket.socket() as server:
            server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            server.bind(("127.0.0.1", host_port))
            server.listen(16)
            while True:
                client, _ = server.accept()
                threading.Thread(target=forward, args=(client, host_port), daemon=True).start()

    def connectors(port, local_port):
        with socket.socket() as server:
            server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            server.bind(("127.0.0.1", port))
            server.listen(16)
            while True:
                client, _ = server.accept()
                waiting[local_port].put(client)

    for pair in [(22228, 22224), (22229, 22225)]:
        threading.Thread(target=connectors, args=pair, daemon=True).start()
    for pair in [(22224, 22228), (22225, 22229)]:
        threading.Thread(target=listener, args=pair, daemon=True).start()
    while True:
        time.sleep(60)

sftp_password = secrets.token_urlsafe(24)
shell_password = secrets.token_urlsafe(24)
(base / "sftp-only-password.txt").write_text(sftp_password)
(base / "shell-only-password.txt").write_text(shell_password)
fixture = "/mnt/test/sshx-fixture-modes"
common = """ListenAddress 127.0.0.1
HostKey /etc/ssh/ssh_host_ed25519_key
PasswordAuthentication yes
KbdInteractiveAuthentication no
PubkeyAuthentication no
PermitRootLogin no
LogLevel ERROR
"""
sftp_config = common + f"Port 2201\nPidFile /run/sshx-sftp-sshd.pid\nAllowUsers sshx-sftp\nSubsystem sftp internal-sftp\nForceCommand internal-sftp\nPermitTTY no\n"
shell_config = common + f"Port 2202\nPidFile /run/sshx-shell-sshd.pid\nAllowUsers sshx-shell\n"
bridge_sftp=base64.b64encode(b"#!/bin/sh\nIFS= read -r trigger || exit\nexec nc 127.0.0.1 2201\n").decode()
bridge_shell=base64.b64encode(b"#!/bin/sh\nIFS= read -r trigger || exit\nexec nc 127.0.0.1 2202\n").decode()
commands = f"""set -e
mkdir -p {fixture}
ip link set lo up
id sshx-sftp >/dev/null 2>&1 || adduser -D -h {fixture}/sftp sshx-sftp
id sshx-shell >/dev/null 2>&1 || adduser -D -h {fixture}/shell sshx-shell
mkdir -p {fixture}/sftp {fixture}/shell
chown sshx-sftp:sshx-sftp {fixture}/sftp
chown sshx-shell:sshx-shell {fixture}/shell
printf '%s\\n' 'sshx-sftp:{sftp_password}' 'sshx-shell:{shell_password}' | chpasswd
printf '%s' '{base64.b64encode(sftp_config.encode()).decode()}' | base64 -d > {fixture}/sftp-sshd.conf
printf '%s' '{base64.b64encode(shell_config.encode()).decode()}' | base64 -d > {fixture}/shell-sshd.conf
/usr/sbin/sshd -t -f {fixture}/sftp-sshd.conf
/usr/sbin/sshd -t -f {fixture}/shell-sshd.conf
test -f /run/sshx-sftp-sshd.pid || /usr/sbin/sshd -f {fixture}/sftp-sshd.conf
test -f /run/sshx-shell-sshd.pid || /usr/sbin/sshd -f {fixture}/shell-sshd.conf
mkdir -p /mnt/diskfull-test
grep -q ' /mnt/diskfull-test ' /proc/mounts || mount -t tmpfs -o size=2m,mode=0700 tmpfs /mnt/diskfull-test
chown sshx-test:sshx-test /mnt/diskfull-test
printf '%s' '{bridge_sftp}' | base64 -d > {fixture}/bridge-sftp.sh
printf '%s' '{bridge_shell}' | base64 -d > {fixture}/bridge-shell.sh
chmod 700 {fixture}/bridge-sftp.sh {fixture}/bridge-shell.sh
if [ -f /run/sshx-mode-bridges.pids ]; then
  while read pid; do kill "$pid" 2>/dev/null || true; done < /run/sshx-mode-bridges.pids
fi
: > /run/sshx-mode-bridges.pids
for mode in sftp shell; do
  if [ "$mode" = sftp ]; then hostport=22228; else hostport=22229; fi
  for slot in 1 2 3 4; do
    bridge={fixture}/bridge-$mode.sh
    (
      while :; do
        nc -w 30 10.0.2.2 "$hostport" -e "$bridge"
        sleep 1
      done
    ) >/dev/null 2>&1 &
    echo $! >> /run/sshx-mode-bridges.pids
  done
done
printf '\\nSSHX_MODES_READY\\n'
"""
with socket.create_connection(("127.0.0.1", 22223), timeout=10) as serial:
    serial.settimeout(1)
    serial.sendall(b"\nstty -echo\nprintf '\\nSSHX_SERIAL_READY\\n'\n")
    received = b""
    deadline = time.monotonic() + 10
    while b"SSHX_SERIAL_READY" not in received and time.monotonic() < deadline:
        try:
            received += serial.recv(65536)
        except TimeoutError:
            pass
    if b"SSHX_SERIAL_READY" not in received:
        raise RuntimeError("Root serial fixture is not ready; no changes applied")
    encoded = base64.b64encode(commands.encode()).decode()
    serial.sendall(f"printf '%s' '{encoded}' | base64 -d | sh\n".encode())
    received = b""
    deadline = time.monotonic() + 25
    while b"SSHX_MODES_READY" not in received and time.monotonic() < deadline:
        try:
            received += serial.recv(65536)
        except TimeoutError:
            pass
    if b"SSHX_MODES_READY" not in received:
        raise RuntimeError("Mode fixture provisioning failed; inspect the isolated guest manually")

for kind, port, user, password_file in [("sftp-only", 22224, "sshx-sftp", "sftp-only-password.txt"), ("shell-only", 22225, "sshx-shell", "shell-only-password.txt")]:
    config = {"Host": "127.0.0.1", "Port": port, "Username": user, "Authentication": "Password", "SecretFile": str(base / password_file), "ExpectedHostFingerprint": main["ExpectedHostFingerprint"], "RemoteParent": "~"}
    (base / f"{kind}.json").write_text(json.dumps(config, indent=2), encoding="utf-8")
print("Independent SFTP-only and shell-only SSH fixtures configured; primary sshd unchanged.")
print("2 MiB disposable tmpfs ready at /mnt/diskfull-test for sshx-test.")
