using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace SSHExplorer.App.NativeDragDrop;

[ComVisible(true), Guid("3D8B0590-F691-11D2-8EA9-006097DF5BD4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDataObjectAsyncCapability
{
    [PreserveSig] int SetAsyncMode([MarshalAs(UnmanagedType.Bool)] bool value);
    [PreserveSig] int GetAsyncMode([MarshalAs(UnmanagedType.Bool)] out bool value);
    [PreserveSig] int StartOperation(IntPtr reserved);
    [PreserveSig] int InOperation([MarshalAs(UnmanagedType.Bool)] out bool value);
    [PreserveSig] int EndOperation(int result, IntPtr reserved, uint effects);
}

[ComVisible(true), Guid("00000121-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDropSource
{
    [PreserveSig] int QueryContinueDrag([MarshalAs(UnmanagedType.Bool)] bool escape, uint keyState);
    [PreserveSig] int GiveFeedback(uint effect);
}

internal static class ShellInterop
{
    internal const int FormatError = unchecked((int)0x80040064), MediumError = unchecked((int)0x80040069), IndexError = unchecked((int)0x80040068);
    internal const int NotImplemented = unchecked((int)0x80004001), UnsupportedAdvice = unchecked((int)0x80040003);
    internal const int Abort = unchecked((int)0x80004004), ReadFault = unchecked((int)0x8003001E), WrongThread = unchecked((int)0x8001010E);
    internal static readonly short Descriptor = Format("FileGroupDescriptorW"), Contents = Format("FileContents"), Preferred = Format("Preferred DropEffect"), Performed = Format("Performed DropEffect"), LogicalPerformed = Format("Logical Performed DropEffect"), PasteSucceeded = Format("Paste Succeeded"), InDragLoop = Format("InShellDragLoop"), Token = Format("SSHExplorer.RemoteFiles");
    internal static readonly Guid MarshalId = new("00000003-0000-0000-C000-000000000046");
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterClipboardFormat(string name);
    internal static short Format(string name) => unchecked((short)RegisterClipboardFormat(name));
    [DllImport("ole32.dll")] internal static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll")] internal static extern void CoUninitialize();
    [DllImport("ole32.dll")] internal static extern int CoCreateFreeThreadedMarshaler(IntPtr outer, out IntPtr marshaler);
    [DllImport("ole32.dll")] internal static extern int DoDragDrop([MarshalAs(UnmanagedType.Interface)] System.Runtime.InteropServices.ComTypes.IDataObject data, IDropSource source, uint allowed, out uint effect);
    [DllImport("ole32.dll")] internal static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, UIntPtr size);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr memory);
    [DllImport("kernel32.dll")] internal static extern UIntPtr GlobalSize(IntPtr memory);
    internal static STGMEDIUM Global(byte[] bytes)
    {
        var memory = GlobalAlloc(0x42, (UIntPtr)Math.Max(1, bytes.Length));
        if (memory == IntPtr.Zero) throw new OutOfMemoryException();
        var target = GlobalLock(memory);
        if (target == IntPtr.Zero) { GlobalFree(memory); throw new OutOfMemoryException(); }
        try { Marshal.Copy(bytes, 0, target, bytes.Length); }
        finally { GlobalUnlock(memory); }
        return new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = memory, pUnkForRelease = null };
    }
    internal static byte[] GlobalBytes(IntPtr memory)
    {
        var length = checked((int)GlobalSize(memory).ToUInt64()); var p = GlobalLock(memory);
        try { var b = new byte[length]; Marshal.Copy(p, b, 0, length); return b; }
        finally { GlobalUnlock(memory); }
    }
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class FormatEnumerator(FORMATETC[] formats, int position = 0) : IEnumFORMATETC
{
    private int index = position;
    public int Next(int count, FORMATETC[] output, int[]? fetched)
    { var n = 0; while (n < count && index < formats.Length) output[n++] = formats[index++]; if (fetched is { Length: > 0 }) fetched[0] = n; return n == count ? 0 : 1; }
    public int Skip(int count) { var available = formats.Length - index; index = Math.Min(formats.Length, index + count); return available >= count ? 0 : 1; }
    public int Reset() { index = 0; return 0; }
    public void Clone(out IEnumFORMATETC clone) => clone = new FormatEnumerator(formats, index);
}
