using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SSHExplorer.Core;

namespace SSHExplorer.App;

/// <summary>One background STA, one query per extension/size/environment; never queries a remote/local document path.</summary>
public static class SystemFileIconProvider
{
    readonly record struct Key(string Extension, bool Directory, int Pixels, int Generation);
    static readonly ConcurrentDictionary<Key, Lazy<Task<ImageSource?>>> cache = new();
    static readonly BlockingCollection<Action> queue = new(1024);
    static int generation;
    public static event EventHandler? Invalidated;
    static SystemFileIconProvider()
    {
        var thread = new Thread(() =>
        {
            OleInitialize(IntPtr.Zero);
            try { foreach (var action in queue.GetConsumingEnumerable()) action(); }
            finally { OleUninitialize(); }
        }) { Name = "SSH Explorer Shell icon STA", IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
    }
    public static void Invalidate() { Interlocked.Increment(ref generation); cache.Clear(); Invalidated?.Invoke(null, EventArgs.Empty); }
    public static int CachedKinds => cache.Count;
    static Key MakeKey(RemoteEntry entry, double pixels)
    {
        var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
        if (ext.Length > 32 || ext.Any(c => !char.IsLetterOrDigit(c) && c != '.')) ext = "";
        var requested = pixels <= 16 ? 16 : pixels <= 32 ? 32 : pixels <= 48 ? 48 : 256;
        return new(ext, entry.IsDirectory && !entry.IsSymbolicLink, requested, Volatile.Read(ref generation));
    }
    public static Task<ImageSource?> GetAsync(RemoteEntry entry, double pixels)
    {
        var key = MakeKey(entry, pixels);
        if (cache.Count >= 512 && !cache.ContainsKey(key)) return Task.FromResult<ImageSource?>(null);
        return cache.GetOrAdd(key, static k => new(() =>
        {
            var completion = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!queue.TryAdd(() => { try { completion.TrySetResult(Extract(k)); } catch { completion.TrySetResult(null); } })) completion.TrySetResult(null);
            return completion.Task;
        })).Value;
    }
    static ImageSource? Extract(Key key)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var info = new ShellFileInfo();
        // USEFILEATTRIBUTES keeps extraction independent of filename, contents, network and execution.
        if (SHGetFileInfoW(key.Directory ? "folder" : "document" + key.Extension, key.Directory ? 0x10u : 0x80u, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x4000 | 0x10) == IntPtr.Zero) return null;
        var iid = typeof(IImageList).GUID; IImageList? images = null; IntPtr icon = IntPtr.Zero;
        try
        {
            int list = key.Pixels <= 16 ? 1 : key.Pixels <= 32 ? 0 : key.Pixels <= 48 ? 2 : 4;
            Marshal.ThrowExceptionForHR(SHGetImageList(list, ref iid, out images));
            Marshal.ThrowExceptionForHR(images.GetIcon(info.IconIndex, 1, out icon));
            if (icon == IntPtr.Zero) return null;
            var bitmap = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()); bitmap.Freeze(); return bitmap;
        }
        finally { if (icon != IntPtr.Zero) DestroyIcon(icon); if (images is not null) Marshal.ReleaseComObject(images); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct ShellFileInfo
    { public IntPtr Icon; public int IconIndex; public uint Attributes; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SHGetFileInfoW(string path, uint attributes, ref ShellFileInfo info, uint size, uint flags);
    [DllImport("shell32.dll")] static extern int SHGetImageList(int list, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IImageList images);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    [DllImport("ole32.dll")] static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] static extern void OleUninitialize();
    [ComImport, Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IImageList
    {
        [PreserveSig] int Add(IntPtr image, IntPtr mask, out int index);
        [PreserveSig] int ReplaceIcon(int index, IntPtr icon, out int result);
        [PreserveSig] int SetOverlayImage(int image, int overlay);
        [PreserveSig] int Replace(int index, IntPtr image, IntPtr mask);
        [PreserveSig] int AddMasked(IntPtr image, uint mask, out int index);
        [PreserveSig] int Draw(IntPtr parameters);
        [PreserveSig] int Remove(int index);
        [PreserveSig] int GetIcon(int index, int flags, out IntPtr icon);
    }
}

public sealed class ShellFileIcon : Image
{
    public static readonly DependencyProperty EntryProperty = DependencyProperty.Register(nameof(Entry), typeof(RemoteEntry), typeof(ShellFileIcon), new PropertyMetadata(null, (d, _) => ((ShellFileIcon)d).Refresh()));
    public RemoteEntry? Entry { get => (RemoteEntry?)GetValue(EntryProperty); set => SetValue(EntryProperty, value); }
    long request;
    public ShellFileIcon()
    {
        Stretch = Stretch.Uniform; SnapsToDevicePixels = true; UseLayoutRounding = true;
        Loaded += (_, _) => { SystemFileIconProvider.Invalidated += Invalidated; Refresh(); };
        Unloaded += (_, _) => { SystemFileIconProvider.Invalidated -= Invalidated; request++; };
        SizeChanged += (_, _) => Refresh();
    }
    void Invalidated(object? sender, EventArgs e) => Dispatcher.BeginInvoke(Refresh);
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi) { base.OnDpiChanged(oldDpi, newDpi); Refresh(); }
    async void Refresh()
    {
        var entry = Entry; if (entry is null || !IsLoaded) return;
        var version = ++request; Source = FileSystemIcons.For(entry);
        if (!entry.IsRegularFile && !entry.IsDirectory && !entry.IsSymbolicLink) return;
        var dpi = VisualTreeHelper.GetDpi(this); var size = Math.Max(ActualWidth, ActualHeight);
        if (!double.IsFinite(size) || size <= 0) size = 24;
        var source = await SystemFileIconProvider.GetAsync(entry, size * Math.Max(dpi.DpiScaleX, dpi.DpiScaleY));
        if (version == request && IsLoaded && source is not null) Source = source;
        InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (Entry?.IsSymbolicLink != true || ActualWidth < 10 || ActualHeight < 10) return;
        double size = Math.Clamp(ActualWidth * .28, 8, 20), y = ActualHeight - size;
        dc.PushTransform(new TranslateTransform(0, y)); dc.PushTransform(new ScaleTransform(size / 10, size / 10));
        dc.DrawRoundedRectangle(SystemColors.WindowBrush, new Pen(SystemColors.WindowTextBrush, .6), new Rect(0, 0, 10, 10), 1, 1);
        dc.DrawGeometry(null, new Pen(SystemColors.WindowTextBrush, 1.2), Geometry.Parse("M2,8 L2,5 7,5 M5,2 L8,5 5,8")); dc.Pop(); dc.Pop();
    }
}
