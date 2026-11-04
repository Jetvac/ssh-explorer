//! Live SSH checks against a disposable local test server. Skipped unless SSHX_LIVE_HOST is set.
//!
//! Required: SSHX_LIVE_HOST, SSHX_LIVE_PORT, SSHX_LIVE_USER, SSHX_LIVE_PASSWORD, SSHX_LIVE_DIR (a
//! scratch folder that is the same path for this process and the server, i.e. a server on this
//! machine). Optional: SSHX_LIVE_FINGERPRINT ("algorithm SHA256:…" the server must present),
//! SSHX_LIVE_AUTH_LOG (server-side log; with SSHX_LIVE_AUTH_MARKERS, a comma-separated list such as
//! "Accepted password,Failed password" for an OpenSSH log, only matching lines count as
//! authentication attempts, otherwise every line does) and SSHX_LIVE_REPORT (JSON-lines file).
//! Run with `--test-threads=1`: the auth-log assertions count attempts made by this process.
#![cfg(feature = "ssh")]

use sshx_core::known_hosts::{HostKeyStatus, KnownHosts};
use sshx_core::sorting::{self, Column};
use sshx_core::ssh::{self, Auth, HostKeyInfo, PtyCommand, SshError};
use sshx_core::terminal::Terminal;
use std::io::Write;
use std::path::{Path, PathBuf};
use std::sync::mpsc::channel;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

struct Live { host: String, port: u16, user: String, password: String, dir: PathBuf, fingerprint: Option<String>, auth_log: Option<PathBuf> }

fn live() -> Option<Live> {
    let host = std::env::var("SSHX_LIVE_HOST").ok()?;
    let var = |n: &str| std::env::var(n).unwrap_or_else(|_| panic!("{n} is required when SSHX_LIVE_HOST is set"));
    Some(Live {
        host, port: var("SSHX_LIVE_PORT").parse().unwrap(), user: var("SSHX_LIVE_USER"), password: var("SSHX_LIVE_PASSWORD"),
        dir: PathBuf::from(var("SSHX_LIVE_DIR")), fingerprint: std::env::var("SSHX_LIVE_FINGERPRINT").ok(),
        auth_log: std::env::var_os("SSHX_LIVE_AUTH_LOG").map(PathBuf::from),
    })
}

macro_rules! live_or_skip { () => { match live() { Some(l) => l, None => { eprintln!("SSHX_LIVE_HOST not set: live SSH test skipped"); return; } } } }

const TIMEOUT: Duration = Duration::from_secs(20);

fn scratch(name: &str) -> PathBuf {
    let dir = std::env::temp_dir().join(format!("sshx-live-{}-{name}", std::process::id()));
    let _ = std::fs::remove_dir_all(&dir);
    std::fs::create_dir_all(&dir).unwrap();
    dir
}

fn auth_attempts(l: &Live) -> Option<usize> {
    let markers: Vec<String> = std::env::var("SSHX_LIVE_AUTH_MARKERS").map(|m| m.split(',').map(|s| s.trim().to_string()).filter(|s| !s.is_empty()).collect()).unwrap_or_default();
    l.auth_log.as_ref().map(|p| {
        let text = std::fs::read_to_string(p).unwrap_or_default();
        text.lines().filter(|line| markers.is_empty() || markers.iter().any(|m| line.contains(m.as_str()))).count()
    })
}

fn report(name: &str, value: f64, unit: &str) {
    eprintln!("MEASURE {name} = {value:.2} {unit}");
    if let Some(path) = std::env::var_os("SSHX_LIVE_REPORT") {
        let mut f = std::fs::OpenOptions::new().create(true).append(true).open(path).unwrap();
        writeln!(f, "{{\"name\":\"{name}\",\"value\":{value:.3},\"unit\":\"{unit}\"}}").unwrap();
    }
}

/// Trusts the server key once (first use) and returns an authenticated session.
fn connect_trusted(l: &Live, store: &Path) -> (ssh::Session, HostKeyInfo) {
    let mut known = KnownHosts::load(store).unwrap();
    let mut accept = |_: &HostKeyInfo| true;
    ssh::connect(&l.host, l.port, &l.user, &Auth::Password(l.password.clone()), &mut known, TIMEOUT, &mut accept).expect("connect")
}

