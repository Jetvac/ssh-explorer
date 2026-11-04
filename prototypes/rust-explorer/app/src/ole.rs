//! Minimal OLE IDropTarget (hand-written COM vtable, no `windows` crate): accepts CF_HDROP from
//! Explorer on the file list and hands the dropped paths to `crate::upload_dropped`.
//! OLE calls these methods on the UI thread that called RegisterDragDrop (STA).

use std::ffi::c_void;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU32, Ordering};
use windows_sys::core::{GUID, HRESULT};
use windows_sys::Win32::Foundation::{E_NOINTERFACE, E_POINTER, POINTL, S_OK};
use windows_sys::Win32::System::Com::{DVASPECT_CONTENT, FORMATETC, STGMEDIUM, TYMED_HGLOBAL};
use windows_sys::Win32::System::Ole::{ReleaseStgMedium, CF_HDROP, DROPEFFECT_COPY, DROPEFFECT_NONE};
use windows_sys::Win32::UI::Shell::DragQueryFileW;

const IID_IUNKNOWN: GUID = GUID::from_u128(0x00000000_0000_0000_c000_000000000046);
const IID_IDROPTARGET: GUID = GUID::from_u128(0x00000122_0000_0000_c000_000000000046);

/// The part of IDataObject's vtable this target uses (IUnknown + GetData + GetDataHere + QueryGetData).
#[repr(C)]
#[allow(dead_code)]
struct DataObjectVtbl {
    query_interface: unsafe extern "system" fn(*mut c_void, *const GUID, *mut *mut c_void) -> HRESULT,
    add_ref: unsafe extern "system" fn(*mut c_void) -> u32,
    release: unsafe extern "system" fn(*mut c_void) -> u32,
    get_data: unsafe extern "system" fn(*mut c_void, *const FORMATETC, *mut STGMEDIUM) -> HRESULT,
    get_data_here: unsafe extern "system" fn(*mut c_void, *const FORMATETC, *mut STGMEDIUM) -> HRESULT,
    query_get_data: unsafe extern "system" fn(*mut c_void, *const FORMATETC) -> HRESULT,
}

#[repr(C)]
struct DropTargetVtbl {
    query_interface: unsafe extern "system" fn(*mut DropTarget, *const GUID, *mut *mut c_void) -> HRESULT,
    add_ref: unsafe extern "system" fn(*mut DropTarget) -> u32,
    release: unsafe extern "system" fn(*mut DropTarget) -> u32,
    drag_enter: unsafe extern "system" fn(*mut DropTarget, *mut c_void, u32, POINTL, *mut u32) -> HRESULT,
    drag_over: unsafe extern "system" fn(*mut DropTarget, u32, POINTL, *mut u32) -> HRESULT,
    drag_leave: unsafe extern "system" fn(*mut DropTarget) -> HRESULT,
    drop: unsafe extern "system" fn(*mut DropTarget, *mut c_void, u32, POINTL, *mut u32) -> HRESULT,
}

#[repr(C)]
pub struct DropTarget { vtbl: *const DropTargetVtbl, refs: AtomicU32, accept: std::cell::Cell<bool> }

static VTBL: DropTargetVtbl = DropTargetVtbl { query_interface, add_ref, release, drag_enter, drag_over, drag_leave, drop: drop_files };

impl DropTarget {
    /// A new COM object with one reference owned by the caller.
    pub fn create() -> *mut c_void {
        Box::into_raw(Box::new(DropTarget { vtbl: &VTBL, refs: AtomicU32::new(1), accept: std::cell::Cell::new(false) })) as *mut c_void
    }
    /// Drops the caller's reference (RegisterDragDrop keeps its own).
    pub unsafe fn release(this: *mut c_void) { release(this as *mut DropTarget); }
}

#[cfg(windows)]
fn path_from_wide(w: &[u16]) -> PathBuf { use std::os::windows::ffi::OsStringExt; PathBuf::from(std::ffi::OsString::from_wide(w)) }
#[cfg(not(windows))]
fn path_from_wide(w: &[u16]) -> PathBuf { PathBuf::from(String::from_utf16_lossy(w)) }

