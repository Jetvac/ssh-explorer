"""Stop only development restricted-mode helpers in the disposable QEMU guest.

Leaves the primary sshd, sshx-test account, VM and test tmpfs untouched.
"""
import socket
import time

command = r'''
terminate_tree() {
  pid="$1"
  test -r "/proc/$pid/task/$pid/children" || return 0
  children=$(cat "/proc/$pid/task/$pid/children")
  for child in $children; do terminate_tree "$child"; done
  kill "$pid" 2>/dev/null || true
}
if test -f /run/sshx-mode-bridges.pids; then
  while read pid; do terminate_tree "$pid"; done < /run/sshx-mode-bridges.pids
  rm -f /run/sshx-mode-bridges.pids
fi
for file in /run/sshx-sftp-sshd.pid /run/sshx-shell-sshd.pid; do
  if test -f "$file"; then
    pid=$(cat "$file")
    command=$(tr '\000' ' ' < "/proc/$pid/cmdline" 2>/dev/null || true)
    case "$command" in *sshx-fixture-modes*) terminate_tree "$pid";; esac
    rm -f "$file"
  fi
done
printf '\nSSHX_MODE_HELPERS_STOPPED\n'
'''
with socket.create_connection(("127.0.0.1", 22223), timeout=5) as serial:
    serial.settimeout(1)
    serial.sendall(command.encode() + b"\n")
    received=b""
    deadline=time.monotonic()+8
    while b"SSHX_MODE_HELPERS_STOPPED" not in received and time.monotonic()<deadline:
        try:
            received+=serial.recv(65536)
        except TimeoutError:
            pass
    if b"SSHX_MODE_HELPERS_STOPPED" not in received:
        raise RuntimeError("Could not confirm restricted-mode fixture cleanup")
print("Restricted-mode guest helpers stopped; primary SSH and VM preserved.")
