using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace SSHExplorer.Core;

/// <summary>
/// PuTTY's Pageant over its classic Windows protocol: the request is put into a named shared memory block owned by
/// this user, Pageant is asked by WM_COPYDATA to answer into the same block. The agent messages themselves are the
/// usual SSH agent protocol, so <see cref="SshAgentClient"/> works on top. Keys never leave Pageant.
/// </summary>
public static class Pageant
{
    const uint CopyDataId = 0x804e50ba, WM_COPYDATA = 0x004A, PAGE_READWRITE = 4, FILE_MAP_WRITE = 2;
    public const int MaxMessage = 256 * 1024;

    public static bool IsRunning => OperatingSystem.IsWindows() && FindWindow("Pageant", "Pageant") != IntPtr.Zero;

    public static SshAgentClient Client() => new(() => new RequestStream(), "Pageant (PuTTY)");

    /// <summary>One request and its reply through Pageant's shared memory.</summary>
    internal static byte[] Exchange(byte[] frame)
    {
        if (!OperatingSystem.IsWindows()) throw new IOException("Pageant работает только в Windows.");
        if (frame.Length > MaxMessage) throw new IOException("Запрос к Pageant слишком велик.");
        var window = FindWindow("Pageant", "Pageant");
        if (window == IntPtr.Zero) throw new IOException("Pageant не запущен.");
        var name = "PageantRequest" + GetCurrentThreadId().ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
        var mapping = CreateFileMapping(new IntPtr(-1), IntPtr.Zero, PAGE_READWRITE, 0, MaxMessage, name);
        if (mapping == IntPtr.Zero) throw new IOException("Не удалось создать общую память для Pageant (код " + Marshal.GetLastWin32Error() + ").");
        try
        {
            var view = MapViewOfFile(mapping, FILE_MAP_WRITE, 0, 0, IntPtr.Zero);
            if (view == IntPtr.Zero) throw new IOException("Не удалось открыть общую память для Pageant.");
            try
            {
                Marshal.Copy(frame, 0, view, frame.Length);
                var nameBytes = Encoding.ASCII.GetBytes(name + "\0");
                var handle = GCHandle.Alloc(nameBytes, GCHandleType.Pinned);
                try
                {
                    var data = new CopyData { dwData = new IntPtr(unchecked((int)CopyDataId)), cbData = nameBytes.Length, lpData = handle.AddrOfPinnedObject() };
                    if (SendMessage(window, WM_COPYDATA, IntPtr.Zero, ref data) == IntPtr.Zero) throw new IOException("Pageant отклонил запрос.");
                }
                finally { handle.Free(); }
                var header = new byte[4]; Marshal.Copy(view, header, 0, 4);
                var length = BinaryPrimitives.ReadUInt32BigEndian(header);
                if (length == 0 || length > MaxMessage - 4) throw new IOException("Pageant вернул ответ недопустимой длины.");
                var reply = new byte[4 + length]; Marshal.Copy(view, reply, 0, reply.Length);
                return reply;
            }
            finally { UnmapViewOfFile(view); }
        }
        finally { CloseHandle(mapping); }
    }

    /// <summary>Collects one framed request; the first read sends it and returns Pageant's framed reply.</summary>
    sealed class RequestStream : Stream
    {
        readonly MemoryStream request = new(); MemoryStream? reply;
        public RequestStream() { if (!IsRunning) throw new IOException("Pageant не запущен."); }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) { if (reply is not null) throw new IOException("Один запрос на соединение."); request.Write(buffer, offset, count); }
        public override int Read(byte[] buffer, int offset, int count) { reply ??= new MemoryStream(Exchange(request.ToArray())); return reply.Read(buffer, offset, count); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    [StructLayout(LayoutKind.Sequential)] struct CopyData { public IntPtr dwData; public int cbData; public IntPtr lpData; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string className, string windowName);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, ref CopyData data);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)] static extern IntPtr CreateFileMapping(IntPtr file, IntPtr attributes, uint protect, uint sizeHigh, int sizeLow, string name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, IntPtr size);
    [DllImport("kernel32.dll")] static extern bool UnmapViewOfFile(IntPtr view);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
}
