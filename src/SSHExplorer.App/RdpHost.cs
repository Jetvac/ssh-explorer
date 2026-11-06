using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32;
using SSHExplorer.Core;
using Forms = System.Windows.Forms;

namespace SSHExplorer.App;

/// <summary>
/// The Windows remote desktop ActiveX control (mstscax.dll, present in every Windows 11), hosted by WinForms. It is the
/// control mstsc and mRemoteNG use. Everything is late bound through IDispatch, so no interop assembly is shipped; the
/// events arrive through <see cref="RdpEventSink"/>.
/// </summary>
internal sealed class RdpAxHost(string clsid) : Forms.AxHost(clsid)
{
    public object? Ocx => GetOcx();

    // MsRdpClient9/10/8/7 NotSafeForScripting, newest first; the version-independent ProgID is the last resort.
    static readonly string[] Known = ["A0C63C30-F08D-4AB4-907C-34905D770C7D", "8B918B82-7985-4C24-89DF-C33AD2BBFBCD", "5F681803-2900-4C43-A1CC-CF405404A676", "A9D7038D-B5ED-472E-9C47-94BEA90A5910", "54D38BF7-B1EF-4479-9674-1BD6EA465258"];

    /// <summary>The first registered control class, or null when remote desktop is unavailable on this computer.</summary>
    public static string? FindClass()
    {
        foreach (var clsid in Known)
            if (Registered(clsid)) return clsid;
        try
        {
            using var prog = Registry.ClassesRoot.OpenSubKey(@"MsTscAx.MsTscAx\CLSID");
            if (prog?.GetValue(null) is string id && Registered(id.Trim('{', '}'))) return id.Trim('{', '}');
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException) { }
        return null;
    }

    static bool Registered(string clsid)
    {
        try { using var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{{{clsid}}}\InprocServer32"); return key is not null; }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException) { return false; }
    }

    // Tab, arrows and Escape belong to the remote desktop while it has focus.
    protected override bool IsInputKey(Forms.Keys keyData) => true;
}

/// <summary>Late-bound property and method access on the control and its settings objects (IDispatch).</summary>
internal static class Com
{
    public static object? Get(object target, string name) => target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);
    public static void Set(object target, string name, object value) => target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, [value]);
    public static object? Call(object target, string name, params object[] args) => target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args);

    /// <summary>
    /// Sets a property on the first settings object that has it, newest interface first ("" is the control itself).
    /// Returns false when none accepts it, which older Windows builds may do for newer options.
    /// </summary>
    public static bool SetFirst(object ocx, string name, object value, params string[] owners)
    {
        foreach (var owner in owners)
        {
            try
            {
                var target = owner.Length == 0 ? ocx : Get(ocx, owner);
                if (target is null) continue;
                Set(target, name, value); return true;
            }
            catch (Exception ex) when (ex is COMException or MissingMethodException or TargetInvocationException or InvalidCastException or ArgumentException or NotSupportedException) { }
        }
        return false;
    }

    public static readonly string[] Advanced = ["AdvancedSettings9", "AdvancedSettings8", "AdvancedSettings7", "AdvancedSettings6", "AdvancedSettings5", "AdvancedSettings4", "AdvancedSettings3", "AdvancedSettings2"];
}