#[test]
fn unknown_host_key_rejected_sends_no_credentials() {
    let l = live_or_skip!();
    let store = scratch("reject").join("known_hosts");
    let before = auth_attempts(&l);
    let mut known = KnownHosts::load(&store).unwrap();
    let mut asked = 0;
    let mut decide = |info: &HostKeyInfo| { asked += 1; assert!(info.fingerprint.starts_with("SHA256:")); false };
    let result = ssh::connect(&l.host, l.port, &l.user, &Auth::Password(l.password.clone()), &mut known, TIMEOUT, &mut decide);
    assert!(matches!(result, Err(SshError::HostKeyRejected(_))), "expected rejection, got {:?}", result.err());
    assert_eq!(asked, 1);
    assert!(!store.exists(), "a rejected key must not be stored");
    std::thread::sleep(Duration::from_millis(300));
    assert_eq!(auth_attempts(&l), before, "no authentication may be attempted after a rejected host key");
}

#[test]
fn first_use_is_stored_then_trusted_without_prompt() {
    let l = live_or_skip!();
    let store = scratch("tofu").join("known_hosts");
    let (_, info) = connect_trusted(&l, &store);
    if let Some(expected) = &l.fingerprint { assert_eq!(&format!("{} {}", info.algorithm, info.fingerprint), expected.trim()); }
    let known = KnownHosts::load(&store).unwrap();
    assert_eq!(known.check(&l.host, l.port, &info.algorithm, &info.fingerprint), HostKeyStatus::Trusted);
    let mut known = known;
    let mut never = |_: &HostKeyInfo| -> bool { panic!("a trusted key must not prompt") };
    ssh::connect(&l.host, l.port, &l.user, &Auth::Password(l.password.clone()), &mut known, TIMEOUT, &mut never).expect("second connect");
}

#[test]
fn changed_host_key_blocks_before_authentication() {
    let l = live_or_skip!();
    let dir = scratch("changed");
    let store = dir.join("known_hosts");
    let (_, info) = connect_trusted(&l, &scratch("changed-probe").join("known_hosts"));
    let forged = format!("{} {} {} SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\n", l.host.to_lowercase(), l.port, info.algorithm);
    std::fs::write(&store, &forged).unwrap();
    let before = auth_attempts(&l);
    let mut known = KnownHosts::load(&store).unwrap();
    let mut decide = |_: &HostKeyInfo| -> bool { panic!("a changed key must never be offered for trust") };
    let result = ssh::connect(&l.host, l.port, &l.user, &Auth::Password(l.password.clone()), &mut known, TIMEOUT, &mut decide);
    match result {
        Err(SshError::HostKeyChanged { expected, actual }) => { assert!(expected.starts_with("SHA256:AAAA")); assert_eq!(actual, info.fingerprint); }
        other => panic!("expected HostKeyChanged, got {:?}", other.err()),
    }
    assert_eq!(std::fs::read_to_string(&store).unwrap(), forged, "the stored key must not be overwritten");
    std::thread::sleep(Duration::from_millis(300));
    assert_eq!(auth_attempts(&l), before, "no authentication may be attempted for a changed host key");
}

#[test]
fn wrong_password_is_rejected() {
    let l = live_or_skip!();
    let store = scratch("badpw").join("known_hosts");
    connect_trusted(&l, &store);
    let mut known = KnownHosts::load(&store).unwrap();
    let mut never = |_: &HostKeyInfo| false;
    let result = ssh::connect(&l.host, l.port, &l.user, &Auth::Password(format!("{}-wrong", l.password)), &mut known, TIMEOUT, &mut never);
    assert!(matches!(result, Err(SshError::Ssh(_)) | Err(SshError::Auth(_))), "expected an authentication failure, got {:?}", result.err());
}

