//! Host-key trust store (prototype format: one "host port algorithm sha256" line per key).
//! A changed key for a known endpoint is reported as Changed and must block the connection.

use std::path::{Path, PathBuf};

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct KnownKey { pub host: String, pub port: u16, pub algorithm: String, pub fingerprint: String }

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum HostKeyStatus { Trusted, Unknown, Changed { expected: String } }

pub struct KnownHosts { path: PathBuf, keys: Vec<KnownKey> }

impl KnownHosts {
    pub fn load(path: &Path) -> std::io::Result<Self> {
        let mut keys = Vec::new();
        match std::fs::read_to_string(path) {
            Ok(text) => {
                for line in text.lines() {
                    let parts: Vec<&str> = line.split_whitespace().collect();
                    if parts.len() == 4 {
                        if let Ok(port) = parts[1].parse() {
                            keys.push(KnownKey { host: parts[0].to_lowercase(), port, algorithm: parts[2].into(), fingerprint: parts[3].into() });
                        }
                    }
                }
            }
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => {}
            Err(e) => return Err(e),
        }
        Ok(KnownHosts { path: path.to_path_buf(), keys })
    }

    pub fn check(&self, host: &str, port: u16, algorithm: &str, fingerprint: &str) -> HostKeyStatus {
        let host = host.to_lowercase();
        let mut changed = None;
        for k in self.keys.iter().filter(|k| k.host == host && k.port == port) {
            if k.algorithm == algorithm && k.fingerprint == fingerprint { return HostKeyStatus::Trusted; }
            changed = Some(k.fingerprint.clone());
        }
        match changed { Some(expected) => HostKeyStatus::Changed { expected }, None => HostKeyStatus::Unknown }
    }

    /// Persist a first-use decision. Never used to overwrite a Changed key.
    pub fn trust(&mut self, host: &str, port: u16, algorithm: &str, fingerprint: &str) -> std::io::Result<()> {
        if matches!(self.check(host, port, algorithm, fingerprint), HostKeyStatus::Changed { .. }) {
            return Err(std::io::Error::new(std::io::ErrorKind::PermissionDenied, "host key changed; refusing to overwrite trust"));
        }
        self.keys.push(KnownKey { host: host.to_lowercase(), port, algorithm: algorithm.into(), fingerprint: fingerprint.into() });
        if let Some(dir) = self.path.parent() { std::fs::create_dir_all(dir)?; }
        let text: String = self.keys.iter().map(|k| format!("{} {} {} {}\n", k.host, k.port, k.algorithm, k.fingerprint)).collect();
        let tmp = self.path.with_extension("tmp");
        std::fs::write(&tmp, text)?;
        std::fs::rename(&tmp, &self.path)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn unknown_then_trusted_then_changed_blocks() {
        let dir = std::env::temp_dir().join(format!("sshx-known-{}", std::process::id()));
        let path = dir.join("known_hosts");
        let mut store = KnownHosts::load(&path).unwrap();
        assert_eq!(store.check("Srv", 22, "ssh-ed25519", "AAA"), HostKeyStatus::Unknown);
        store.trust("Srv", 22, "ssh-ed25519", "AAA").unwrap();
        let store = KnownHosts::load(&path).unwrap();
        assert_eq!(store.check("srv", 22, "ssh-ed25519", "AAA"), HostKeyStatus::Trusted);
        assert_eq!(store.check("srv", 22, "ssh-ed25519", "BBB"), HostKeyStatus::Changed { expected: "AAA".into() });
        assert_eq!(store.check("srv", 2222, "ssh-ed25519", "BBB"), HostKeyStatus::Unknown);
        let _ = std::fs::remove_dir_all(dir);
    }
}
