//! Embeds the application icon and manifest without rc.exe or extra crates: writes a Win32 .res
//! (RT_ICON images + RT_GROUP_ICON 1 + RT_MANIFEST 1) and hands it to link.exe, which converts
//! .res inputs itself. The manifest enables Common Controls v6 (themed controls, header sort
//! arrows, double-buffered ListView) and per-monitor v2 DPI awareness.
use std::path::PathBuf;

const RT_ICON: u16 = 3;
const RT_GROUP_ICON: u16 = 14;
const RT_MANIFEST: u16 = 24;
const LANG_EN_US: u16 = 0x0409;

fn resource(out: &mut Vec<u8>, kind: u16, id: u16, flags: u16, language: u16, data: &[u8]) {
    // RESOURCEHEADER with ordinal type and name: 32 bytes, already DWORD aligned.
    out.extend_from_slice(&(data.len() as u32).to_le_bytes());
    out.extend_from_slice(&32u32.to_le_bytes());
    out.extend_from_slice(&[0xFF, 0xFF]); out.extend_from_slice(&kind.to_le_bytes());
    out.extend_from_slice(&[0xFF, 0xFF]); out.extend_from_slice(&id.to_le_bytes());
    out.extend_from_slice(&0u32.to_le_bytes()); // DataVersion
    out.extend_from_slice(&flags.to_le_bytes()); // MemoryFlags
    out.extend_from_slice(&language.to_le_bytes());
    out.extend_from_slice(&0u32.to_le_bytes()); // Version
    out.extend_from_slice(&0u32.to_le_bytes()); // Characteristics
    out.extend_from_slice(data);
    while !out.len().is_multiple_of(4) { out.push(0); }
}

fn u16_at(b: &[u8], i: usize) -> u16 { u16::from_le_bytes([b[i], b[i + 1]]) }
fn u32_at(b: &[u8], i: usize) -> u32 { u32::from_le_bytes([b[i], b[i + 1], b[i + 2], b[i + 3]]) }

fn main() {
    let manifest_dir = PathBuf::from(std::env::var("CARGO_MANIFEST_DIR").unwrap());
    let icon_path = manifest_dir.join("../../../src/SSHExplorer.App/Assets/AppIcon.ico");
    let manifest_path = manifest_dir.join("app.manifest");
    println!("cargo:rerun-if-changed={}", icon_path.display());
    println!("cargo:rerun-if-changed={}", manifest_path.display());
    if std::env::var("CARGO_CFG_TARGET_ENV").as_deref() != Ok("msvc") { return; }

    let ico = std::fs::read(&icon_path).expect("AppIcon.ico (shared with the WPF app)");
    assert!(u16_at(&ico, 0) == 0 && u16_at(&ico, 2) == 1, "not an ICO file");
    let count = u16_at(&ico, 4) as usize;
    let mut res = Vec::new();
    resource(&mut res, 0, 0, 0, 0, &[]); // the empty leading entry every .res starts with
    let mut group = Vec::new();
    group.extend_from_slice(&0u16.to_le_bytes());
    group.extend_from_slice(&1u16.to_le_bytes());
    group.extend_from_slice(&(count as u16).to_le_bytes());
    for i in 0..count {
        let e = 6 + i * 16;
        let (size, offset) = (u32_at(&ico, e + 8) as usize, u32_at(&ico, e + 12) as usize);
        let id = (i + 1) as u16;
        resource(&mut res, RT_ICON, id, 0x1010, LANG_EN_US, &ico[offset..offset + size]);
        group.extend_from_slice(&ico[e..e + 4]); // width, height, colours, reserved
        group.extend_from_slice(&ico[e + 4..e + 8]); // planes, bit count
        group.extend_from_slice(&(size as u32).to_le_bytes());
        group.extend_from_slice(&id.to_le_bytes());
    }
    resource(&mut res, RT_GROUP_ICON, 1, 0x1030, LANG_EN_US, &group);
    let manifest = std::fs::read(&manifest_path).expect("app.manifest");
    resource(&mut res, RT_MANIFEST, 1, 0x0030, LANG_EN_US, &manifest);

    let out = PathBuf::from(std::env::var("OUT_DIR").unwrap()).join("sshx-rust-prototype.res");
    std::fs::write(&out, res).unwrap();
    println!("cargo:rustc-link-arg-bins={}", out.display());
}