/// <summary>IDispatch layout (IUnknown + 4 methods); a distinct IID so the runtime builds a plain vtable for it.</summary>
[ComVisible(true), Guid("6F3C9B1E-2A47-4C1D-9E58-3B7A0D2C4E91"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IRdpEventDispatch
{
    [PreserveSig] int GetTypeInfoCount(out uint count);
    [PreserveSig] int GetTypeInfo(uint index, uint lcid, out IntPtr info);
    [PreserveSig] int GetIDsOfNames(ref Guid riid, IntPtr names, uint count, uint lcid, IntPtr ids);
    [PreserveSig] int Invoke(int dispId, ref Guid riid, uint lcid, ushort flags, IntPtr dispParams, IntPtr result, IntPtr excepInfo, IntPtr argErr);
}

/// <summary>
/// Receives IMsTscAxEvents (a dispinterface) as raw IDispatch::Invoke calls, so arguments are read by position and
/// type from DISPPARAMS without a type library. Exceptions never cross into the control.
/// </summary>
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class RdpEventSink : IRdpEventDispatch, ICustomQueryInterface
{
    public static readonly Guid EventsIid = new("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6");
    static readonly Guid DispatchIid = new("00020400-0000-0000-C000-000000000046");
    internal Action<RdpEvent>? Handler { get; set; }

    public CustomQueryInterfaceResult GetInterface(ref Guid iid, out IntPtr ppv)
    {
        if (iid == EventsIid || iid == DispatchIid)
        {
            ppv = Marshal.GetComInterfaceForObject(this, typeof(IRdpEventDispatch), CustomQueryInterfaceMode.Ignore);
            return CustomQueryInterfaceResult.Handled;
        }
        ppv = IntPtr.Zero; return CustomQueryInterfaceResult.NotHandled;
    }

    public int GetTypeInfoCount(out uint count) { count = 0; return 0; }
    public int GetTypeInfo(uint index, uint lcid, out IntPtr info) { info = IntPtr.Zero; return unchecked((int)0x80004001); }
    public int GetIDsOfNames(ref Guid riid, IntPtr names, uint count, uint lcid, IntPtr ids) => unchecked((int)0x80004001);

    public int Invoke(int dispId, ref Guid riid, uint lcid, ushort flags, IntPtr dispParams, IntPtr result, IntPtr excepInfo, IntPtr argErr)
    {
        try { if (dispParams != IntPtr.Zero) Handler?.Invoke(new RdpEvent(dispId, Marshal.PtrToStructure<DispParams>(dispParams), result)); }
        catch (Exception ex) { _ = LocalDiagnostics.RecordAsync("rdp.event.failed", ex); }
        return 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DispParams { public IntPtr Args; public IntPtr NamedArgs; public int Count; public int NamedCount; }
}

/// <summary>One event: its DISPID and its arguments in declaration order.</summary>
internal readonly struct RdpEvent(int id, RdpEventSink.DispParams args, IntPtr result)
{
    // IMsTscAxEvents DISPIDs.
    public const int Connecting = 1, Connected = 2, LoginComplete = 3, Disconnected = 4, EnterFullScreen = 5, LeaveFullScreen = 6,
        RequestLeaveFullScreen = 9, FatalError = 10, Warning = 11, RemoteDesktopSizeChange = 12, ReceivedPublicKey = 16, AutoReconnecting = 17, LogonError = 22;
    const ushort VtBool = 11, VtBstr = 8, VtByRef = 0x4000;
    static int VariantSize => IntPtr.Size == 8 ? 24 : 16;
    public int Id => id;
    public int Count => args.Count;

    IntPtr Arg(int index) => index < 0 || index >= args.Count ? IntPtr.Zero : args.Args + (args.Count - 1 - index) * VariantSize;

    public int Int(int index)
    {
        var p = Arg(index); if (p == IntPtr.Zero) return 0;
        try { return Convert.ToInt32(Marshal.GetObjectForNativeVariant(p), System.Globalization.CultureInfo.InvariantCulture); }
        catch (Exception ex) when (ex is InvalidCastException or OverflowException or FormatException or ArgumentException or NotSupportedException) { return 0; }
    }

    /// <summary>The raw bytes of the first BSTR argument (the server key is binary data packed into a BSTR).</summary>
    public byte[]? FirstBstrBytes()
    {
        for (int i = 0; i < args.Count; i++)
        {
            var p = Arg(i); var vt = (ushort)Marshal.ReadInt16(p);
            if ((vt & 0xFFF) != VtBstr) continue;
            var bstr = Marshal.ReadIntPtr(p, 8);
            if ((vt & VtByRef) != 0) bstr = bstr == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(bstr);
            if (bstr == IntPtr.Zero) return [];
            int length = (int)SysStringByteLen(bstr); var bytes = new byte[length];
            Marshal.Copy(bstr, bytes, 0, length); return bytes;
        }
        return null;
    }

    /// <summary>Answers an <c>[out, retval] VARIANT_BOOL*</c>: through a by-reference argument, or the result.</summary>
    public void SetBool(bool value)
    {
        short v = value ? (short)-1 : (short)0;
        for (int i = 0; i < args.Count; i++)
        {
            var p = Arg(i); var vt = (ushort)Marshal.ReadInt16(p);
            if (vt == (VtBool | VtByRef)) { var target = Marshal.ReadIntPtr(p, 8); if (target != IntPtr.Zero) { Marshal.WriteInt16(target, v); return; } }
        }
        if (result != IntPtr.Zero) { Marshal.WriteInt16(result, 0, unchecked((short)VtBool)); Marshal.WriteInt16(result, 8, v); }
    }

    [DllImport("oleaut32.dll")] static extern uint SysStringByteLen(IntPtr bstr);
}

/// <summary>IMsTscNonScriptable / IMsRdpClientNonScriptable (vtable interfaces): the password and Ctrl+Alt+Del.</summary>
[ComImport, Guid("2F079C4C-87B2-4AFD-97AB-20CDB43038AE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMsRdpClientNonScriptable
{
    void SetClearTextPassword([MarshalAs(UnmanagedType.BStr)] string value);
    void SetPortablePassword([MarshalAs(UnmanagedType.BStr)] string value);
    [return: MarshalAs(UnmanagedType.BStr)] string GetPortablePassword();
    void SetPortableSalt([MarshalAs(UnmanagedType.BStr)] string value);
    [return: MarshalAs(UnmanagedType.BStr)] string GetPortableSalt();
    void SetBinaryPassword([MarshalAs(UnmanagedType.BStr)] string value);
    [return: MarshalAs(UnmanagedType.BStr)] string GetBinaryPassword();
    void SetBinarySalt([MarshalAs(UnmanagedType.BStr)] string value);
    [return: MarshalAs(UnmanagedType.BStr)] string GetBinarySalt();
    void ResetPassword();
    void NotifyRedirectDeviceChange(UIntPtr wParam, IntPtr lParam);
    void SendKeys(int count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] short[] keyUp, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] int[] keyData);
}

/// <summary>
/// One remote desktop control: configure, connect, resize, full screen, disconnect. Runs on the UI thread; events are
/// raised there too.
/// </summary>
internal sealed class RdpClient : IDisposable
{
    readonly RdpAxHost host;
    readonly RdpEventSink sink = new();
    IConnectionPoint? point; int cookie; bool disposed;
    public Forms.Control Control => host;
    public event Action<RdpEvent>? Event;

    public RdpClient(string clsid)
    {
        host = new RdpAxHost(clsid) { Dock = Forms.DockStyle.Fill };
        sink.Handler = e => Event?.Invoke(e);
        host.HandleCreated += (_, _) => Attach();
    }

    public object? Ocx => disposed ? null : host.Ocx;
    public bool Ready => Ocx is not null && point is not null;

    void Attach()
    {
        if (point is not null || host.Ocx is not { } ocx) return;
        try
        {
            var container = (IConnectionPointContainer)ocx; var iid = RdpEventSink.EventsIid;
            container.FindConnectionPoint(ref iid, out var cp); cp.Advise(sink, out cookie); point = cp;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException) { _ = LocalDiagnostics.RecordAsync("rdp.events.attach.failed", ex); }
    }

    /// <summary>True when the control exists (its window was created) and the events are attached.</summary>
    public bool EnsureCreated()
    {
        if (!host.IsHandleCreated && host.Parent is { IsHandleCreated: true }) host.CreateControl();
        Attach();
        return Ready;
    }

    public void SetPassword(string password)
    {
        var ocx = Ocx ?? throw new InvalidOperationException("Элемент удалённого рабочего стола не создан.");
        if (Com.SetFirst(ocx, "ClearTextPassword", password, "AdvancedSettings2", "AdvancedSettings")) return;
        ((IMsRdpClientNonScriptable)ocx).SetClearTextPassword(password);
    }

    /// <summary>
    /// The Windows key's usual job — the Start menu — on the remote computer: Ctrl+Esc (scan codes 1D, 01). Unlike the
    /// extended Win scan code, this works the same on every server version.
    /// </summary>
    public void SendStartMenu()
    {
        var ocx = Ocx ?? throw new InvalidOperationException("Нет подключения.");
        ((IMsRdpClientNonScriptable)ocx).SendKeys(4, [0, 0, -1, -1], [0x1D, 0x01, 0x01, 0x1D]);
    }

    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    /// <summary>
    /// A picture of the remote desktop as shown in the tab (PrintWindow with full content; the screen area as a fallback).
    /// </summary>
    public System.Drawing.Bitmap Snapshot()
    {
        if (!host.IsHandleCreated || host.Width <= 0 || host.Height <= 0) throw new InvalidOperationException("Рабочий стол не показан.");
        var bitmap = new System.Drawing.Bitmap(host.Width, host.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            var hdc = g.GetHdc(); bool ok;
            try { ok = PrintWindow(host.Handle, hdc, 2); } finally { g.ReleaseHdc(hdc); }
            if (!ok) g.CopyFromScreen(host.PointToScreen(System.Drawing.Point.Empty), System.Drawing.Point.Empty, host.Size);
        }
        return bitmap;
    }

    /// <summary>Ctrl+Alt+Del to the remote computer (scan codes Ctrl 1D, Alt 38, Del 53).</summary>
    public void SendCtrlAltDel()
    {
        var ocx = Ocx ?? throw new InvalidOperationException("Нет подключения.");
        ((IMsRdpClientNonScriptable)ocx).SendKeys(6, [0, 0, 0, -1, -1, -1], [0x1D, 0x38, 0x53, 0x53, 0x38, 0x1D]);
    }

    public void Dispose()
    {
        if (disposed) return; disposed = true;
        try { if (point is not null) point.Unadvise(cookie); } catch (COMException) { }
        point = null; sink.Handler = null;
        try { host.Dispose(); } catch (Exception ex) when (ex is COMException or InvalidOperationException or InvalidComObjectException) { }
    }
}
