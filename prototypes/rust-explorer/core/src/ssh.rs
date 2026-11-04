//! SSH/SFTP/PTY over libssh2 (ssh2 crate, WinCNG backend on Windows: no OpenSSL to ship).
//! The host key is verified BEFORE any authentication data is sent.

use crate::known_hosts::{HostKeyStatus, KnownHosts};
use crate::sorting::Entry;
use std::io::{ErrorKind, Read, Write};
use std::net::{TcpStream, ToSocketAddrs};
use std::path::Path;
use std::sync::mpsc::{Receiver, TryRecvError};
use std::time::Duration;

pub use ssh2::Session;

#[derive(Clone, Debug)]
pub struct HostKeyInfo { pub algorithm: String, pub fingerprint: String }

#[derive(Clone, Debug)]
pub enum Auth { Password(String), KeyFile { path: std::path::PathBuf, passphrase: Option<String> } }

#[derive(Debug)]
pub enum SshError { Io(std::io::Error), Ssh(ssh2::Error), HostKeyRejected(String), HostKeyChanged { expected: String, actual: String }, Auth(String) }

impl std::fmt::Display for SshError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            SshError::Io(e) => write!(f, "сеть: {e}"),
            SshError::Ssh(e) => write!(f, "SSH: {e}"),
            SshError::HostKeyRejected(fp) => write!(f, "ключ сервера не принят пользователем ({fp})"),
            SshError::HostKeyChanged { expected, actual } => write!(f, "КЛЮЧ СЕРВЕРА ИЗМЕНИЛСЯ: ожидался {expected}, получен {actual}. Подключение заблокировано."),
            SshError::Auth(m) => write!(f, "аутентификация: {m}"),
        }
    }
}
impl std::error::Error for SshError {}
impl From<std::io::Error> for SshError { fn from(e: std::io::Error) -> Self { SshError::Io(e) } }
impl From<ssh2::Error> for SshError { fn from(e: ssh2::Error) -> Self { SshError::Ssh(e) } }

fn key_name(kind: ssh2::HostKeyType) -> &'static str {
    match kind {
        ssh2::HostKeyType::Rsa => "ssh-rsa",
        ssh2::HostKeyType::Dss => "ssh-dss",
        ssh2::HostKeyType::Ecdsa256 => "ecdsa-sha2-nistp256",
        ssh2::HostKeyType::Ecdsa384 => "ecdsa-sha2-nistp384",
        ssh2::HostKeyType::Ecdsa521 => "ecdsa-sha2-nistp521",
        ssh2::HostKeyType::Ed255219 => "ssh-ed25519", // (sic) variant name in ssh2 0.9.4
        _ => "unknown",
    }
}

/// TCP + handshake + host-key decision + authentication. `decide` is asked only for an Unknown key.
pub fn connect(host: &str, port: u16, user: &str, auth: &Auth, known: &mut KnownHosts, timeout: Duration,
               decide: &mut dyn FnMut(&HostKeyInfo) -> bool) -> Result<(ssh2::Session, HostKeyInfo), SshError> {
    let addr = (host, port).to_socket_addrs()?.next().ok_or_else(|| std::io::Error::new(ErrorKind::NotFound, "адрес не найден"))?;
    let tcp = TcpStream::connect_timeout(&addr, timeout)?;
    tcp.set_nodelay(true)?;
    let mut session = ssh2::Session::new()?;
    session.set_tcp_stream(tcp);
    session.set_timeout(timeout.as_millis().min(u32::MAX as u128) as u32);
    session.handshake()?;
    let (_, kind) = session.host_key().ok_or_else(|| SshError::Auth("сервер не предъявил ключ".into()))?;
    let hash = session.host_key_hash(ssh2::HashType::Sha256).ok_or_else(|| SshError::Auth("нет SHA-256 отпечатка ключа".into()))?;
    let info = HostKeyInfo { algorithm: key_name(kind).to_string(), fingerprint: format!("SHA256:{}", crate::base64(hash).trim_end_matches('=')) };
    match known.check(host, port, &info.algorithm, &info.fingerprint) {
        HostKeyStatus::Trusted => {}
        HostKeyStatus::Changed { expected } => return Err(SshError::HostKeyChanged { expected, actual: info.fingerprint }),
        HostKeyStatus::Unknown => {
            if !decide(&info) { return Err(SshError::HostKeyRejected(info.fingerprint)); }
            known.trust(host, port, &info.algorithm, &info.fingerprint)?;
        }
    }
    match auth {
        Auth::Password(p) => session.userauth_password(user, p)?,
        Auth::KeyFile { path, passphrase } => session.userauth_pubkey_file(user, None, path, passphrase.as_deref())?,
    }
    if !session.authenticated() { return Err(SshError::Auth("сервер отклонил учётные данные".into())); }
    Ok((session, info))
}

