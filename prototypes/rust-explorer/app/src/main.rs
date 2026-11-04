//! SSH Explorer — Rust prototype (Win32, no browser runtime). Isolated experiment, not the product.
//!
//! Checks the hard boundaries of a native rewrite: a real Win32 window (native caption, Snap,
//! per-monitor DPI v2, Common Controls v6 through the embedded manifest, embedded app icon), tabs,
//! an editable path (Ctrl+L / Alt+D, Enter, Escape, stale replies ignored), a virtual (owner-data)
//! ListView with typed header sorting and shell icons, a lazy TreeView, SSH/SFTP with host-key
//! verification before authentication, a PTY terminal drawn with GDI from the sshx-core VT grid,
//! clipboard, search and an OLE IDropTarget (Explorer → server upload).
//! `--selftest report.json` measures startup, memory, list/terminal/sort costs, clicks a header
//! with SendInput and saves two window captures next to the report.
//!
//! Threading rule: UI handles live in `UI` (Copy), mutable data in `STATE`. A `with_state` closure
//! must not call Win32 functions that send messages (MoveWindow, SetWindowText, SendMessage,
//! SetFocus, ShowWindow …) because they re-enter the window procedures; re-entry is counted
//! (`reentrancyFaults` in the self-test) instead of aborting.
#![windows_subsystem = "windows"]
#![allow(non_snake_case, clippy::missing_safety_doc, clippy::too_many_arguments)]

mod capture;
mod ole;
mod text;

use sshx_core::known_hosts::KnownHosts;
use sshx_core::sorting::{self, Column, Entry};
use sshx_core::ssh::{self, Auth, PtyCommand, SshError};
use sshx_core::terminal::{Color, Key, Terminal};
use std::cell::{Cell, RefCell};
use std::collections::{HashMap, HashSet};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};
use std::sync::mpsc::{channel, Receiver, Sender};
use std::time::{Duration, Instant};
use windows_sys::core::PCWSTR;
use windows_sys::Win32::Foundation::*;
use windows_sys::Win32::Graphics::Gdi::*;
use windows_sys::Win32::Storage::FileSystem::{FILE_ATTRIBUTE_DIRECTORY, FILE_ATTRIBUTE_NORMAL};
use windows_sys::Win32::System::DataExchange::*;
use windows_sys::Win32::System::LibraryLoader::GetModuleHandleW;
use windows_sys::Win32::System::Memory::*;
use windows_sys::Win32::System::Ole::{OleInitialize, OleUninitialize, RegisterDragDrop, RevokeDragDrop, CF_UNICODETEXT};
use windows_sys::Win32::System::ProcessStatus::{K32GetProcessMemoryInfo, PROCESS_MEMORY_COUNTERS};
use windows_sys::Win32::System::SystemInformation::GetSystemTimePreciseAsFileTime;
use windows_sys::Win32::System::SystemServices::{SS_ENDELLIPSIS, SS_LEFTNOWORDWRAP, SS_NOPREFIX};
use windows_sys::Win32::System::Threading::{GetCurrentProcess, GetProcessTimes};
use windows_sys::Win32::System::WindowsProgramming::MulDiv;
use windows_sys::Win32::UI::Controls::*;
use windows_sys::Win32::UI::HiDpi::*;
use windows_sys::Win32::UI::Input::KeyboardAndMouse::*;
use windows_sys::Win32::UI::Shell::{SHGetFileInfoW, SHFILEINFOW, SHGFI_OPENICON, SHGFI_SMALLICON, SHGFI_SYSICONINDEX, SHGFI_USEFILEATTRIBUTES};
use windows_sys::Win32::UI::WindowsAndMessaging::*;
use text::{cell_text, icon_key, json_str};

const WM_APP_RESULT: u32 = WM_APP + 1;
const WM_APP_OUTPUT: u32 = WM_APP + 2;
const WM_APP_HOSTKEY: u32 = WM_APP + 3;
const ID_CONNECT: usize = 100;
const ID_PATH: usize = 101;
const ID_UP: usize = 102;
const ID_FIND: usize = 103;
const ID_FIND_TEXT: usize = 104;
const ID_TIMER_SELFTEST: usize = 1;
const COLUMNS: [(&str, i32); 4] = [("Имя", 320), ("Размер", 110), ("Изменён", 170), ("Права", 80)];
/// Upper bound of terminal bytes parsed per UI message, so a flood cannot starve input/paint.
const OUTPUT_SLICE: usize = 4 << 20;

static OUTPUT_PENDING: AtomicBool = AtomicBool::new(false);
static REENTRY_FAULTS: AtomicU32 = AtomicU32::new(0);
static SELFTEST_FAILED: AtomicBool = AtomicBool::new(false);

fn wide(s: &str) -> Vec<u16> { s.encode_utf16().chain(std::iter::once(0)).collect() }

#[derive(Clone, Copy, PartialEq, Eq)]
enum Target { Files, Tree { item: isize, generation: u64 } }

/// Results from worker threads, posted to the UI thread as boxed messages.
enum WorkResult {
    Connected { home: String, fingerprint: String, algorithm: String },
    Listed { seq: u64, path: String, target: Target, entries: Result<Vec<Entry>, String> },
    Uploaded { done: usize, error: Option<String> },
    TerminalClosed(Option<String>),
    Failed { message: String, alarm: bool },
}

enum WorkCommand { List { seq: u64, path: String, target: Target }, Upload { files: Vec<PathBuf>, dir: String } }

/// Sent synchronously (SendMessage) from the connection thread so the trust prompt runs on the UI thread.
struct HostKeyPrompt { text: Vec<u16> }

#[derive(Clone, Copy)]
struct Ui {
    hwnd: HWND,
    host: HWND, port: HWND, user: HWND, password: HWND, connect: HWND,
    tabs: HWND, path: HWND, up: HWND, tree: HWND, list: HWND, term: HWND, find_text: HWND, find: HWND, status: HWND,
    font: HFONT, mono: HFONT,
}

impl Ui {
    fn controls(&self) -> [HWND; 13] {
        [self.host, self.port, self.user, self.password, self.connect, self.tabs, self.path, self.up, self.tree, self.list, self.find_text, self.find, self.status]
    }
}

#[derive(Default)]
struct Report {
    idle_ws: usize, idle_private: usize, list_populate_ms: f64, after_list_ws: usize,
    header_foreground: bool, header_sent: bool, header_sorted: bool,
    files_capture: String, terminal_capture: String,
}

struct State {
    cell_w: i32, cell_h: i32, dpi: u32, mono_face: String,
    entries: Vec<Entry>, sort_column: Column, sort_desc: bool, current_path: String, home: String, nav_seq: u64,
    tree_paths: Vec<String>, tree_loaded: HashSet<usize>, tree_generation: u64,
    connected: bool, work: Option<Sender<WorkCommand>>, pty: Option<Sender<PtyCommand>>, output: Option<Receiver<Vec<u8>>>,
    terminal: Terminal, selection: Option<((usize, usize), (usize, usize))>, selecting: bool, focused: bool, find_from: Option<(usize, usize)>,
    started: Instant, first_paint: Option<(Duration, u64)>, selftest: Option<PathBuf>, selftest_stage: u32, report: Report,
    drop_registered: bool, icon_from_resource: bool,
}

thread_local! {
    static UI: Cell<Option<Ui>> = const { Cell::new(None) };
    static STATE: RefCell<Option<State>> = const { RefCell::new(None) };
    static ICONS: RefCell<HashMap<String, i32>> = RefCell::new(HashMap::new());
    static HIGH_SURROGATE: Cell<u16> = const { Cell::new(0) };
}

fn ui() -> Option<Ui> { UI.with(|u| u.get()) }

fn with_state<R>(f: impl FnOnce(&mut State) -> R) -> Option<R> {
    STATE.with(|s| match s.try_borrow_mut() {
        Ok(mut guard) => guard.as_mut().map(f),
        Err(_) => { REENTRY_FAULTS.fetch_add(1, Ordering::Relaxed); None }
    })
}

fn env(name: &str) -> String { std::env::var(name).unwrap_or_default() }

fn main() {
    let started = Instant::now();
    let args: Vec<String> = std::env::args().collect();
    let selftest = args.iter().position(|a| a == "--selftest").and_then(|i| args.get(i + 1)).map(PathBuf::from);
    let code = unsafe { run(started, selftest) };
    std::process::exit(code);
}