fn hdrop_format() -> FORMATETC {
    FORMATETC { cfFormat: CF_HDROP, ptd: std::ptr::null_mut(), dwAspect: DVASPECT_CONTENT, lindex: -1, tymed: TYMED_HGLOBAL as u32 }
}

unsafe fn data_vtbl(data: *mut c_void) -> &'static DataObjectVtbl { &**(data as *mut *const DataObjectVtbl) }

unsafe extern "system" fn query_interface(this: *mut DropTarget, iid: *const GUID, out: *mut *mut c_void) -> HRESULT {
    if out.is_null() || iid.is_null() { return E_POINTER; }
    let iid = &*iid;
    let eq = |g: &GUID| g.data1 == iid.data1 && g.data2 == iid.data2 && g.data3 == iid.data3 && g.data4 == iid.data4;
    if eq(&IID_IUNKNOWN) || eq(&IID_IDROPTARGET) { add_ref(this); *out = this as *mut c_void; S_OK } else { *out = std::ptr::null_mut(); E_NOINTERFACE }
}

unsafe extern "system" fn add_ref(this: *mut DropTarget) -> u32 { (*this).refs.fetch_add(1, Ordering::AcqRel) + 1 }

unsafe extern "system" fn release(this: *mut DropTarget) -> u32 {
    let left = (*this).refs.fetch_sub(1, Ordering::AcqRel) - 1;
    if left == 0 { drop(Box::from_raw(this)); }
    left
}

fn effect(accept: bool) -> u32 { if accept && crate::can_upload() { DROPEFFECT_COPY } else { DROPEFFECT_NONE } }

unsafe extern "system" fn drag_enter(this: *mut DropTarget, data: *mut c_void, _keys: u32, _pt: POINTL, effect_out: *mut u32) -> HRESULT {
    let format = hdrop_format();
    let accept = !data.is_null() && (data_vtbl(data).query_get_data)(data, &format) == S_OK;
    (*this).accept.set(accept);
    if !effect_out.is_null() { *effect_out = effect(accept); }
    S_OK
}

unsafe extern "system" fn drag_over(this: *mut DropTarget, _keys: u32, _pt: POINTL, effect_out: *mut u32) -> HRESULT {
    if !effect_out.is_null() { *effect_out = effect((*this).accept.get()); }
    S_OK
}

unsafe extern "system" fn drag_leave(this: *mut DropTarget) -> HRESULT { (*this).accept.set(false); S_OK }

unsafe extern "system" fn drop_files(this: *mut DropTarget, data: *mut c_void, _keys: u32, _pt: POINTL, effect_out: *mut u32) -> HRESULT {
    let accepted = effect((*this).accept.get()) == DROPEFFECT_COPY;
    (*this).accept.set(false);
    if !effect_out.is_null() { *effect_out = DROPEFFECT_NONE; }
    if !accepted || data.is_null() { return S_OK; }
    let format = hdrop_format();
    let mut medium: STGMEDIUM = std::mem::zeroed();
    if (data_vtbl(data).get_data)(data, &format, &mut medium) != S_OK { return S_OK; }
    let hdrop = medium.u.hGlobal;
    let count = DragQueryFileW(hdrop, u32::MAX, std::ptr::null_mut(), 0);
    let mut files = Vec::with_capacity(count as usize);
    for i in 0..count {
        let len = DragQueryFileW(hdrop, i, std::ptr::null_mut(), 0);
        let mut buffer = vec![0u16; len as usize + 1];
        let written = DragQueryFileW(hdrop, i, buffer.as_mut_ptr(), buffer.len() as u32);
        files.push(path_from_wide(&buffer[..written as usize]));
    }
    ReleaseStgMedium(&mut medium);
    if !effect_out.is_null() { *effect_out = DROPEFFECT_COPY; }
    crate::upload_dropped(files);
    S_OK
}
