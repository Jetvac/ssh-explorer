using System.IO;
using System.Text.Json;
using System.Windows;
using SSHExplorer.Core;

namespace SSHExplorer.App;

/// <summary>Explicit loopback-only integration entry point. Never reads ordinary user profiles.</summary>
internal static class TestSessionHarness
{
    private sealed class Configuration
    {
        public string Directory { get; set; } = "";
        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 22222;
        public string Username { get; set; } = "sshx-test";
        public string Secret { get; set; } = "";
        public string Fingerprint { get; set; } = "";
        public string InitialPath { get; set; } = "~";
        public string Name { get; set; } = "Локальный испытательный стенд";
        public bool RunTerminalProbe { get; set; }
        public bool RunFileLoadProbe { get; set; }
        public bool RunClipboardProbe { get; set; }
        public bool ExitAfterProbes { get; set; }
        public string? Theme { get; set; }
        public double? WindowWidth { get; set; }
        public double? WindowHeight { get; set; }
        public List<Configuration>? Nodes { get; set; }
    }
    private static Configuration? _configuration;
    public static string? TryGetStoreDirectory(string[] args)
    {
        if (args.Length == 2 && args[0] == "--settings-dir")
        {
            if (!Path.IsPathFullyQualified(args[1])) throw new ArgumentException("Укажите абсолютную папку настроек.");
            return args[1];
        }
        if (args.Length == 0 || args[0] != "--test-session") return null;
        if (args.Length != 2) throw new ArgumentException("Испытательный режим: --test-session <config.json>");
        var c = JsonSerializer.Deserialize<Configuration>(File.ReadAllText(args[1])) ?? throw new InvalidDataException("Пустая конфигурация стенда.");
        foreach (var node in c.Nodes ?? [c])
        {
            if (node.Host != "127.0.0.1" && node.Host != "::1") throw new ArgumentException("Испытательный режим разрешён только для изолированного loopback-стенда.");
            if (!node.Fingerprint.StartsWith("SHA256:", StringComparison.Ordinal)) throw new ArgumentException("Укажите ожидаемый SHA256-отпечаток стенда.");
        }
        if (string.IsNullOrWhiteSpace(c.Directory) || !Path.IsPathFullyQualified(c.Directory)) throw new ArgumentException("Укажите отдельную абсолютную папку настроек.");
        _configuration = c; return c.Directory;
    }
    public static async Task StartAsync(App app, string[] args)
    {
        if (_configuration is not { } c || app.MainWindow is not MainWindow main) return;
        main.Title += " — ИЗОЛИРОВАННЫЙ ИСПЫТАТЕЛЬНЫЙ РЕЖИМ";
        if (c.Theme is "Light" or "Dark" or "System") { App.Store.Settings.Theme = c.Theme; Theme.Apply(c.Theme); }
        if (c.WindowWidth is { } width) main.Width = Math.Clamp(width, 860, 2000);
        if (c.WindowHeight is { } height) main.Height = Math.Clamp(height, 560, 1200);
        foreach (var node in c.Nodes ?? [c])
        {
            var profile = App.Store.Profiles.FirstOrDefault(p => p.Host == node.Host && p.Port == node.Port && p.Username == node.Username && p.Name == node.Name)
                ?? new ServerProfile { Name = node.Name, Host = node.Host, Port = node.Port, Username = node.Username, InitialPath = node.InitialPath };
            if (!App.Store.Profiles.Contains(profile)) await App.Store.SaveProfileAsync(profile, null);
            await main.OpenServer(profile, suppliedCredentials: new SessionCredentials(node.Secret), trustOverride: key => Task.FromResult(key.Fingerprint == node.Fingerprint && !key.Changed ? TrustDecision.Permanent : TrustDecision.Reject));
        }
        if (c.RunFileLoadProbe) await main.RunFileLoadProbeAsync();
        if (c.RunClipboardProbe) await main.RunClipboardProbeAsync();
        if (c.RunTerminalProbe) await main.RunTerminalProbeAsync();
        if (c.ExitAfterProbes) main.Close();
    }
}

public sealed partial class MainWindow
{
    internal async Task RunFileLoadProbeAsync()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        UpdateLayout();
        IEnumerable<T> Visuals<T>(DependencyObject parent) where T : DependencyObject
        {
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T item) yield return item;
                foreach (var nested in Visuals<T>(child)) yield return nested;
            }
        }
        var grid = Visuals<System.Windows.Controls.DataGrid>(this).First(g => g.IsVisible && g.Items.Count >= 10_000);
        var rows = Visuals<System.Windows.Controls.DataGridRow>(grid).Count();
        var report = new { RealSftpEntryCount = grid.Items.Count, RealizedRowCount = rows, DispatcherIdleMilliseconds = watch.Elapsed.TotalMilliseconds, WorkingSetMiB = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / 1024d / 1024d, RowVirtualization = grid.EnableRowVirtualization, Passed = grid.Items.Count >= 10_000 && rows is > 0 and < 100 };
        await File.WriteAllTextAsync(Path.Combine(App.Store.DirectoryPath, "file-load-probe.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal async Task RunTerminalProbeAsync()
    {
        var session = sessions.Values.First(v => v.Session.State == SessionState.Connected).Session;
        var terminal = new TerminalView(session);
        var window = new Window { Title = "Изолированная проверка xterm / WebView2", Owner = this, Width = 1000, Height = 650, Content = terminal };
        window.Closed += (_, _) => terminal.Dispose(); window.Show();
        var report = await terminal.RunAcceptanceProbeAsync();
        await File.WriteAllTextAsync(Path.Combine(App.Store.DirectoryPath, "terminal-probe.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
