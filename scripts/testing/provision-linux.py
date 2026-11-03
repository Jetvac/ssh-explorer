"""Provision a disposable QEMU test guest only; never called by SSH Explorer."""
import argparse, base64, hashlib, json, secrets, socket, sys, time
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument('--name', required=True)
parser.add_argument('--serial-port', type=int, required=True)
parser.add_argument('--ssh-port', type=int, required=True)
parser.add_argument('--directory', required=True)
parser.add_argument('--resume', action='store_true')
args = parser.parse_args()
root = Path(__file__).resolve().parents[2]
base = Path(args.directory).resolve(); base.mkdir(parents=True, exist_ok=True)
sys.path.insert(0, str(root / '.tools/python-libs'))
import paramiko
from cryptography.hazmat.primitives.asymmetric import ed25519
from cryptography.hazmat.primitives import serialization

if not args.resume:
    s = socket.create_connection(('127.0.0.1', args.serial_port), timeout=15)
    s.settimeout(1)
    def until(needle, seconds=90):
        data=b''; deadline=time.monotonic()+seconds
        while needle not in data and time.monotonic()<deadline:
            try:
                block=s.recv(65536)
                if not block: raise RuntimeError('Guest console closed')
                data+=block
            except TimeoutError: pass
        if needle not in data: raise RuntimeError('Guest setup timed out: '+data[-1500:].decode(errors='replace'))
        return data
    s.sendall(b'\n'); until(b'login:')
    s.sendall(b'root\n'); until(b'# ')
    s.sendall(b'stty -echo\n'); until(b'# ')
    password=secrets.token_urlsafe(24); passphrase=secrets.token_urlsafe(24)
    private=ed25519.Ed25519PrivateKey.generate()
    public=private.public_key().public_bytes(serialization.Encoding.OpenSSH,serialization.PublicFormat.OpenSSH).decode()
    (base/'client-key').write_bytes(private.private_bytes(serialization.Encoding.PEM,serialization.PrivateFormat.OpenSSH,serialization.BestAvailableEncryption(passphrase.encode())))
    (base/'password.txt').write_text(password,encoding='utf-8')
    (base/'key-passphrase.txt').write_text(passphrase,encoding='utf-8')
    commands=f'''ip link set eth0 up
    udhcpc -i eth0 -q -n
    printf '%s\\n' 'https://dl-cdn.alpinelinux.org/alpine/v3.24/main' > /etc/apk/repositories
    apk add openssh-server openssh-sftp-server e2fsprogs musl-utils
    hostname {args.name}
    modprobe ext4
    mkfs.ext4 -F /dev/vda >/tmp/test-mkfs.log 2>&1
    mkdir -p /mnt/test
    mount -t ext4 /dev/vda /mnt/test
    adduser -D -h /mnt/test/operator operator
    printf '%s\\n' 'operator:{password}' | chpasswd
    mkdir -p /mnt/test/operator/.ssh
    printf '%s\\n' '{public}' > /mnt/test/operator/.ssh/authorized_keys
    chown -R operator:operator /mnt/test/operator
    chmod 700 /mnt/test/operator/.ssh
    chmod 600 /mnt/test/operator/.ssh/authorized_keys
    ssh-keygen -A
    mkdir -p /run/sshd
    /usr/sbin/sshd
    printf '\\nSSHX_READY_V02\\n'
    '''
    s.sendall(commands.encode()); log=until(b'SSHX_READY_V02',180)
    (base/'provision.log').write_bytes(log); s.close()
else:
    password=(base/'password.txt').read_text(encoding='utf-8')

for attempt in range(4):
    try:
        t=paramiko.Transport(socket.create_connection(('127.0.0.1',args.ssh_port),timeout=15))
        t.banner_timeout=15; t.auth_timeout=15; t.start_client(timeout=20)
        break
    except (OSError,paramiko.SSHException):
        t.close()
        if attempt == 3: raise
        time.sleep(2)
fingerprint='SHA256:'+base64.b64encode(hashlib.sha256(t.get_remote_server_key().asbytes()).digest()).decode().rstrip('=')
t.auth_password('operator',password)
sftp=paramiko.SFTPClient.from_transport(t)
home=sftp.normalize('.')
for name in ['Documents','Images','Upload','Target','Empty']:
    try: sftp.mkdir(home+'/'+name)
    except OSError:
        if not sftp.stat(home+'/'+name).st_mode: raise
for name,content in {'README.txt':'Isolated SSH Explorer 0.2 test host '+args.name+'\n','Documents/РџСЂРёРІРµС‚.txt':'РџСЂРёРІРµС‚, Windows!\n','Documents/notes.txt':'UTF-8 SSH/SFTP native OLE fixture\n'}.items():
    with sftp.open(home+'/'+name,'wb') as f: f.write(content.encode())
sftp.close(); t.close()
config={'Host':'127.0.0.1','Port':args.ssh_port,'Username':'operator','Authentication':'Password','SecretFile':str(base/'password.txt'),'ExpectedHostFingerprint':fingerprint,'RemoteParent':'~','RunMonitoring':True,'LargeFileBytes':0}
(base/'integration.json').write_text(json.dumps(config,indent=2),encoding='utf-8')
ui={'Host':config['Host'],'Port':args.ssh_port,'Username':'operator','Secret':password,'Fingerprint':fingerprint,'InitialPath':'~','Name':args.name}
(base/'ui-node.json').write_text(json.dumps(ui,indent=2),encoding='utf-8')
print(json.dumps({'Name':args.name,'Port':args.ssh_port,'Fingerprint':fingerprint,'Directory':str(base)},ensure_ascii=True))