pub fn home(session: &ssh2::Session) -> Result<String, SshError> {
    let sftp = session.sftp()?;
    Ok(sftp.realpath(Path::new("."))?.to_string_lossy().replace('\\', "/"))
}

/// Server-side canonical path (resolves ".", "..", symlinks and relative paths).
pub fn realpath(sftp: &ssh2::Sftp, path: &str) -> Result<String, SshError> {
    Ok(sftp.realpath(Path::new(path))?.to_string_lossy().replace('\\', "/"))
}

/// Lists a directory (names as data; "." and ".." removed).
pub fn list(sftp: &ssh2::Sftp, path: &str) -> Result<Vec<Entry>, SshError> {
    let items = sftp.readdir(Path::new(path))?;
    Ok(items.into_iter().filter_map(|(p, stat)| {
        let name = p.file_name()?.to_string_lossy().to_string();
        if name == "." || name == ".." { return None; }
        Some(Entry { name, is_dir: stat.is_dir(), size: if stat.is_dir() { None } else { stat.size }, modified: stat.mtime, permissions: stat.perm.map(|p| p & 0o7777) })
    }).collect())
}

pub fn upload(sftp: &ssh2::Sftp, local: &Path, remote_dir: &str) -> Result<String, SshError> {
    let name = local.file_name().ok_or_else(|| std::io::Error::new(ErrorKind::InvalidInput, "нет имени файла"))?.to_string_lossy().to_string();
    let target = if remote_dir.ends_with('/') { format!("{remote_dir}{name}") } else { format!("{remote_dir}/{name}") };
    let mut source = std::fs::File::open(local)?;
    // Never overwrite: create exclusively.
    let mut file = sftp.open_mode(Path::new(&target), ssh2::OpenFlags::WRITE | ssh2::OpenFlags::CREATE | ssh2::OpenFlags::EXCLUSIVE, 0o644, ssh2::OpenType::File)?;
    std::io::copy(&mut source, &mut file)?;
    Ok(target)
}

pub enum PtyCommand { Input(Vec<u8>), Resize(u32, u32), Close }

/// Runs an interactive PTY shell on its own session until EOF, Close or a dropped command sender.
/// `on_output` receives each chunk as it arrives and returns false to stop (receiver gone).
pub fn run_pty(session: ssh2::Session, cols: u32, rows: u32, commands: Receiver<PtyCommand>,
               on_output: &mut dyn FnMut(&[u8]) -> bool) -> Result<(), SshError> {
    let mut channel = session.channel_session()?;
    channel.request_pty("xterm-256color", None, Some((cols, rows, 0, 0)))?;
    channel.shell()?;
    session.set_blocking(false);
    let mut buffer = vec![0u8; 32 * 1024];
    loop {
        loop {
            match commands.try_recv() {
                Ok(PtyCommand::Input(bytes)) => { session.set_blocking(true); channel.write_all(&bytes)?; channel.flush()?; session.set_blocking(false); }
                Ok(PtyCommand::Resize(c, r)) => { session.set_blocking(true); channel.request_pty_size(c, r, None, None)?; session.set_blocking(false); }
                Ok(PtyCommand::Close) | Err(TryRecvError::Disconnected) => { session.set_blocking(true); let _ = channel.close(); return Ok(()); }
                Err(TryRecvError::Empty) => break,
            }
        }
        match channel.read(&mut buffer) {
            Ok(0) => { if channel.eof() { return Ok(()); } std::thread::sleep(Duration::from_millis(4)); }
            Ok(n) => { if !on_output(&buffer[..n]) { session.set_blocking(true); let _ = channel.close(); return Ok(()); } }
            Err(e) if e.kind() == ErrorKind::WouldBlock => std::thread::sleep(Duration::from_millis(4)),
            Err(e) => return Err(SshError::Io(e)),
        }
    }
}

/// Executes a command and returns (stdout, exit status); used by the throughput benchmark.
pub fn exec(session: &ssh2::Session, command: &str) -> Result<(Vec<u8>, i32), SshError> {
    let mut channel = session.channel_session()?;
    channel.exec(command)?;
    let mut out = Vec::new();
    channel.read_to_end(&mut out)?;
    channel.wait_close()?;
    Ok((out, channel.exit_status()?))
}