#[test]
fn sftp_lists_ten_thousand_entries_with_typed_sorting() {
    let l = live_or_skip!();
    let dir = l.dir.join(format!("list-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&dir);
    std::fs::create_dir_all(&dir).unwrap();
    for i in 0..10_000 { std::fs::write(dir.join(format!("file{i}.txt")), if i % 7 == 0 { "x".repeat(i % 100) } else { String::new() }).unwrap(); }
    for name in ["Docs", "archive", "bin2", "bin10"] { std::fs::create_dir(dir.join(name)).unwrap(); }
    let (session, _) = connect_trusted(&l, &scratch("list").join("known_hosts"));
    let sftp = session.sftp().unwrap();
    let start = Instant::now();
    let mut entries = ssh::list(&sftp, dir.to_str().unwrap()).unwrap();
    report("sftp_list_10k_ms", start.elapsed().as_secs_f64() * 1000.0, "ms");
    assert_eq!(entries.len(), 10_004);
    sorting::sort(&mut entries, Column::Name, false);
    let names: Vec<&str> = entries.iter().take(6).map(|e| e.name.as_str()).collect();
    assert_eq!(names, ["archive", "bin2", "bin10", "Docs", "file0.txt", "file1.txt"]);
    let pos = |n: &str| entries.iter().position(|e| e.name == n).unwrap();
    assert!(pos("file2.txt") < pos("file10.txt"));
    sorting::sort(&mut entries, Column::Size, true);
    assert!(entries[..4].iter().all(|e| e.is_dir), "folders first in both directions");
    assert_eq!(entries[4].size, Some(99), "largest file first when descending (399 % 100)");
    let resolved = ssh::realpath(&sftp, &format!("{}/Docs/..", dir.display())).unwrap();
    assert_eq!(resolved, dir.to_str().unwrap());
    let _ = std::fs::remove_dir_all(&dir);
}

#[test]
fn upload_never_overwrites_an_existing_file() {
    let l = live_or_skip!();
    let target = l.dir.join(format!("upload-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&target);
    std::fs::create_dir_all(&target).unwrap();
    let local = scratch("upload").join("payload.txt");
    std::fs::write(&local, "first version").unwrap();
    let (session, _) = connect_trusted(&l, &scratch("upload-kh").join("known_hosts"));
    let sftp = session.sftp().unwrap();
    let remote = ssh::upload(&sftp, &local, target.to_str().unwrap()).expect("first upload");
    assert_eq!(remote, format!("{}/payload.txt", target.display()));
    std::fs::write(&local, "second version").unwrap();
    assert!(ssh::upload(&sftp, &local, target.to_str().unwrap()).is_err(), "an existing file must not be replaced");
    assert_eq!(std::fs::read_to_string(target.join("payload.txt")).unwrap(), "first version");
    let _ = std::fs::remove_dir_all(&target);
}

#[test]
fn pty_roundtrip_resize_and_close() {
    let l = live_or_skip!();
    let (session, _) = connect_trusted(&l, &scratch("pty").join("known_hosts"));
    let output = Arc::new(Mutex::new(Vec::<u8>::new()));
    let (tx, rx) = channel::<PtyCommand>();
    let sink = output.clone();
    let worker = std::thread::spawn(move || ssh::run_pty(session, 80, 24, rx, &mut |bytes: &[u8]| { sink.lock().unwrap().extend_from_slice(bytes); true }));
    let wait_for = |needle: &str| {
        let deadline = Instant::now() + Duration::from_secs(10);
        while Instant::now() < deadline {
            if String::from_utf8_lossy(&output.lock().unwrap()).contains(needle) { return true; }
            std::thread::sleep(Duration::from_millis(20));
        }
        false
    };
    let start = Instant::now();
    tx.send(PtyCommand::Input(b"printf 'X%sY\\n' $((6*7))\n".to_vec())).unwrap();
    assert!(wait_for("X42Y"), "command output not seen: {:?}", String::from_utf8_lossy(&output.lock().unwrap()));
    report("pty_command_roundtrip_ms", start.elapsed().as_secs_f64() * 1000.0, "ms");
    tx.send(PtyCommand::Resize(100, 40)).unwrap();
    tx.send(PtyCommand::Input(b"stty size\n".to_vec())).unwrap();
    assert!(wait_for("40 100"), "resize not applied: {:?}", String::from_utf8_lossy(&output.lock().unwrap()));
    let mut terminal = Terminal::new(100, 40);
    terminal.feed(&output.lock().unwrap());
    assert!(terminal.screen_text().contains("X42Y"));
    tx.send(PtyCommand::Close).unwrap();
    let joined = Instant::now();
    let result = worker.join().unwrap();
    assert!(result.is_ok(), "pty ended with {:?}", result.err());
    assert!(joined.elapsed() < Duration::from_secs(5));
}

#[test]
fn exec_stream_throughput() {
    let l = live_or_skip!();
    let (session, _) = connect_trusted(&l, &scratch("exec").join("known_hosts"));
    let start = Instant::now();
    let (out, status) = ssh::exec(&session, "head -c 16777216 /dev/zero").unwrap();
    let seconds = start.elapsed().as_secs_f64();
    assert_eq!(status, 0);
    assert_eq!(out.len(), 16 << 20);
    report("exec_16MiB_MBps", out.len() as f64 / seconds / 1e6, "MB/s");
}
