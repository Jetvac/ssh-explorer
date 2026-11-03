using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace SSHExplorer.App.NativeDragDrop;

[ComVisible(true), Guid("0000000C-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface INativeStream
{
    [PreserveSig] int Read(IntPtr buffer, uint count, IntPtr read);
    [PreserveSig] int Write(IntPtr buffer, uint count, IntPtr written);
    [PreserveSig] int Seek(long move, uint origin, IntPtr position);
    [PreserveSig] int SetSize(ulong size);
    [PreserveSig] int CopyTo(INativeStream destination, ulong count, IntPtr read, IntPtr written);
    [PreserveSig] int Commit(uint flags);
    [PreserveSig] int Revert();
    [PreserveSig] int LockRegion(ulong offset, ulong count, uint type);
    [PreserveSig] int UnlockRegion(ulong offset, ulong count, uint type);
    [PreserveSig] int Stat(out STATSTG stat, uint flags);
    [PreserveSig] int Clone(out INativeStream clone);
}

[ComVisible(true), Guid("0C733A30-2A1C-11CE-ADE5-00AA0044773D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface INativeSequentialStream
{
    [PreserveSig] int Read(IntPtr buffer, uint count, IntPtr read);
    [PreserveSig] int Write(IntPtr buffer, uint count, IntPtr written);
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class ShellFileStream : INativeStream, INativeSequentialStream
{
    private readonly ShellVirtualDataObject owner;
    private readonly ShellVirtualDataObject.VirtualEntry entry;
    private readonly int index;
    private readonly object gate = new();
    private Stream? stream;
    private long position;
    private bool closed;
    internal ShellFileStream(ShellVirtualDataObject owner, ShellVirtualDataObject.VirtualEntry entry, int index, long position = 0)
    { this.owner = owner; this.entry = entry; this.index = index; this.position = position; }
    public int Read(IntPtr buffer, uint count, IntPtr read)
    {
        if (read != IntPtr.Zero) Marshal.WriteInt32(read, 0);
        try
        {
            lock (gate)
            {
                if (closed) return ShellInterop.Abort;
                if (count == 0) return 0;
                if (position >= entry.Length) { owner.BytesRead(index, position, 0, entry.Length); return 1; }
                stream ??= owner.OpenStream(entry); stream.Position = position;
                var bytes = new byte[Math.Min(128 * 1024u, count)]; uint total = 0;
                while (total < count && position < entry.Length)
                {
                    var amount = (int)Math.Min(bytes.Length, Math.Min((long)(count - total), entry.Length - position));
                    var got = stream.Read(bytes, 0, amount); if (got == 0) throw new IOException("Источник неожиданно закончился.");
                    Marshal.Copy(bytes, 0, new IntPtr(checked(buffer.ToInt64() + total)), got);
                    position += got; total += (uint)got; owner.BytesRead(index, position, got, entry.Length);
                    if (read != IntPtr.Zero) Marshal.WriteInt32(read, unchecked((int)total));
                }
                return total == count ? 0 : 1;
            }
        }
        catch (Exception error) { owner.Failed(error); return error is OperationCanceledException ? ShellInterop.Abort : ShellInterop.ReadFault; }
    }
    public int Write(IntPtr buffer, uint count, IntPtr written) => unchecked((int)0x80030005);
    public int Seek(long move, uint origin, IntPtr result)
    {
        try
        {
            lock (gate)
            {
                if (closed) return ShellInterop.Abort;
                var target = origin switch { 0 => move, 1 => checked(position + move), 2 => checked(entry.Length + move), _ => -1 };
                if (target < 0 || target > entry.Length) return unchecked((int)0x80030001);
                position = target; if (stream is not null) stream.Position = position;
                if (result != IntPtr.Zero) Marshal.WriteInt64(result, position); return 0;
            }
        }
        catch (Exception e) { owner.Failed(e); return ShellInterop.ReadFault; }
    }
    public int SetSize(ulong size) => unchecked((int)0x80030005);
    public int CopyTo(INativeStream destination, ulong count, IntPtr read, IntPtr written)
    {
        if (read != IntPtr.Zero) Marshal.WriteInt64(read, 0);
        if (written != IntPtr.Zero) Marshal.WriteInt64(written, 0);
        var buffer = Marshal.AllocHGlobal(128 * 1024); var oneRead = Marshal.AllocHGlobal(4); var oneWritten = Marshal.AllocHGlobal(4);
        ulong transferred = 0;
        try
        {
            while (transferred < count)
            {
                var hr = Read(buffer, (uint)Math.Min(128 * 1024UL, count - transferred), oneRead);
                if (hr < 0) return hr;
                var n = unchecked((uint)Marshal.ReadInt32(oneRead)); if (n == 0) break;
                var writeResult = destination.Write(buffer, n, oneWritten); if (writeResult < 0) return writeResult;
                if (unchecked((uint)Marshal.ReadInt32(oneWritten)) != n) return ShellInterop.ReadFault;
                transferred += n;
                if (read != IntPtr.Zero) Marshal.WriteInt64(read, unchecked((long)transferred));
                if (written != IntPtr.Zero) Marshal.WriteInt64(written, unchecked((long)transferred));
            }
            return transferred == count ? 0 : 1;
        }
        finally { Marshal.FreeHGlobal(buffer); Marshal.FreeHGlobal(oneRead); Marshal.FreeHGlobal(oneWritten); }
    }
    public int Commit(uint flags) => 0;
    public int Revert() => ShellInterop.NotImplemented;
    public int LockRegion(ulong offset, ulong count, uint type) => ShellInterop.NotImplemented;
    public int UnlockRegion(ulong offset, ulong count, uint type) => ShellInterop.NotImplemented;
    public int Stat(out STATSTG stat, uint flags)
    { stat = new STATSTG { cbSize = entry.Length, type = 2, grfMode = 0, pwcsName = (flags & 1) == 0 ? entry.Name : null! }; return 0; }
    public int Clone(out INativeStream clone)
    { lock (gate) clone = owner.RegisterClone(new ShellFileStream(owner, entry, index, position)); return 0; }
    internal void Close() { lock (gate) { closed = true; stream?.Dispose(); stream = null; } }
}
