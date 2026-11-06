using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using SSHExplorer.Core;

namespace SSHExplorer.App;

/// <summary>One notification: when, which server, what; resolved ones stay in the list until cleared.</summary>
public sealed class AlertItem(DateTime time, Guid server, string serverName, string key, string text) : ViewModels.ObservableObject
{
    bool resolved;
    public DateTime Time => time;
    public Guid Server => server;
    public string ServerName => serverName;
    public string Key => key;
    public string Text => text;
    public bool Resolved { get => resolved; set { if (Set(ref resolved, value)) Raise(nameof(Display)); } }
    public string Display => $"{time:HH:mm} · {serverName}: {text}" + (resolved ? " — решено" : "");
}

/// <summary>
/// Notifications while the app is open. Each server's check reports its current issues; only new ones notify
/// (a Windows notification when allowed, the taskbar otherwise), and ones that went away are marked resolved.
/// </summary>
public static class AlertCenter
{
    static readonly Dictionary<(Guid Server, string Source), HashSet<string>> active = [];
    public static ObservableCollection<AlertItem> Items { get; } = [];
    public static event EventHandler? Changed;
    /// <summary>Test hook: receives what would be shown as a Windows notification.</summary>
    internal static Action<string, string>? NotifyOverride { get; set; }
    public static int ActiveCount => Items.Count(i => !i.Resolved);

    /// <summary>The server's issues now: new ones are added and notified, missing ones marked resolved.</summary>
    /// <remarks>Each source (health check, metric history) reports its own set; one source never resolves another's.</remarks>
    public static IReadOnlyList<HealthIssue> Report(Guid server, string serverName, IReadOnlyList<HealthIssue> issues, string source = "health")
    {
        if (!active.TryGetValue((server, source), out var previous)) active[(server, source)] = previous = [];
        var (fresh, resolved) = ServerHealth.Diff(previous, issues);
        foreach (var key in resolved) { previous.Remove(key); foreach (var item in Items.Where(i => i.Server == server && i.Key == key && !i.Resolved)) item.Resolved = true; }
        foreach (var issue in fresh) { previous.Add(issue.Key); Add(server, serverName, issue.Key, issue.Text); }
        if (fresh.Count > 0) Notify(serverName, fresh.Count == 1 ? fresh[0].Text : $"{fresh[0].Text} и ещё {fresh.Count - 1}");
        if (fresh.Count + resolved.Count > 0) Changed?.Invoke(null, EventArgs.Empty);
        return fresh;
    }

    /// <summary>A one-off event (for example, a failed automatic script).</summary>
    public static void Raise(Guid server, string serverName, string text)
    {
        Add(server, serverName, "event:" + Guid.NewGuid().ToString("N"), text);
        Notify(serverName, text); Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void ClearResolved() { foreach (var i in Items.Where(i => i.Resolved).ToArray()) Items.Remove(i); Changed?.Invoke(null, EventArgs.Empty); }
    public static void ClearAll() { foreach (var i in Items.Where(i => i.Key.StartsWith("event:", StringComparison.Ordinal)).ToArray()) Items.Remove(i); ClearResolved(); }
    public static void Forget(Guid server, string source = "health") { if (active.Remove((server, source), out var keys)) { foreach (var item in Items.Where(i => i.Server == server && !i.Resolved && keys.Contains(i.Key))) item.Resolved = true; Changed?.Invoke(null, EventArgs.Empty); } }
    internal static void Reset() { active.Clear(); Items.Clear(); }

    static void Add(Guid server, string name, string key, string text)
    {
        Items.Insert(0, new AlertItem(DateTime.Now, server, name, key, text));
        while (Items.Count > 200) Items.RemoveAt(Items.Count - 1);
    }

    static void Notify(string title, string text)
    {
        if (NotifyOverride is { } fake) { fake(title, text); return; }
        if (App.Store?.Settings.AlertWindowsNotifications == false || Application.Current?.MainWindow is not Window main) return;
        if (!TrayNotifier.Show(main, "SSH Explorer · " + title, text)) TrayNotifier.Flash(main);
    }
}

/// <summary>A Windows notification through the notification area (Shell_NotifyIcon), without WinForms.</summary>
internal static class TrayNotifier
{
    const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10, NIIF_WARNING = 2, WM_USER = 0x400, Callback = WM_USER + 77, NIN_BALLOONUSERCLICK = WM_USER + 5, WM_LBUTTONUP = 0x202;
    static bool added; static HwndSource? source; static IntPtr icon;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NotifyIconData
    {
        public int cbSize; public IntPtr hWnd; public int uID; public int uFlags; public int uCallbackMessage; public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState; public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);
    [DllImport("user32.dll")] static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int ExtractIconEx(string file, int index, IntPtr large, ref IntPtr small, int count);
    [DllImport("user32.dll")] static extern bool FlashWindowEx(ref FlashInfo info);
    [StructLayout(LayoutKind.Sequential)] struct FlashInfo { public int cbSize; public IntPtr hwnd; public int dwFlags; public int uCount; public int dwTimeout; }

    static NotifyIconData Data(IntPtr hwnd) => new() { cbSize = Marshal.SizeOf<NotifyIconData>(), hWnd = hwnd, uID = 1, uCallbackMessage = Callback, hIcon = icon, szTip = "SSH Explorer", szInfo = "", szInfoTitle = "" };

    public static bool Show(Window owner, string title, string text)
    {
        try
        {
            var hwnd = new WindowInteropHelper(owner).Handle; if (hwnd == IntPtr.Zero) return false;
            if (!added)
            {
                source = HwndSource.FromHwnd(hwnd); source?.AddHook(Hook);
                IntPtr small = IntPtr.Zero; if (Environment.ProcessPath is { } exe) ExtractIconEx(exe, 0, IntPtr.Zero, ref small, 1);
                icon = small != IntPtr.Zero ? small : LoadIcon(IntPtr.Zero, (IntPtr)32515);
                var add = Data(hwnd); add.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
                if (!Shell_NotifyIcon(NIM_ADD, ref add)) return false;
                added = true; owner.Closed += (_, _) => Remove(hwnd);
            }
            var info = Data(hwnd); info.uFlags = NIF_INFO; info.szInfoTitle = Cut(title, 63); info.szInfo = Cut(text, 255); info.dwInfoFlags = NIIF_WARNING;
            return Shell_NotifyIcon(NIM_MODIFY, ref info);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException) { return false; }
    }

    static string Cut(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    static void Remove(IntPtr hwnd) { if (!added) return; var d = Data(hwnd); Shell_NotifyIcon(NIM_DELETE, ref d); added = false; source?.RemoveHook(Hook); }

    static IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Callback && ((int)lParam & 0xFFFF) is NIN_BALLOONUSERCLICK or WM_LBUTTONUP && Application.Current?.MainWindow is Window main)
        {
            if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal;
            main.Activate(); (main as MainWindow)?.ShowAlerts(); handled = true;
        }
        return IntPtr.Zero;
    }

    public static void Flash(Window owner)
    {
        if (owner.IsActive) return;
        var info = new FlashInfo { cbSize = Marshal.SizeOf<FlashInfo>(), hwnd = new WindowInteropHelper(owner).Handle, dwFlags = 3 | 12, uCount = 5 };
        if (info.hwnd != IntPtr.Zero) FlashWindowEx(ref info);
    }
}
