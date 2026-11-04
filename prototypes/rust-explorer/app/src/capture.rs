//! Self-test evidence: renders a top-level window with PrintWindow(PW_RENDERFULLCONTENT) into a
//! 32-bit DIB and writes an uncompressed top-down BMP. CI render only — not proof of OS DPI,
//! High Contrast or physical input behaviour.

use std::path::Path;
use windows_sys::Win32::Foundation::{HWND, RECT};
use windows_sys::Win32::Graphics::Gdi::*;
use windows_sys::Win32::Storage::Xps::PrintWindow;
use windows_sys::Win32::UI::WindowsAndMessaging::{GetWindowRect, PW_RENDERFULLCONTENT};

pub unsafe fn window_bmp(hwnd: HWND, path: &Path) -> Result<(i32, i32), String> {
    let mut rc: RECT = std::mem::zeroed();
    GetWindowRect(hwnd, &mut rc);
    let (w, h) = (rc.right - rc.left, rc.bottom - rc.top);
    if w <= 0 || h <= 0 { return Err("empty window".into()); }
    let screen = GetDC(std::ptr::null_mut());
    let mem = CreateCompatibleDC(screen);
    let mut info: BITMAPINFO = std::mem::zeroed();
    info.bmiHeader = BITMAPINFOHEADER { biSize: std::mem::size_of::<BITMAPINFOHEADER>() as u32, biWidth: w, biHeight: -h, biPlanes: 1, biBitCount: 32, biCompression: BI_RGB, biSizeImage: 0, biXPelsPerMeter: 0, biYPelsPerMeter: 0, biClrUsed: 0, biClrImportant: 0 };
    let mut bits: *mut core::ffi::c_void = std::ptr::null_mut();
    let dib = CreateDIBSection(mem, &info, DIB_RGB_COLORS, &mut bits, std::ptr::null_mut(), 0);
    let result = if dib.is_null() || bits.is_null() {
        Err("CreateDIBSection failed".to_string())
    } else {
        let old = SelectObject(mem, dib);
        let printed = PrintWindow(hwnd, mem, PW_RENDERFULLCONTENT) != 0;
        SelectObject(mem, old);
        if !printed {
            Err("PrintWindow failed".to_string())
        } else {
            let pixels = std::slice::from_raw_parts(bits as *const u8, (w * h * 4) as usize);
            write_bmp(path, w, h, pixels).map(|_| (w, h)).map_err(|e| e.to_string())
        }
    };
    if !dib.is_null() { DeleteObject(dib); }
    DeleteDC(mem);
    ReleaseDC(std::ptr::null_mut(), screen);
    result
}

/// BITMAPFILEHEADER + BITMAPINFOHEADER (negative height = top-down) + BGRA rows.
pub fn write_bmp(path: &Path, w: i32, h: i32, bgra: &[u8]) -> std::io::Result<()> {
    let mut out = Vec::with_capacity(54 + bgra.len());
    out.extend_from_slice(b"BM");
    out.extend_from_slice(&((54 + bgra.len()) as u32).to_le_bytes());
    out.extend_from_slice(&0u32.to_le_bytes());
    out.extend_from_slice(&54u32.to_le_bytes());
    out.extend_from_slice(&40u32.to_le_bytes());
    out.extend_from_slice(&w.to_le_bytes());
    out.extend_from_slice(&(-h).to_le_bytes());
    out.extend_from_slice(&1u16.to_le_bytes());
    out.extend_from_slice(&32u16.to_le_bytes());
    out.extend_from_slice(&[0u8; 24]); // BI_RGB, image size, resolution, palette: all zero
    out.extend_from_slice(bgra);
    std::fs::write(path, out)
}