unsafe fn run(started: Instant, selftest: Option<PathBuf>) -> i32 {
    // The manifest already declares per-monitor v2; this call is a no-op then and a fallback otherwise.
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    OleInitialize(std::ptr::null());
    let icc = INITCOMMONCONTROLSEX { dwSize: std::mem::size_of::<INITCOMMONCONTROLSEX>() as u32, dwICC: ICC_LISTVIEW_CLASSES | ICC_TREEVIEW_CLASSES | ICC_TAB_CLASSES | ICC_STANDARD_CLASSES | ICC_BAR_CLASSES };
    InitCommonControlsEx(&icc);
    let instance = GetModuleHandleW(std::ptr::null());
    let dpi = GetDpiForSystem().max(96);
    let main_class = wide("SSHXRustMain");
    let term_class = wide("SSHXRustTerm");
    let big = load_icon(instance, SM_CXICON, dpi);
    let small = load_icon(instance, SM_CXSMICON, dpi);
    let icon_from_resource = !big.is_null();
    let wc = WNDCLASSEXW {
        cbSize: std::mem::size_of::<WNDCLASSEXW>() as u32, style: CS_HREDRAW | CS_VREDRAW, lpfnWndProc: Some(main_proc), cbClsExtra: 0, cbWndExtra: 0,
        hInstance: instance, hIcon: if big.is_null() { LoadIconW(std::ptr::null_mut(), IDI_APPLICATION) } else { big }, hCursor: LoadCursorW(std::ptr::null_mut(), IDC_ARROW),
        hbrBackground: (COLOR_WINDOW + 1) as usize as HBRUSH, lpszMenuName: std::ptr::null(), lpszClassName: main_class.as_ptr(), hIconSm: small,
    };
    RegisterClassExW(&wc);
    let tc = WNDCLASSEXW { lpfnWndProc: Some(term_proc), lpszClassName: term_class.as_ptr(), hCursor: LoadCursorW(std::ptr::null_mut(), IDC_IBEAM), hbrBackground: std::ptr::null_mut(), style: CS_DBLCLKS, ..wc };
    RegisterClassExW(&tc);
    let title = wide("SSH Explorer — Rust prototype");
    let hwnd = CreateWindowExW(WS_EX_CONTROLPARENT, main_class.as_ptr(), title.as_ptr(), WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN, CW_USEDEFAULT, CW_USEDEFAULT,
        MulDiv(1200, dpi as i32, 96), MulDiv(780, dpi as i32, 96), std::ptr::null_mut(), std::ptr::null_mut(), instance, std::ptr::null());
    if hwnd.is_null() { return 1; }
    build(hwnd, started, selftest, icon_from_resource);
    ShowWindow(hwnd, SW_SHOW);
    UpdateWindow(hwnd);
    let mut msg: MSG = std::mem::zeroed();
    while GetMessageW(&mut msg, std::ptr::null_mut(), 0, 0) > 0 {
        if accelerator(&msg) { continue; }
        if IsDialogMessageW(hwnd, &msg) != 0 { continue; }
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
    OleUninitialize();
    if SELFTEST_FAILED.load(Ordering::Relaxed) { 2 } else { 0 }
}

/// RT_GROUP_ICON 1 written by build.rs, loaded at the exact size for `dpi`; null if absent.
unsafe fn load_icon(instance: HINSTANCE, metric: SYSTEM_METRICS_INDEX, dpi: u32) -> HICON {
    let size = GetSystemMetricsForDpi(metric, dpi);
    LoadImageW(instance, 1usize as PCWSTR, IMAGE_ICON, size, size, LR_DEFAULTCOLOR) as HICON
}

unsafe fn set_window_icons(hwnd: HWND, dpi: u32) {
    let instance = GetModuleHandleW(std::ptr::null());
    for (kind, metric) in [(ICON_BIG, SM_CXICON), (ICON_SMALL, SM_CXSMICON)] {
        let icon = load_icon(instance, metric, dpi);
        if icon.is_null() { continue; }
        let old = SendMessageW(hwnd, WM_SETICON, kind as WPARAM, icon as LPARAM) as HICON;
        if !old.is_null() { DestroyIcon(old); }
    }
}

/// Keyboard routes that must work before IsDialogMessage sees the key.
unsafe fn accelerator(msg: &MSG) -> bool {
    let Some(u) = ui() else { return false };
    let key = msg.wParam as u16;
    let ctrl = GetKeyState(VK_CONTROL as i32) < 0;
    match msg.message {
        WM_KEYDOWN if key == VK_RETURN => {
            let id = if msg.hwnd == u.path { ID_PATH } else if msg.hwnd == u.find_text { ID_FIND } else if [u.host, u.port, u.user, u.password].contains(&msg.hwnd) { ID_CONNECT } else { return false };
            SendMessageW(u.hwnd, WM_COMMAND, id, 0);
            true
        }
        WM_KEYDOWN if key == VK_ESCAPE && msg.hwnd == u.path => { revert_path(u); true }
        // Ctrl+L is "clear screen" for shells, so the terminal keeps it.
        WM_KEYDOWN if key == b'L' as u16 && ctrl && msg.hwnd != u.term => { focus_path(u); true }
        WM_SYSKEYDOWN if key == b'D' as u16 && msg.hwnd != u.term => { focus_path(u); true }
        _ => false,
    }
}

unsafe fn focus_path(u: Ui) {
    if SendMessageW(u.tabs, TCM_GETCURSEL, 0, 0) != 0 { SendMessageW(u.tabs, TCM_SETCURSEL, 0, 0); layout(u.hwnd); }
    SetFocus(u.path);
    SendMessageW(u.path, EM_SETSEL, 0, -1);
}

unsafe fn revert_path(u: Ui) {
    let current = with_state(|s| s.current_path.clone()).unwrap_or_default();
    SetWindowTextW(u.path, wide(&current).as_ptr());
    SendMessageW(u.path, EM_SETSEL, 0, -1);
}

unsafe fn child(parent: HWND, class: &str, text: &str, style: u32, id: usize) -> HWND {
    let (c, t) = (wide(class), wide(text));
    CreateWindowExW(0, c.as_ptr(), t.as_ptr(), WS_CHILD | WS_VISIBLE | style, 0, 0, 10, 10, parent, id as HMENU, GetModuleHandleW(std::ptr::null()), std::ptr::null())
}

unsafe fn cue(edit: HWND, text: &str) { let t = wide(text); SendMessageW(edit, EM_SETCUEBANNER, 0, t.as_ptr() as LPARAM); }

unsafe fn create_fonts(dpi: u32) -> (HFONT, HFONT, String) {
    let make = |points: i32, pitch: u32, face: &str| {
        let name = wide(face);
        CreateFontW(-MulDiv(points, dpi as i32, 72), 0, 0, 0, FW_NORMAL as i32, 0, 0, 0, DEFAULT_CHARSET as u32, OUT_DEFAULT_PRECIS as u32, CLIP_DEFAULT_PRECIS as u32, CLEARTYPE_QUALITY as u32, pitch, name.as_ptr())
    };
    let font = make(9, (DEFAULT_PITCH | FF_DONTCARE) as u32, "Segoe UI");
    let mono_pitch = (FIXED_PITCH | FF_MODERN) as u32;
    // Cascadia Mono ships with Windows Terminal, not with every Windows; fall back to Consolas.
    let mut mono = make(10, mono_pitch, "Cascadia Mono");
    let mut face = font_face(mono);
    if face != "Cascadia Mono" { DeleteObject(mono); mono = make(10, mono_pitch, "Consolas"); face = font_face(mono); }
    (font, mono, face)
}

unsafe fn font_face(font: HFONT) -> String {
    let dc = GetDC(std::ptr::null_mut());
    let old = SelectObject(dc, font);
    let mut buf = [0u16; 64];
    GetTextFaceW(dc, buf.len() as i32, buf.as_mut_ptr());
    SelectObject(dc, old);
    ReleaseDC(std::ptr::null_mut(), dc);
    let end = buf.iter().position(|&c| c == 0).unwrap_or(buf.len());
    String::from_utf16_lossy(&buf[..end])
}

unsafe fn measure_cell(mono: HFONT) -> (i32, i32) {
    let dc = GetDC(std::ptr::null_mut());
    let old = SelectObject(dc, mono);
    let mut tm: TEXTMETRICW = std::mem::zeroed();
    GetTextMetricsW(dc, &mut tm);
    let mut size: SIZE = std::mem::zeroed();
    let m = wide("M");
    GetTextExtentPoint32W(dc, m.as_ptr(), 1, &mut size);
    SelectObject(dc, old);
    ReleaseDC(std::ptr::null_mut(), dc);
    (size.cx.max(1), tm.tmHeight.max(1))
}

unsafe fn build(hwnd: HWND, started: Instant, selftest: Option<PathBuf>, icon_from_resource: bool) {
    let dpi = GetDpiForWindow(hwnd).max(96);
    let (font, mono, mono_face) = create_fonts(dpi);
    let es = WS_BORDER | WS_TABSTOP | ES_AUTOHSCROLL as u32;
    let host = child(hwnd, "EDIT", &env("SSHX_TEST_HOST"), es, 0);
    let port_text = std::env::var("SSHX_TEST_PORT").unwrap_or_else(|_| "22".into());
    let port = child(hwnd, "EDIT", &port_text, es | ES_NUMBER as u32, 0);
    let user = child(hwnd, "EDIT", &env("SSHX_TEST_USER"), es, 0);
    let password = child(hwnd, "EDIT", "", es | ES_PASSWORD as u32, 0);
    let connect = child(hwnd, "BUTTON", "Подключиться", WS_TABSTOP | BS_PUSHBUTTON as u32, ID_CONNECT);
    let tabs = child(hwnd, "SysTabControl32", "", WS_CLIPSIBLINGS | WS_TABSTOP, 0);
    for (i, name) in ["Файлы", "Терминал"].iter().enumerate() {
        let mut text = wide(name);
        let item = TCITEMW { mask: TCIF_TEXT, dwState: 0, dwStateMask: 0, pszText: text.as_mut_ptr(), cchTextMax: 0, iImage: -1, lParam: 0 };
        SendMessageW(tabs, TCM_INSERTITEMW, i, &item as *const _ as LPARAM);
    }
    let up = child(hwnd, "BUTTON", "Вверх", WS_TABSTOP | BS_PUSHBUTTON as u32, ID_UP);
    let path = child(hwnd, "EDIT", "", es, ID_PATH);
    let tree = child(hwnd, "SysTreeView32", "", WS_BORDER | WS_TABSTOP | TVS_HASBUTTONS | TVS_HASLINES | TVS_LINESATROOT | TVS_SHOWSELALWAYS, 0);
    let list = child(hwnd, "SysListView32", "", WS_BORDER | WS_TABSTOP | LVS_REPORT | LVS_OWNERDATA | LVS_SHOWSELALWAYS | LVS_SHAREIMAGELISTS, 0);
    SendMessageW(list, LVM_SETEXTENDEDLISTVIEWSTYLE, 0, (LVS_EX_FULLROWSELECT | LVS_EX_DOUBLEBUFFER | LVS_EX_HEADERDRAGDROP) as LPARAM);
    for (i, (name, width)) in COLUMNS.iter().enumerate() {
        let mut text = wide(name);
        let col = LVCOLUMNW { mask: LVCF_TEXT | LVCF_WIDTH | LVCF_SUBITEM | LVCF_FMT, fmt: if i == 1 { LVCFMT_RIGHT } else { LVCFMT_LEFT }, cx: MulDiv(*width, dpi as i32, 96), pszText: text.as_mut_ptr(), cchTextMax: 0, iSubItem: i as i32, iImage: 0, iOrder: 0, cxMin: 0, cxDefault: 0, cxIdeal: 0 };
        SendMessageW(list, LVM_INSERTCOLUMNW, i, &col as *const _ as LPARAM);
    }
    let system_images = system_image_list();
    if system_images != 0 {
        SendMessageW(list, LVM_SETIMAGELIST, LVSIL_SMALL as WPARAM, system_images);
        SendMessageW(tree, TVM_SETIMAGELIST, TVSIL_NORMAL as WPARAM, system_images);
    }
    let find_text = child(hwnd, "EDIT", "", es, ID_FIND_TEXT);
    let find = child(hwnd, "BUTTON", "Найти выше", WS_TABSTOP | BS_PUSHBUTTON as u32, ID_FIND);
    let term_class = wide("SSHXRustTerm");
    let term = CreateWindowExW(0, term_class.as_ptr(), std::ptr::null(), WS_CHILD | WS_TABSTOP | WS_BORDER, 0, 0, 10, 10, hwnd, std::ptr::null_mut(), GetModuleHandleW(std::ptr::null()), std::ptr::null());
    let status = child(hwnd, "STATIC", "Введите адрес, пользователя и пароль. Ключ сервера проверяется до входа.", SS_LEFTNOWORDWRAP | SS_ENDELLIPSIS | SS_NOPREFIX, 0);
    cue(host, "Адрес сервера"); cue(user, "Пользователь"); cue(password, "Пароль"); cue(path, "Путь на сервере (Ctrl+L)"); cue(find_text, "Поиск в выводе");
    let u = Ui { hwnd, host, port, user, password, connect, tabs, path, up, tree, list, term, find_text, find, status, font, mono };
    for h in u.controls() { SendMessageW(h, WM_SETFONT, font as WPARAM, 1); }
    let (cell_w, cell_h) = measure_cell(mono);
    let drop_target = ole::DropTarget::create();
    let drop_registered = RegisterDragDrop(list, drop_target) == S_OK;
    ole::DropTarget::release(drop_target);
    UI.with(|c| c.set(Some(u)));
    STATE.with(|s| *s.borrow_mut() = Some(State {
        cell_w, cell_h, dpi, mono_face,
        entries: Vec::new(), sort_column: Column::Name, sort_desc: false, current_path: String::new(), home: "/".into(), nav_seq: 0,
        tree_paths: Vec::new(), tree_loaded: HashSet::new(), tree_generation: 0,
        connected: false, work: None, pty: None, output: None,
        terminal: Terminal::new(80, 24), selection: None, selecting: false, focused: false, find_from: None,
        started, first_paint: None, selftest, selftest_stage: 0, report: Report::default(),
        drop_registered, icon_from_resource,
    }));
    set_window_icons(hwnd, dpi);
    layout(hwnd);
    if !drop_registered { set_status("Перетаскивание из Проводника недоступно: RegisterDragDrop не удался."); }
    if with_state(|s| s.selftest.is_some()).unwrap_or(false) { SetTimer(hwnd, ID_TIMER_SELFTEST, 600, None); }
}

fn set_status(text: &str) {
    if let Some(u) = ui() { let w = wide(text); unsafe { SetWindowTextW(u.status, w.as_ptr()); } }
}

unsafe fn get_text(h: HWND) -> String {
    let len = GetWindowTextLengthW(h).max(0) as usize;
    let mut buf = vec![0u16; len + 1];
    let n = GetWindowTextW(h, buf.as_mut_ptr(), buf.len() as i32).max(0) as usize;
    String::from_utf16_lossy(&buf[..n])
}

/// Both pages are always positioned (only visibility toggles), so the hidden terminal keeps its
/// real size and the PTY is never opened at the 10×10 creation size.
unsafe fn layout(hwnd: HWND) {
    let (Some(u), Some(dpi)) = (ui(), with_state(|s| s.dpi)) else { return };
    let mut rc: RECT = std::mem::zeroed();
    GetClientRect(hwnd, &mut rc);
    let s = |v: i32| MulDiv(v, dpi as i32, 96);
    let (w, h) = (rc.right, rc.bottom);
    let (row, pad, gap) = (s(26), s(8), s(6));
    let mut x = pad;
    for (ctl, width) in [(u.host, 220), (u.port, 56), (u.user, 140), (u.password, 160), (u.connect, 130)] {
        MoveWindow(ctl, x, pad, s(width), row, 1);
        x += s(width) + gap;
    }
    let status_h = s(24);
    MoveWindow(u.status, pad, h - status_h + s(3), (w - pad * 2).max(0), status_h - s(6), 1);
    let tabs_rect = RECT { left: pad, top: pad + row + s(10), right: (w - pad).max(pad + 1), bottom: (h - status_h).max(pad + row + s(11)) };
    MoveWindow(u.tabs, tabs_rect.left, tabs_rect.top, tabs_rect.right - tabs_rect.left, tabs_rect.bottom - tabs_rect.top, 1);
    let mut body = tabs_rect;
    SendMessageW(u.tabs, TCM_ADJUSTRECT, 0, &mut body as *mut RECT as LPARAM);
    let inner = RECT { left: body.left + s(4), top: body.top + s(4), right: body.right - s(4), bottom: body.bottom - s(4) };
    let width = (inner.right - inner.left).max(0);
    let top = inner.top + row + gap;
    let height = (inner.bottom - top).max(0);
    let tree_w = s(240).min(width / 3);
    MoveWindow(u.up, inner.left, inner.top, s(80), row, 1);
    MoveWindow(u.path, inner.left + s(86), inner.top, (width - s(86)).max(0), row, 1);
    MoveWindow(u.tree, inner.left, top, tree_w, height, 1);
    MoveWindow(u.list, inner.left + tree_w + gap, top, (width - tree_w - gap).max(0), height, 1);
    MoveWindow(u.find_text, inner.left, inner.top, s(240), row, 1);
    MoveWindow(u.find, inner.left + s(246), inner.top, s(110), row, 1);
    MoveWindow(u.term, inner.left, top, width, height, 1);
    let files = SendMessageW(u.tabs, TCM_GETCURSEL, 0, 0) == 0;
    for ctl in [u.path, u.up, u.tree, u.list] { ShowWindow(ctl, if files { SW_SHOW } else { SW_HIDE }); }
    for ctl in [u.term, u.find_text, u.find] { ShowWindow(ctl, if files { SW_HIDE } else { SW_SHOW }); }
}

unsafe fn apply_dpi(hwnd: HWND, dpi: u32) {
    let Some(mut u) = ui() else { return };
    let old_dpi = with_state(|s| s.dpi).unwrap_or(96).max(1);
    let (font, mono, face) = create_fonts(dpi);
    for h in u.controls() { SendMessageW(h, WM_SETFONT, font as WPARAM, 1); }
    let (cell_w, cell_h) = measure_cell(mono);
    let (old_font, old_mono) = (u.font, u.mono);
    u.font = font;
    u.mono = mono;
    UI.with(|c| c.set(Some(u)));
    with_state(|s| { s.dpi = dpi; s.cell_w = cell_w; s.cell_h = cell_h; s.mono_face = face; });
    DeleteObject(old_font);
    DeleteObject(old_mono);
    for i in 0..COLUMNS.len() {
        let w = SendMessageW(u.list, LVM_GETCOLUMNWIDTH, i, 0) as i32;
        SendMessageW(u.list, LVM_SETCOLUMNWIDTH, i, MulDiv(w, dpi as i32, old_dpi as i32) as LPARAM);
    }
    set_window_icons(hwnd, dpi);
}

unsafe extern "system" fn main_proc(hwnd: HWND, msg: u32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    match msg {
        WM_SIZE => { layout(hwnd); 0 }
        WM_GETMINMAXINFO => {
            let dpi = GetDpiForWindow(hwnd).max(96) as i32;
            let info = &mut *(lparam as *mut MINMAXINFO);
            info.ptMinTrackSize = POINT { x: MulDiv(760, dpi, 96), y: MulDiv(480, dpi, 96) };
            0
        }
        WM_DPICHANGED => {
            apply_dpi(hwnd, (wparam & 0xFFFF) as u32);
            let r = &*(lparam as *const RECT);
            SetWindowPos(hwnd, std::ptr::null_mut(), r.left, r.top, r.right - r.left, r.bottom - r.top, SWP_NOZORDER | SWP_NOACTIVATE);
            layout(hwnd);
            fit_terminal();
            0
        }
        WM_PAINT => {
            let mut ps: PAINTSTRUCT = std::mem::zeroed();
            BeginPaint(hwnd, &mut ps);
            EndPaint(hwnd, &ps);
            let now = filetime_now();
            with_state(|s| if s.first_paint.is_none() { s.first_paint = Some((s.started.elapsed(), now)); });
            0
        }
        WM_CTLCOLORSTATIC => {
            // Status text on the window background instead of the dialog grey.
            SetBkColor(wparam as HDC, GetSysColor(COLOR_WINDOW));
            SetTextColor(wparam as HDC, GetSysColor(COLOR_WINDOWTEXT));
            GetSysColorBrush(COLOR_WINDOW) as LRESULT
        }
        WM_COMMAND => {
            // Only clicks and our own synthesized commands (notification code 0); EN_CHANGE etc. are ignored.
            if (wparam >> 16) & 0xFFFF == 0 {
                match wparam & 0xFFFF {
                    ID_CONNECT => connect(),
                    ID_PATH => submit_path(),
                    ID_UP => go_up(),
                    ID_FIND => find_in_terminal(),
                    _ => {}
                }
            }
            0
        }
        WM_NOTIFY => on_notify(lparam),
        WM_TIMER if wparam == ID_TIMER_SELFTEST => { KillTimer(hwnd, ID_TIMER_SELFTEST); selftest_step(hwnd); 0 }
        WM_APP_RESULT => { let result = Box::from_raw(lparam as *mut WorkResult); on_result(*result); 0 }
        WM_APP_OUTPUT => { drain_output(); 0 }
        WM_APP_HOSTKEY => {
            let prompt = &*(lparam as *const HostKeyPrompt);
            let caption = wide("Проверка ключа сервера");
            (MessageBoxW(hwnd, prompt.text.as_ptr(), caption.as_ptr(), MB_YESNO | MB_ICONWARNING | MB_DEFBUTTON2) == IDYES) as LRESULT
        }
        WM_DESTROY => {
            if let Some(u) = ui() { RevokeDragDrop(u.list); }
            with_state(|s| { if let Some(p) = s.pty.take() { let _ = p.send(PtyCommand::Close); } s.work = None; });
            PostQuitMessage(0);
            0
        }
        _ => DefWindowProcW(hwnd, msg, wparam, lparam),
    }
}

fn post_result(hwnd_word: isize, result: WorkResult) {
    let raw = Box::into_raw(Box::new(result));
    unsafe { if PostMessageW(hwnd_word as HWND, WM_APP_RESULT, 0, raw as LPARAM) == 0 { drop(Box::from_raw(raw)); } }
}

fn connect() {
    let Some(u) = ui() else { return };
    let (host, port_text, user, password) = unsafe { (get_text(u.host).trim().to_string(), get_text(u.port), get_text(u.user).trim().to_string(), get_text(u.password)) };
    if host.is_empty() || user.is_empty() { set_status("Укажите адрес сервера и пользователя."); return; }
    let port = match port_text.trim().parse::<u16>() { Ok(p) if p > 0 => p, _ => { set_status("Порт — число от 1 до 65535."); return; } };
    let auth = match std::env::var_os("SSHX_TEST_KEY") { Some(k) => Auth::KeyFile { path: PathBuf::from(k), passphrase: None }, None => Auth::Password(password) };
    // Test hook, honoured only together with --selftest: trust an unknown key without a prompt.
    let accept_new = with_state(|s| s.selftest.is_some()).unwrap_or(false) && std::env::var_os("SSHX_SELFTEST_ACCEPT_NEW_KEY").is_some();
    let (work_tx, work_rx) = channel::<WorkCommand>();
    let (pty_tx, pty_rx) = channel::<PtyCommand>();
    let (out_tx, out_rx) = channel::<Vec<u8>>();
    let size = with_state(|s| {
        if let Some(p) = s.pty.take() { let _ = p.send(PtyCommand::Close); }
        s.work = Some(work_tx); s.pty = Some(pty_tx); s.output = Some(out_rx);
        s.connected = false; s.tree_generation += 1; s.tree_paths.clear(); s.tree_loaded.clear(); s.entries.clear(); s.nav_seq += 1;
        (s.terminal.cols() as u32, s.terminal.rows() as u32)
    });
    let (cols, rows) = size.unwrap_or((80, 24));
    unsafe {
        EnableWindow(u.connect, 0);
        SendMessageW(u.list, LVM_SETITEMCOUNT, 0, 0);
        SendMessageW(u.tree, TVM_DELETEITEM, 0, TVI_ROOT as LPARAM);
    }
    set_status(&format!("Подключение к {host}:{port}…"));
    let hwnd_word = u.hwnd as isize;
    std::thread::spawn(move || worker(hwnd_word, host, port, user, auth, accept_new, cols, rows, work_rx, pty_rx, out_tx));
}

fn worker(hwnd_word: isize, host: String, port: u16, user: String, auth: Auth, accept_new: bool, cols: u32, rows: u32,
          work_rx: Receiver<WorkCommand>, pty_rx: Receiver<PtyCommand>, out_tx: Sender<Vec<u8>>) {
    let post = |r: WorkResult| post_result(hwnd_word, r);
    let mut known = match KnownHosts::load(&known_hosts_path()) {
        Ok(k) => k,
        Err(e) => { post(WorkResult::Failed { message: format!("хранилище ключей: {e}"), alarm: false }); return; }
    };
    let mut decide = |info: &ssh::HostKeyInfo| -> bool {
        if accept_new { return true; }
        let prompt = HostKeyPrompt { text: wide(&format!(
            "Первое подключение к {host}:{port}.\n\nАлгоритм: {}\nОтпечаток: {}\n\nСверьте отпечаток с администратором сервера. Доверять этому ключу?",
            info.algorithm, info.fingerprint)) };
        unsafe { SendMessageW(hwnd_word as HWND, WM_APP_HOSTKEY, 0, &prompt as *const HostKeyPrompt as LPARAM) == 1 }
    };
    let timeout = Duration::from_secs(15);
    let (session, info) = match ssh::connect(&host, port, &user, &auth, &mut known, timeout, &mut decide) {
        Ok(v) => v,
        Err(e) => { let alarm = matches!(e, SshError::HostKeyChanged { .. }); post(WorkResult::Failed { message: e.to_string(), alarm }); return; }
    };
    let home = ssh::home(&session).unwrap_or_else(|_| "/".into());
    post(WorkResult::Connected { home, fingerprint: info.fingerprint.clone(), algorithm: info.algorithm.clone() });
    // Independent second connection for the PTY; its key is trusted by now, so it can never prompt.
    let mut never = |_: &ssh::HostKeyInfo| false;
    match ssh::connect(&host, port, &user, &auth, &mut known, timeout, &mut never) {
        Ok((pty_session, _)) => {
            std::thread::spawn(move || {
                let mut forward = |bytes: &[u8]| -> bool {
                    if out_tx.send(bytes.to_vec()).is_err() { return false; }
                    if !OUTPUT_PENDING.swap(true, Ordering::AcqRel) { unsafe { PostMessageW(hwnd_word as HWND, WM_APP_OUTPUT, 0, 0); } }
                    true
                };
                let result = ssh::run_pty(pty_session, cols, rows, pty_rx, &mut forward);
                post_result(hwnd_word, WorkResult::TerminalClosed(result.err().map(|e| e.to_string())));
            });
        }
        Err(e) => post(WorkResult::Failed { message: format!("терминал: {e}"), alarm: false }),
    }
    let sftp = match session.sftp() { Ok(s) => s, Err(e) => { post(WorkResult::Failed { message: format!("SFTP: {e}"), alarm: false }); return; } };
    for command in work_rx {
        match command {
            WorkCommand::List { seq, path, target } => {
                let path = if target == Target::Files { ssh::realpath(&sftp, &path).unwrap_or(path) } else { path };
                let entries = ssh::list(&sftp, &path).map_err(|e| e.to_string());
                post(WorkResult::Listed { seq, path, target, entries });
            }
            WorkCommand::Upload { files, dir } => {
                let (mut done, mut error) = (0, None);
                for f in &files {
                    match ssh::upload(&sftp, f, &dir) {
                        Ok(_) => done += 1,
                        Err(e) => { error = Some(format!("{}: {e}", f.file_name().map(|n| n.to_string_lossy().to_string()).unwrap_or_default())); break; }
                    }
                }
                post(WorkResult::Uploaded { done, error });
            }
        }
    }
}

fn known_hosts_path() -> PathBuf {
    if let Some(p) = std::env::var_os("SSHX_KNOWN_HOSTS") { return PathBuf::from(p); }
    let base = std::env::var_os("APPDATA").map(PathBuf::from).unwrap_or_else(std::env::temp_dir);
    base.join("SSHExplorerRustPrototype").join("known_hosts")
}

fn join(dir: &str, name: &str) -> String { if dir.ends_with('/') { format!("{dir}{name}") } else { format!("{dir}/{name}") } }

/// Requests a listing for the file view. Relative paths resolve against the current folder,
/// "~" against the login folder. Every request gets a new sequence number; older replies are dropped.
fn navigate(path: &str) -> bool {
    let path = path.trim().to_string();
    if path.is_empty() || path.contains('\0') { return false; }
    with_state(|s| {
        if !s.connected { return false; }
        let resolved = if path == "~" { s.home.clone() }
            else if let Some(rest) = path.strip_prefix("~/") { join(&s.home, rest) }
            else if path.starts_with('/') { path }
            else { join(&s.current_path, &path) };
        s.nav_seq += 1;
        let seq = s.nav_seq;
        s.work.as_ref().map(|w| w.send(WorkCommand::List { seq, path: resolved, target: Target::Files }).is_ok()).unwrap_or(false)
    }).unwrap_or(false)
}

fn request_tree(path: String, item: HTREEITEM, index: usize) {
    with_state(|s| {
        if !s.tree_loaded.insert(index) { return; }
        let generation = s.tree_generation;
        if let Some(w) = &s.work { let _ = w.send(WorkCommand::List { seq: 0, path, target: Target::Tree { item, generation } }); }
    });
}

fn submit_path() {
    let Some(u) = ui() else { return };
    let text = unsafe { get_text(u.path) };
    if !navigate(&text) { set_status(if with_state(|s| s.connected).unwrap_or(false) { "Введите путь на сервере." } else { "Сначала подключитесь к серверу." }); }
}

fn go_up() {
    let current = with_state(|s| s.current_path.clone()).unwrap_or_default();
    let trimmed = current.trim_end_matches('/');
    let parent = match trimmed.rfind('/') { Some(0) | None => "/".to_string(), Some(i) => trimmed[..i].to_string() };
    navigate(&parent);
}

fn can_upload() -> bool { with_state(|s| s.connected && s.work.is_some()).unwrap_or(false) }

fn upload_dropped(files: Vec<PathBuf>) {
    let (files, folders): (Vec<PathBuf>, Vec<PathBuf>) = files.into_iter().partition(|p| !p.is_dir());
    if files.is_empty() { set_status(&format!("Папки прототип пока не загружает (перетащено: {}).", folders.len())); return; }
    let count = files.len();
    let sent = with_state(|s| { let dir = s.current_path.clone(); s.work.as_ref().map(|w| w.send(WorkCommand::Upload { files, dir }).is_ok()).unwrap_or(false) }).unwrap_or(false);
    let skipped = if folders.is_empty() { String::new() } else { format!(", папки пропущены: {}", folders.len()) };
    set_status(&if sent { format!("Загрузка файлов: {count}{skipped}. Существующие файлы не перезаписываются.") } else { "Нет подключения для загрузки.".to_string() });
}

unsafe fn on_result(result: WorkResult) {
    let Some(u) = ui() else { return };
    match result {
        WorkResult::Connected { home, fingerprint, algorithm } => {
            EnableWindow(u.connect, 1);
            with_state(|s| { s.connected = true; s.home = home.clone(); });
            SendMessageW(u.tree, TVM_DELETEITEM, 0, TVI_ROOT as LPARAM);
            let (root, index) = insert_tree(u.tree, TVI_ROOT, "/", "/");
            request_tree("/".into(), root, index);
            navigate(&home);
            set_status(&format!("Подключено · ключ {algorithm} {fingerprint} проверен до входа"));
        }
        WorkResult::Listed { seq, path, target: Target::Files, entries } => {
            if with_state(|s| s.nav_seq) != Some(seq) { return; }
            match entries {
                Ok(entries) => {
                    let count = with_state(|s| {
                        s.current_path = path.clone();
                        s.entries = entries;
                        let (column, descending) = (s.sort_column, s.sort_desc);
                        sorting::sort(&mut s.entries, column, descending);
                        s.entries.len()
                    }).unwrap_or(0);
                    SetWindowTextW(u.path, wide(&path).as_ptr());
                    SendMessageW(u.list, LVM_SETITEMCOUNT, count, 0);
                    select_only(u.list, None);
                    if count > 0 { SendMessageW(u.list, LVM_ENSUREVISIBLE, 0, 0); }
                    InvalidateRect(u.list, std::ptr::null(), 1);
                    update_sort_arrows(u);
                    set_status(&format!("{path} · объектов: {count}"));
                }
                // The typed text stays in the path box for correction.
                Err(e) => set_status(&format!("Не удалось открыть {path}: {e}")),
            }
        }
        WorkResult::Listed { path, target: Target::Tree { item, generation }, entries, .. } => {
            if with_state(|s| s.tree_generation) != Some(generation) { return; }
            let item = item as HTREEITEM;
            clear_children(u.tree, item);
            match entries {
                Ok(mut list) => {
                    list.retain(|e| e.is_dir);
                    sorting::sort(&mut list, Column::Name, false);
                    for e in &list { insert_tree(u.tree, item, &e.name, &join(&path, &e.name)); }
                    if list.is_empty() { set_has_children(u.tree, item, false); } else { SendMessageW(u.tree, TVM_EXPAND, TVE_EXPAND as WPARAM, item as LPARAM); }
                }
                Err(e) => { set_has_children(u.tree, item, false); set_status(&format!("Дерево: {path}: {e}")); }
            }
        }
        WorkResult::Uploaded { done, error } => {
            match error { None => set_status(&format!("Загружено файлов: {done}")), Some(e) => set_status(&format!("Загружено: {done}; остановлено: {e}")) }
            let current = with_state(|s| s.current_path.clone()).unwrap_or_default();
            navigate(&current);
        }
        WorkResult::TerminalClosed(error) => set_status(&match error { None => "Терминал закрыт.".to_string(), Some(e) => format!("Терминал: {e}") }),
        WorkResult::Failed { message, alarm } => {
            EnableWindow(u.connect, 1);
            set_status(&message);
            if alarm {
                let (text, caption) = (wide(&message), wide("Ключ сервера изменился"));
                MessageBoxW(u.hwnd, text.as_ptr(), caption.as_ptr(), MB_OK | MB_ICONERROR);
            }
        }
    }
}

unsafe fn insert_tree(tree: HWND, parent: HTREEITEM, text: &str, full: &str) -> (HTREEITEM, usize) {
    let index = with_state(|s| { s.tree_paths.push(full.to_string()); s.tree_paths.len() - 1 }).unwrap_or(usize::MAX);
    let mut label = wide(text);
    let (closed, open) = (icon_index("\0dir"), icon_index("\0dir-open"));
    let item = TVITEMW {
        mask: TVIF_TEXT | TVIF_PARAM | TVIF_CHILDREN | TVIF_IMAGE | TVIF_SELECTEDIMAGE, hItem: 0, state: 0, stateMask: 0,
        pszText: label.as_mut_ptr(), cchTextMax: 0, iImage: closed, iSelectedImage: open, cChildren: 1, lParam: index as LPARAM,
    };
    let insert = TVINSERTSTRUCTW { hParent: parent, hInsertAfter: TVI_LAST, Anonymous: TVINSERTSTRUCTW_0 { item } };
    (SendMessageW(tree, TVM_INSERTITEMW, 0, &insert as *const _ as LPARAM) as HTREEITEM, index)
}

unsafe fn set_has_children(tree: HWND, item: HTREEITEM, has: bool) {
    let mut tv: TVITEMW = std::mem::zeroed();
    tv.mask = TVIF_CHILDREN;
    tv.hItem = item;
    tv.cChildren = has as i32;
    SendMessageW(tree, TVM_SETITEMW, 0, &tv as *const _ as LPARAM);
}

unsafe fn clear_children(tree: HWND, item: HTREEITEM) {
    loop {
        let child = SendMessageW(tree, TVM_GETNEXTITEM, TVGN_CHILD as WPARAM, item as LPARAM) as HTREEITEM;
        if child == 0 { break; }
        SendMessageW(tree, TVM_DELETEITEM, 0, child as LPARAM);
    }
}

unsafe fn select_only(list: HWND, index: Option<usize>) {
    let mut item: LVITEMW = std::mem::zeroed();
    item.stateMask = LVIS_SELECTED | LVIS_FOCUSED;
    SendMessageW(list, LVM_SETITEMSTATE, usize::MAX, &item as *const _ as LPARAM);
    if let Some(i) = index {
        item.state = LVIS_SELECTED | LVIS_FOCUSED;
        SendMessageW(list, LVM_SETITEMSTATE, i, &item as *const _ as LPARAM);
        SendMessageW(list, LVM_ENSUREVISIBLE, i, 0);
    }
}

/// Typed header sort; the focused row is kept by name across the re-sort.
unsafe fn sort_by(u: Ui, column: Column) {
    let focused = SendMessageW(u.list, LVM_GETNEXTITEM, usize::MAX, (LVNI_SELECTED | LVNI_FOCUSED) as LPARAM) as isize;
    let restored = with_state(|s| {
        let name = if focused >= 0 { s.entries.get(focused as usize).map(|e| e.name.clone()) } else { None };
        s.sort_desc = if s.sort_column == column { !s.sort_desc } else { false };
        s.sort_column = column;
        let descending = s.sort_desc;
        sorting::sort(&mut s.entries, column, descending);
        name.and_then(|n| s.entries.iter().position(|e| e.name == n))
    }).flatten();
    select_only(u.list, restored);
    InvalidateRect(u.list, std::ptr::null(), 0);
    update_sort_arrows(u);
}

unsafe fn update_sort_arrows(u: Ui) {
    let Some((column, descending)) = with_state(|s| (s.sort_column, s.sort_desc)) else { return };
    let active = match column { Column::Name => 0, Column::Size => 1, Column::Modified => 2, Column::Permissions => 3 };
    let header = SendMessageW(u.list, LVM_GETHEADER, 0, 0) as HWND;
    for i in 0..COLUMNS.len() {
        let mut item: HDITEMW = std::mem::zeroed();
        item.mask = HDI_FORMAT;
        SendMessageW(header, HDM_GETITEMW, i, &mut item as *mut _ as LPARAM);
        item.fmt &= !(HDF_SORTUP | HDF_SORTDOWN);
        if i == active { item.fmt |= if descending { HDF_SORTDOWN } else { HDF_SORTUP }; }
        SendMessageW(header, HDM_SETITEMW, i, &item as *const _ as LPARAM);
    }
}

/// Shared system image list (shell icons); never destroyed by the controls (LVS_SHAREIMAGELISTS).
unsafe fn system_image_list() -> LPARAM {
    let mut info: SHFILEINFOW = std::mem::zeroed();
    let name = wide("file");
    SHGetFileInfoW(name.as_ptr(), FILE_ATTRIBUTE_NORMAL, &mut info, std::mem::size_of::<SHFILEINFOW>() as u32, SHGFI_SYSICONINDEX | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES) as LPARAM
}

/// Shell icon index by extension only (SHGFI_USEFILEATTRIBUTES: no disk access, nothing executed).
fn icon_index(key: &str) -> i32 {
    if let Some(i) = ICONS.with(|c| c.borrow().get(key).copied()) { return i; }
    let (name, attributes, extra) = match key {
        "\0dir" => ("folder".to_string(), FILE_ATTRIBUTE_DIRECTORY, 0),
        "\0dir-open" => ("folder".to_string(), FILE_ATTRIBUTE_DIRECTORY, SHGFI_OPENICON),
        ext => (format!("file{ext}"), FILE_ATTRIBUTE_NORMAL, 0),
    };
    let mut info: SHFILEINFOW = unsafe { std::mem::zeroed() };
    let w = wide(&name);
    unsafe { SHGetFileInfoW(w.as_ptr(), attributes, &mut info, std::mem::size_of::<SHFILEINFOW>() as u32, SHGFI_SYSICONINDEX | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES | extra); }
    ICONS.with(|c| c.borrow_mut().insert(key.to_string(), info.iIcon));
    info.iIcon
}

unsafe fn on_notify(lparam: LPARAM) -> LRESULT {
    let Some(u) = ui() else { return 0 };
    let hdr = &*(lparam as *const NMHDR);
    if hdr.hwndFrom == u.list {
        match hdr.code {
            LVN_GETDISPINFOW => {
                let info = &mut *(lparam as *mut NMLVDISPINFOW);
                let (row, column, mask) = (info.item.iItem, info.item.iSubItem, info.item.mask);
                let cell = with_state(|s| s.entries.get(row as usize).map(|e| (cell_text(e, column), icon_key(e)))).flatten();
                if let Some((text, key)) = cell {
                    if mask & LVIF_TEXT != 0 && !info.item.pszText.is_null() && info.item.cchTextMax > 0 {
                        let w: Vec<u16> = text.encode_utf16().collect();
                        let n = w.len().min(info.item.cchTextMax as usize - 1);
                        std::ptr::copy_nonoverlapping(w.as_ptr(), info.item.pszText, n);
                        *info.item.pszText.add(n) = 0;
                    }
                    if mask & LVIF_IMAGE != 0 && column == 0 { info.item.iImage = icon_index(&key); }
                }
            }
            LVN_COLUMNCLICK => {
                let info = &*(lparam as *const NMLISTVIEW);
                let column = match info.iSubItem { 1 => Column::Size, 2 => Column::Modified, 3 => Column::Permissions, _ => Column::Name };
                sort_by(u, column);
            }
            LVN_ITEMACTIVATE => {
                let info = &*(lparam as *const NMITEMACTIVATE);
                let target = with_state(|s| s.entries.get(info.iItem.max(0) as usize).map(|e| (e.is_dir, join(&s.current_path, &e.name)))).flatten();
                match target {
                    Some((true, path)) => { navigate(&path); }
                    Some((false, _)) => set_status("Открытие и редактирование файлов в прототипе не реализованы."),
                    None => {}
                }
            }
            LVN_KEYDOWN => {
                let info = &*(lparam as *const NMLVKEYDOWN);
                if info.wVKey == VK_BACK { go_up(); }
            }
            _ => {}
        }
    } else if hdr.hwndFrom == u.tree && hdr.code == TVN_ITEMEXPANDINGW {
        let info = &*(lparam as *const NMTREEVIEWW);
        if info.action == TVE_EXPAND {
            let index = info.itemNew.lParam as usize;
            if let Some(p) = with_state(|s| s.tree_paths.get(index).cloned()).flatten() { request_tree(p, info.itemNew.hItem, index); }
        }
    } else if hdr.hwndFrom == u.tree && hdr.code == TVN_SELCHANGEDW {
        let info = &*(lparam as *const NMTREEVIEWW);
        // Only user-driven changes navigate (deleting items also changes the selection).
        if info.action == TVC_BYMOUSE || info.action == TVC_BYKEYBOARD {
            if let Some(p) = with_state(|s| s.tree_paths.get(info.itemNew.lParam as usize).cloned()).flatten() { navigate(&p); }
        }
    } else if hdr.hwndFrom == u.tabs && hdr.code == TCN_SELCHANGE {
        layout(u.hwnd);
        if SendMessageW(u.tabs, TCM_GETCURSEL, 0, 0) == 1 { SetFocus(u.term); fit_terminal(); }
    }
    0
}

fn drain_output() {
    OUTPUT_PENDING.store(false, Ordering::Release);
    let mut more = false;
    let drained = with_state(|s| {
        let mut fed = 0usize;
        if let Some(rx) = &s.output {
            while fed < OUTPUT_SLICE {
                match rx.try_recv() { Ok(chunk) => { fed += chunk.len(); s.terminal.feed(&chunk); } Err(_) => break }
            }
            more = fed >= OUTPUT_SLICE;
        }
        (fed > 0, s.terminal.take_responses())
    });
    let Some((any, responses)) = drained else { return };
    if !responses.is_empty() { send_input(responses); }
    if more && !OUTPUT_PENDING.swap(true, Ordering::AcqRel) {
        if let Some(u) = ui() { unsafe { PostMessageW(u.hwnd, WM_APP_OUTPUT, 0, 0); } }
    }
    if any { if let Some(u) = ui() { unsafe { InvalidateRect(u.term, std::ptr::null(), 0); } } }
}

fn send_input(bytes: Vec<u8>) {
    with_state(|s| { s.terminal.view_offset = 0; if let Some(p) = &s.pty { let _ = p.send(PtyCommand::Input(bytes)); } });
}

fn fit_terminal() {
    let Some(u) = ui() else { return };
    let mut rc: RECT = unsafe { std::mem::zeroed() };
    unsafe { GetClientRect(u.term, &mut rc); }
    let resized = with_state(|s| {
        let (cols, rows) = ((rc.right / s.cell_w).max(2) as usize, (rc.bottom / s.cell_h).max(1) as usize);
        if (cols, rows) == (s.terminal.cols(), s.terminal.rows()) { return false; }
        s.terminal.resize(cols, rows);
        if let Some(p) = &s.pty { let _ = p.send(PtyCommand::Resize(cols as u32, rows as u32)); }
        true
    }).unwrap_or(false);
    if resized { unsafe { InvalidateRect(u.term, std::ptr::null(), 0); } }
}

/// Each press selects the previous match (towards older output), wrapping to the newest.
fn find_in_terminal() {
    let Some(u) = ui() else { return };
    let needle = unsafe { get_text(u.find_text) };
    if needle.is_empty() { return; }
    let count = with_state(|s| {
        let hits = s.terminal.find(&needle);
        let Some(&newest) = hits.last() else { s.find_from = None; return 0; };
        let pick = s.find_from.and_then(|prev| hits.iter().rev().find(|h| **h < prev).copied()).unwrap_or(newest);
        s.find_from = Some(pick);
        let (line, col) = pick;
        let screen_start = s.terminal.total_lines() - s.terminal.rows();
        s.terminal.view_offset = screen_start.saturating_sub(line.min(screen_start));
        s.selection = Some(((line, col), (line, col + needle.chars().count().saturating_sub(1))));
        hits.len()
    }).unwrap_or(0);
    unsafe { InvalidateRect(u.term, std::ptr::null(), 0); }
    set_status(&if count == 0 { format!("«{needle}» не найдено") } else { format!("Совпадений: {count}") });
}

/// Campbell palette (Windows Terminal default) for the 16 base colours.
const PALETTE: [(u8, u8, u8); 16] = [(12, 12, 12), (197, 15, 31), (19, 161, 14), (193, 156, 0), (0, 55, 218), (136, 23, 152), (58, 150, 221), (204, 204, 204),
    (118, 118, 118), (231, 72, 86), (22, 198, 12), (249, 241, 165), (59, 120, 255), (180, 0, 158), (97, 214, 214), (242, 242, 242)];

fn rgb(c: Color, fg: bool, bold: bool) -> COLORREF {
    let (r, g, b) = match c {
        Color::Default => if fg { (204, 204, 204) } else { (12, 12, 12) },
        Color::Indexed(i) if i < 8 && fg && bold => PALETTE[i as usize + 8],
        Color::Indexed(i) if i < 16 => PALETTE[i as usize],
        Color::Indexed(i) if i < 232 => { let i = i - 16; let v = |x: u8| if x == 0 { 0 } else { 55 + x * 40 }; (v(i / 36), v(i / 6 % 6), v(i % 6)) }
        Color::Indexed(i) => { let v = 8 + (i - 232) * 10; (v, v, v) }
        Color::Rgb(r, g, b) => (r, g, b),
    };
    (r as u32) | ((g as u32) << 8) | ((b as u32) << 16)
}

/// Draws the visible grid into `dc` in runs of identical attributes. Every glyph gets an explicit
/// advance (lpDx) of one or two cells, so wide and fallback glyphs never shift the grid.
unsafe fn paint_terminal(dc: HDC, terminal: &Terminal, mono: HFONT, cell_w: i32, cell_h: i32, width: i32, height: i32,
                         selection: Option<((usize, usize), (usize, usize))>, focused: bool) {
    let background = CreateSolidBrush(rgb(Color::Default, false, false));
    let all = RECT { left: 0, top: 0, right: width, bottom: height };
    FillRect(dc, &all, background);
    DeleteObject(background);
    let old = SelectObject(dc, mono);
    let first_line = terminal.total_lines() - terminal.rows() - terminal.view_offset.min(terminal.scrollback_len());
    let range = selection.map(|(a, b)| if a <= b { (a, b) } else { (b, a) });
    let (mut text, mut dx): (Vec<u16>, Vec<i32>) = (Vec::with_capacity(256), Vec::with_capacity(256));
    for (y, row) in terminal.visible().iter().enumerate() {
        let line = first_line + y;
        let selected = |col: usize| range.map(|(a, b)| (line, col) >= a && (line, col) <= b).unwrap_or(false);
        let mut x = 0usize;
        while x < row.len() {
            let (attr, sel, start) = (row[x].attr, selected(x), x);
            text.clear();
            dx.clear();
            while x < row.len() && row[x].attr == attr && selected(x) == sel {
                if !row[x].spacer {
                    let span = if x + 1 < row.len() && row[x + 1].spacer { 2 } else { 1 };
                    let mut buf = [0u16; 2];
                    for (k, unit) in row[x].ch.encode_utf16(&mut buf).iter().enumerate() { text.push(*unit); dx.push(if k == 0 { span * cell_w } else { 0 }); }
                }
                x += 1;
            }
            let (mut fg, mut bg) = (rgb(attr.fg, true, attr.bold), rgb(attr.bg, false, false));
            if attr.inverse ^ sel { std::mem::swap(&mut fg, &mut bg); }
            SetTextColor(dc, fg);
            SetBkColor(dc, bg);
            let cell = RECT { left: start as i32 * cell_w, top: y as i32 * cell_h, right: x as i32 * cell_w, bottom: (y as i32 + 1) * cell_h };
            ExtTextOutW(dc, cell.left, cell.top, ETO_OPAQUE | ETO_CLIPPED, &cell, text.as_ptr(), text.len() as u32, dx.as_ptr());
            if attr.underline {
                let brush = CreateSolidBrush(fg);
                let line_rect = RECT { left: cell.left, top: cell.bottom - 2, right: cell.right, bottom: cell.bottom - 1 };
                FillRect(dc, &line_rect, brush);
                DeleteObject(brush);
            }
        }
    }
    if terminal.cursor_visible() {
        let (r, c) = terminal.cursor();
        let thickness = if focused { 2 } else { 1 };
        let caret = RECT { left: c as i32 * cell_w, top: (r as i32 + 1) * cell_h - thickness, right: (c as i32 + 1) * cell_w, bottom: (r as i32 + 1) * cell_h };
        let brush = CreateSolidBrush(rgb(Color::Default, true, false));
        FillRect(dc, &caret, brush);
        DeleteObject(brush);
    }
    SelectObject(dc, old);
}

fn key_down(vk: VIRTUAL_KEY) -> bool { unsafe { GetKeyState(vk as i32) < 0 } }

unsafe extern "system" fn term_proc(hwnd: HWND, msg: u32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    match msg {
        WM_PAINT => {
            let mut ps: PAINTSTRUCT = std::mem::zeroed();
            let dc = BeginPaint(hwnd, &mut ps);
            let mut rc: RECT = std::mem::zeroed();
            GetClientRect(hwnd, &mut rc);
            let mem = CreateCompatibleDC(dc);
            let bmp = CreateCompatibleBitmap(dc, rc.right.max(1), rc.bottom.max(1));
            let old = SelectObject(mem, bmp);
            let mono = ui().map(|u| u.mono).unwrap_or(std::ptr::null_mut());
            with_state(|s| paint_terminal(mem, &s.terminal, mono, s.cell_w, s.cell_h, rc.right, rc.bottom, s.selection, s.focused));
            BitBlt(dc, 0, 0, rc.right, rc.bottom, mem, 0, 0, SRCCOPY);
            SelectObject(mem, old);
            DeleteObject(bmp);
            DeleteDC(mem);
            EndPaint(hwnd, &ps);
            0
        }
        WM_ERASEBKGND => 1,
        WM_SIZE => { fit_terminal(); 0 }
        WM_SETFOCUS | WM_KILLFOCUS => { with_state(|s| s.focused = msg == WM_SETFOCUS); InvalidateRect(hwnd, std::ptr::null(), 0); 0 }
        WM_LBUTTONDOWN | WM_LBUTTONDBLCLK => {
            SetFocus(hwnd);
            let pos = cell_at(lparam);
            with_state(|s| { s.selection = Some((pos, pos)); s.selecting = true; });
            SetCapture(hwnd);
            InvalidateRect(hwnd, std::ptr::null(), 0);
            0
        }
        WM_MOUSEMOVE => {
            if with_state(|s| s.selecting).unwrap_or(false) {
                let pos = cell_at(lparam);
                with_state(|s| if let Some((start, _)) = s.selection { s.selection = Some((start, pos)); });
                InvalidateRect(hwnd, std::ptr::null(), 0);
            }
            0
        }
        WM_LBUTTONUP => {
            ReleaseCapture();
            // A click without a drag clears the selection.
            with_state(|s| { s.selecting = false; if let Some((a, b)) = s.selection { if a == b { s.selection = None; } } });
            InvalidateRect(hwnd, std::ptr::null(), 0);
            0
        }
        WM_RBUTTONUP => {
            // Windows Terminal / PuTTY convention: right click copies a selection, otherwise pastes.
            if with_state(|s| s.selection.is_some()).unwrap_or(false) { copy_selection(hwnd); with_state(|s| s.selection = None); InvalidateRect(hwnd, std::ptr::null(), 0); } else { paste(hwnd); }
            0
        }
        WM_MOUSEWHEEL => {
            let delta = ((wparam >> 16) & 0xFFFF) as u16 as i16 as isize;
            with_state(|s| s.terminal.scroll_view(delta * 3 / 120));
            InvalidateRect(hwnd, std::ptr::null(), 0);
            0
        }
        WM_CHAR => {
            if key_down(VK_CONTROL) && key_down(VK_SHIFT) { return 0; }
            let unit = wparam as u32;
            let bytes: Vec<u8> = match unit {
                0x08 => vec![0x7f],
                0xD800..=0xDBFF => { HIGH_SURROGATE.with(|h| h.set(unit as u16)); return 0; }
                0xDC00..=0xDFFF => {
                    let high = HIGH_SURROGATE.with(|h| h.replace(0));
                    match char::decode_utf16([high, unit as u16]).next() { Some(Ok(c)) => c.to_string().into_bytes(), _ => return 0 }
                }
                _ => match char::from_u32(unit) { Some(c) => c.to_string().into_bytes(), None => return 0 },
            };
            send_input(bytes);
            0
        }
        WM_SYSCHAR if wparam != b' ' as usize => {
            // Alt+key as Meta (ESC prefix); Alt+Space still opens the system menu.
            let mut bytes = vec![0x1b];
            if let Some(c) = char::from_u32(wparam as u32) { bytes.extend_from_slice(c.to_string().as_bytes()); }
            send_input(bytes);
            0
        }
        WM_KEYDOWN => {
            let (ctrl, shift) = (key_down(VK_CONTROL), key_down(VK_SHIFT));
            let vk = wparam as VIRTUAL_KEY;
            if (ctrl && shift && vk == b'C' as u16) || (ctrl && vk == VK_INSERT) { copy_selection(hwnd); return 0; }
            if (ctrl && shift && vk == b'V' as u16) || (shift && vk == VK_INSERT) { paste(hwnd); return 0; }
            if ctrl && shift && vk == b'F' as u16 { if let Some(u) = ui() { SetFocus(u.find_text); } return 0; }
            if shift && (vk == VK_PRIOR || vk == VK_NEXT) {
                with_state(|s| { let page = s.terminal.rows() as isize; s.terminal.scroll_view(if vk == VK_PRIOR { page } else { -page }); });
                InvalidateRect(hwnd, std::ptr::null(), 0);
                return 0;
            }
            let key = match vk {
                VK_UP => Some(Key::Up), VK_DOWN => Some(Key::Down), VK_LEFT => Some(Key::Left), VK_RIGHT => Some(Key::Right),
                VK_HOME => Some(Key::Home), VK_END => Some(Key::End), VK_PRIOR => Some(Key::PageUp), VK_NEXT => Some(Key::PageDown),
                VK_INSERT => Some(Key::Insert), VK_DELETE => Some(Key::Delete),
                v if (VK_F1..=VK_F12).contains(&v) => Some(Key::F((v - VK_F1 + 1) as u8)),
                _ => None,
            };
            if let Some(k) = key {
                let sequence = with_state(|s| s.terminal.key_sequence(k).to_vec()).unwrap_or_default();
                send_input(sequence);
                return 0;
            }
            DefWindowProcW(hwnd, msg, wparam, lparam)
        }
        WM_GETDLGCODE => (DLGC_WANTALLKEYS | DLGC_WANTCHARS | DLGC_WANTARROWS | DLGC_WANTTAB) as LRESULT,
        _ => DefWindowProcW(hwnd, msg, wparam, lparam),
    }
}

fn cell_at(lparam: LPARAM) -> (usize, usize) {
    let (x, y) = ((lparam & 0xFFFF) as u16 as i16 as i32, ((lparam >> 16) & 0xFFFF) as u16 as i16 as i32);
    with_state(|s| {
        let first = s.terminal.total_lines() - s.terminal.rows() - s.terminal.view_offset.min(s.terminal.scrollback_len());
        let row = ((y.max(0) / s.cell_h) as usize).min(s.terminal.rows().saturating_sub(1));
        let col = ((x.max(0) / s.cell_w) as usize).min(s.terminal.cols().saturating_sub(1));
        (first + row, col)
    }).unwrap_or((0, 0))
}

unsafe fn copy_selection(hwnd: HWND) {
    let Some(text) = with_state(|s| s.selection.map(|(a, b)| s.terminal.selection_text(a, b))).flatten() else { return };
    let data: Vec<u16> = text.replace('\n', "\r\n").encode_utf16().chain(std::iter::once(0)).collect();
    if OpenClipboard(hwnd) == 0 { return; }
    EmptyClipboard();
    let mem = GlobalAlloc(GMEM_MOVEABLE, data.len() * 2);
    if !mem.is_null() {
        let p = GlobalLock(mem) as *mut u16;
        if !p.is_null() {
            std::ptr::copy_nonoverlapping(data.as_ptr(), p, data.len());
            GlobalUnlock(mem);
            if SetClipboardData(CF_UNICODETEXT as u32, mem as HANDLE).is_null() { GlobalFree(mem); }
        } else {
            GlobalFree(mem);
        }
    }
    CloseClipboard();
}

unsafe fn paste(hwnd: HWND) {
    if OpenClipboard(hwnd) == 0 { return; }
    let handle = GetClipboardData(CF_UNICODETEXT as u32);
    let mut text = String::new();
    if !handle.is_null() {
        let p = GlobalLock(handle as HGLOBAL) as *const u16;
        if !p.is_null() {
            let mut n = 0;
            while *p.add(n) != 0 { n += 1; }
            text = String::from_utf16_lossy(std::slice::from_raw_parts(p, n));
            GlobalUnlock(handle as HGLOBAL);
        }
    }
    CloseClipboard();
    if !text.is_empty() {
        // Bracketed paste when the application asked for it; CRLF → CR like a typed Enter.
        let bytes = with_state(|s| s.terminal.paste_bytes(&text.replace("\r\n", "\r"))).unwrap_or_default();
        send_input(bytes);
    }
}

fn memory() -> (usize, usize) {
    unsafe {
        let mut counters: PROCESS_MEMORY_COUNTERS = std::mem::zeroed();
        counters.cb = std::mem::size_of::<PROCESS_MEMORY_COUNTERS>() as u32;
        K32GetProcessMemoryInfo(GetCurrentProcess(), &mut counters, counters.cb);
        (counters.WorkingSetSize, counters.PagefileUsage)
    }
}

fn filetime_now() -> u64 {
    let mut ft: FILETIME = unsafe { std::mem::zeroed() };
    unsafe { GetSystemTimePreciseAsFileTime(&mut ft); }
    ((ft.dwHighDateTime as u64) << 32) | ft.dwLowDateTime as u64
}

fn process_created() -> u64 {
    let (mut c, mut e, mut k, mut u): (FILETIME, FILETIME, FILETIME, FILETIME) = unsafe { std::mem::zeroed() };
    unsafe { GetProcessTimes(GetCurrentProcess(), &mut c, &mut e, &mut k, &mut u); }
    ((c.dwHighDateTime as u64) << 32) | c.dwLowDateTime as u64
}

fn synthetic_entries(count: u64) -> Vec<Entry> {
    (0..count).map(|i| Entry {
        name: if i % 50 == 0 { format!("папка {}", i / 50) } else { format!("file{}.{}", (i * 7919) % count, ["txt", "log", "json", "sh", "png", "tar.gz"][i as usize % 6]) },
        is_dir: i % 50 == 0,
        size: if i % 50 == 0 || i % 13 == 0 { None } else { Some((i * 104_729) % 10_000_000) },
        modified: Some(1_700_000_000 + i * 37),
        permissions: Some(if i % 50 == 0 { 0o755 } else { 0o644 }),
    }).collect()
}

/// Synthetic terminal content for the render capture; clearly labelled, no SSH involved.
const DEMO: &str = "\x1b[90m# self-test: демонстрационный вывод, без SSH\x1b[0m\r\n\
\x1b[1;32muser@prototype\x1b[0m:\x1b[1;34m~/project\x1b[0m$ ls --color\r\n\
\x1b[1;34mdocs\x1b[0m  \x1b[1;32mbuild.sh\x1b[0m  README.md  отчёт-2026.txt  图表.png  \x1b[4mlinks.md\x1b[0m\r\n\
\x1b[1;32muser@prototype\x1b[0m:\x1b[1;34m~/project\x1b[0m$ systemctl status nginx --no-pager | head -3\r\n\
\x1b[1;32m●\x1b[0m nginx.service - A high performance web server\r\n\
     Loaded: loaded (/lib/systemd/system/nginx.service; \x1b[1;32menabled\x1b[0m)\r\n\
     Active: \x1b[1;32mactive (running)\x1b[0m since Wed 2026-11-04 09:12:03 UTC\r\n\
┌──────────┬────────┐\r\n│ 256 цветов │ RGB    │\r\n└──────────┴────────┘\r\n";

unsafe fn selftest_step(hwnd: HWND) {
    let Some(u) = ui() else { return };
    let stage = with_state(|s| { s.selftest_stage += 1; s.selftest_stage }).unwrap_or(0);
    let report_path = with_state(|s| s.selftest.clone()).flatten().unwrap_or_else(|| PathBuf::from("selftest.json"));
    match stage {
        1 => {
            let (ws, private) = memory();
            with_state(|s| { s.report.idle_ws = ws; s.report.idle_private = private; s.connected = true; s.current_path = "/".into(); });
            let entries = synthetic_entries(100_000);
            let seq = with_state(|s| { s.nav_seq += 1; s.nav_seq }).unwrap_or(0);
            let start = Instant::now();
            on_result(WorkResult::Listed { seq, path: "/self-test (100 000 синтетических строк)".into(), target: Target::Files, entries: Ok(entries) });
            UpdateWindow(u.list);
            let elapsed = start.elapsed().as_secs_f64() * 1000.0;
            let (ws_after, _) = memory();
            with_state(|s| { s.report.list_populate_ms = elapsed; s.report.after_list_ws = ws_after; s.connected = false; });
            SetTimer(hwnd, ID_TIMER_SELFTEST, 400, None);
        }
        2 => {
            let files = capture_to(hwnd, &report_path, "files.bmp");
            let (foreground, sent) = click_header(u, 1);
            with_state(|s| { s.report.files_capture = files; s.report.header_foreground = foreground; s.report.header_sent = sent; });
            SetTimer(hwnd, ID_TIMER_SELFTEST, 500, None);
        }
        3 => {
            with_state(|s| s.report.header_sorted = s.sort_column == Column::Size && !s.sort_desc);
            SendMessageW(u.tabs, TCM_SETCURSEL, 1, 0);
            layout(hwnd);
            fit_terminal();
            with_state(|s| s.terminal.feed(DEMO.as_bytes()));
            for i in 0..24u8 { let line = format!("\x1b[38;5;{}m██\x1b[0m", 232 + i); with_state(|s| s.terminal.feed(line.as_bytes())); }
            with_state(|s| s.terminal.feed(b"\r\n\x1b[1;32muser@prototype\x1b[0m:\x1b[1;34m~/project\x1b[0m$ "));
            InvalidateRect(u.term, std::ptr::null(), 0);
            SetTimer(hwnd, ID_TIMER_SELFTEST, 400, None);
        }
        _ => {
            let terminal_capture = capture_to(hwnd, &report_path, "terminal.bmp");
            with_state(|s| s.report.terminal_capture = terminal_capture);
            let json = selftest_report(u);
            if std::fs::write(&report_path, json).is_err() { SELFTEST_FAILED.store(true, Ordering::Relaxed); }
            PostMessageW(hwnd, WM_CLOSE, 0, 0);
        }
    }
}

unsafe fn capture_to(hwnd: HWND, report: &Path, suffix: &str) -> String {
    let path = report.with_extension(suffix);
    match capture::window_bmp(hwnd, &path) { Ok((w, h)) => format!("{} ({w}x{h})", path.file_name().map(|n| n.to_string_lossy().to_string()).unwrap_or_default()), Err(e) => format!("error: {e}") }
}

/// Software OS input (SendInput) on this process's own window: the click travels through the
/// header control and the ListView to LVN_COLUMNCLICK. Not physical-mouse coverage.
unsafe fn click_header(u: Ui, column: usize) -> (bool, bool) {
    let header = SendMessageW(u.list, LVM_GETHEADER, 0, 0) as HWND;
    let mut rc: RECT = std::mem::zeroed();
    if SendMessageW(header, HDM_GETITEMRECT, column, &mut rc as *mut RECT as LPARAM) == 0 { return (false, false); }
    let mut pt = POINT { x: (rc.left + rc.right) / 2, y: (rc.top + rc.bottom) / 2 };
    ClientToScreen(header, &mut pt);
    let foreground = SetForegroundWindow(u.hwnd) != 0;
    SetCursorPos(pt.x, pt.y);
    let mut inputs: [INPUT; 2] = std::mem::zeroed();
    inputs[0].r#type = INPUT_MOUSE;
    inputs[0].Anonymous.mi.dwFlags = MOUSEEVENTF_LEFTDOWN;
    inputs[1].r#type = INPUT_MOUSE;
    inputs[1].Anonymous.mi.dwFlags = MOUSEEVENTF_LEFTUP;
    let sent = SendInput(2, inputs.as_ptr(), std::mem::size_of::<INPUT>() as i32) == 2;
    (foreground, sent)
}

unsafe fn selftest_report(u: Ui) -> String {
    // Terminal parse throughput on realistic coloured log lines with Cyrillic text.
    let mut term = Terminal::new(160, 48);
    let line = "\x1b[32mINFO\x1b[0m 2026-11-04 12:00:00 \x1b[1;34mservice\x1b[0m started on port 8080 — запуск службы\r\n".as_bytes();
    let target = 8 * 1024 * 1024;
    let (mut fed, start) = (0usize, Instant::now());
    while fed < target { term.feed(line); fed += line.len(); }
    let parse_mbps = fed as f64 / start.elapsed().as_secs_f64() / 1e6;
    // Full-frame GDI render time into a memory DC.
    let (cell_w, cell_h) = with_state(|s| (s.cell_w, s.cell_h)).unwrap_or((8, 16));
    let frame_ms = {
        let dc = GetDC(u.hwnd);
        let mem = CreateCompatibleDC(dc);
        let (w, h) = (160 * cell_w, 48 * cell_h);
        let bmp = CreateCompatibleBitmap(dc, w, h);
        let old = SelectObject(mem, bmp);
        let start = Instant::now();
        for _ in 0..100 { paint_terminal(mem, &term, u.mono, cell_w, cell_h, w, h, None, true); }
        let elapsed = start.elapsed();
        SelectObject(mem, old);
        DeleteObject(bmp);
        DeleteDC(mem);
        ReleaseDC(u.hwnd, dc);
        elapsed.as_secs_f64() * 1000.0 / 100.0
    };
    let mut entries = synthetic_entries(100_000);
    let start = Instant::now();
    sorting::sort(&mut entries, Column::Size, true);
    sorting::sort(&mut entries, Column::Name, false);
    let sort_ms = start.elapsed().as_secs_f64() * 1000.0;
    let (ws_after, private_after) = memory();
    let exe = std::env::current_exe().ok().and_then(|p| std::fs::metadata(p).ok()).map(|m| m.len()).unwrap_or(0);
    let comctl = SendMessageW(u.list, CCM_GETVERSION, 0, 0);
    let created = process_created();
    let s = STATE.with(|s| s.try_borrow().ok().and_then(|g| g.as_ref().map(|s| (
        s.first_paint, s.dpi, s.mono_face.clone(), s.drop_registered, s.icon_from_resource,
        s.report.idle_ws, s.report.idle_private, s.report.list_populate_ms, s.report.after_list_ws,
        s.report.header_foreground, s.report.header_sent, s.report.header_sorted, s.report.files_capture.clone(), s.report.terminal_capture.clone()))));
    let Some((first_paint, dpi, face, drop_ok, icon_ok, idle_ws, idle_private, populate_ms, after_list_ws, fg, sent, sorted, files_cap, term_cap)) = s else { return "{}".into() };
    let (main_to_paint, process_to_paint) = match first_paint {
        Some((d, at)) => (d.as_secs_f64() * 1000.0, at.saturating_sub(created) as f64 / 10_000.0),
        None => (-1.0, -1.0),
    };
    format!(concat!(
        "{{\n",
        "  \"scope\": \"Rust prototype window on a CI runner; synthetic data, no SSH in this run\",\n",
        "  \"exeBytes\": {},\n  \"dpi\": {},\n  \"monoFont\": {},\n  \"commonControlsVersion\": {},\n",
        "  \"windowIconFromResource\": {},\n  \"dropTargetRegistered\": {},\n  \"reentrancyFaults\": {},\n",
        "  \"processStartToFirstPaintMs\": {:.1},\n  \"mainToFirstPaintMs\": {:.1},\n",
        "  \"idleWorkingSetBytes\": {},\n  \"idlePrivateBytes\": {},\n",
        "  \"list100kPopulateMs\": {:.1},\n  \"afterListWorkingSetBytes\": {},\n",
        "  \"headerClick\": {{ \"route\": \"SendInput\", \"foreground\": {}, \"sent\": {}, \"sortedBySizeAscending\": {} }},\n",
        "  \"terminalParseMBps\": {:.1},\n  \"terminalFrame160x48Ms\": {:.2},\n  \"sort100kTwoKeysMs\": {:.1},\n",
        "  \"afterBenchWorkingSetBytes\": {},\n  \"afterBenchPrivateBytes\": {},\n",
        "  \"captures\": {{ \"files\": {}, \"terminal\": {} }}\n",
        "}}\n"),
        exe, dpi, json_str(&face), comctl, icon_ok, drop_ok, REENTRY_FAULTS.load(Ordering::Relaxed),
        process_to_paint, main_to_paint, idle_ws, idle_private, populate_ms, after_list_ws,
        fg, sent, sorted, parse_mbps, frame_ms, sort_ms, ws_after, private_after, json_str(&files_cap), json_str(&term_cap))
}
