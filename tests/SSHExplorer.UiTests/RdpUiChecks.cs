using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Media;
using System.Windows.Threading;
using SSHExplorer.App;
using SSHExplorer.App.ViewModels;
using SSHExplorer.Core;

/// <summary>
/// 0.5: RDP profiles in the profile dialog, on Home and as tabs. The real Windows control (mstscax) is created on the
/// runner and dials a closed port: the failure must come back as an explained state, not a hang or a crash.
/// </summary>
internal static class RdpUiChecks
{
    public static void Run(MainWindow main, AppStore store, CiVisualEvidence evidence, Action<bool, string> check)
    {
        // Profile dialog: RDP hides SSH sign-in, shows the domain and the RDP options, and saves an RDP profile.
        var model = new ProfileViewModel(new ProfilePersistence(store));
        model.ProfileName = "win-01"; model.Host = "win01.example.test"; model.Username = "admin";
        model.ProtocolIndex = 1;
        check(model.Port == "3389" && model.IsRdp && !model.IsKeyAuthentication && model.UsernameLabel == "Пользователь Windows" && model.SecretLabel == "Пароль Windows", "choosing RDP moves the default port to 3389 and asks for a Windows user and password");
        model.ProtocolIndex = 0; check(model.Port == "22", "back to SSH restores port 22");
        model.ProtocolIndex = 1; model.Domain = "CORP"; model.FitTab = false; model.RdpWidth = "300"; model.RdpHeight = "200";
        check(!model.Validate(""), "a desktop smaller than 640 × 480 is refused");
        model.RdpWidth = "1600"; model.RdpHeight = "900"; model.Printers = true; model.GatewayModeIndex = 1;
        check(!model.Validate("") && model.RdpError.Contains("шлюз", StringComparison.OrdinalIgnoreCase), "a gateway without an address is refused: " + model.RdpError);
        model.GatewayHost = "gw.example.test";
        check(model.Validate(""), "a complete RDP profile validates");
        var dialog = new ProfileDialog(new ProfilePersistence(store), new ServerProfile { Name = "win-01", Host = "win01.example.test", Port = 3389, Username = "admin", Protocol = ConnectionProtocol.Rdp, Rdp = new RdpOptions { Domain = "CORP" } }, main) { Width = 560, ShowInTaskbar = false };
        dialog.Show(); Pump();
        try
        {
            var advanced = Descendants<Expander>(dialog).First(); advanced.IsExpanded = true; dialog.UpdateLayout(); Pump();
            var auth = Descendants<ComboBox>(dialog).First(c => System.Windows.Automation.AutomationProperties.GetName(c) == "Способ входа");
            var options = (FrameworkElement)LogicalTreeHelper.FindLogicalNode(dialog, "RdpOptions");
            check(auth.Visibility != Visibility.Visible && options is { Visibility: Visibility.Visible } && Descendants<TextBox>(dialog).Any(t => t.IsVisible && System.Windows.Automation.AutomationProperties.GetName(t) == "Домен (необязательно)" && t.Text == "CORP"),
                "an RDP profile shows the domain and the RDP options, not the SSH sign-in");
            evidence.Capture((FrameworkElement)dialog.Content, "rdp-profile", new { theme = "Light", fixtureOnly = true });
        }
        finally { dialog.Close(); }

        // A desktop tab on a closed port: the actual control is created and the failure is explained in place.
        var profile = new ServerProfile { Name = "Закрытый RDP", Host = "127.0.0.1", Port = FreePort(), Username = "nobody", Protocol = ConnectionProtocol.Rdp, Rdp = new RdpOptions { Nla = true } };
        var view = main.AddDesktopForTest(profile); Pump(); Layout(main);
        var layer = Descendants<Canvas>(main).Single(c => c.Name == "DesktopLayer");
        var host = Descendants<WindowsFormsHost>(layer).Single();
        check(view.State == SessionState.Closed && host.Visibility != Visibility.Visible && view.MessageText == "Не подключён", "a new desktop tab waits with an explanation; the native control is hidden");
        var connect = view.ConnectAsync("not-a-real-password", CancellationToken.None);
        bool sawConnecting = view.State == SessionState.Connecting;
        var watch = Stopwatch.StartNew();
        while (!connect.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(90)) Pump();
        bool result = connect.IsCompleted && connect.Result;
        check(sawConnecting && connect.IsCompleted && !result, $"dialing a closed port ends (in {watch.Elapsed.TotalSeconds:0} s) instead of hanging");
        check(view.ControlCreated, "the Windows remote desktop control is created on the runner and its events are attached");
        check(view.State == SessionState.Disconnected && !string.IsNullOrWhiteSpace(view.LastError) && host.Visibility != Visibility.Visible && view.MessageText == view.LastError,
            "the failure is shown in the tab with the reason: " + view.LastError);
        evidence.Capture(view, "rdp-tab-failed", new { theme = "Light", fixtureOnly = true, reason = view.LastError });
        // Home lists RDP profiles with their own icon and the domain beside the user.
        var row = new ServerProfileViewModel(new ServerProfile { Name = "w", Host = "h", Port = 3389, Username = "admin", Protocol = ConnectionProtocol.Rdp, Rdp = new RdpOptions { Domain = "CORP" } }, []);
        check(row.ProtocolGlyph == IconCatalog.RemoteDesktop && row.Username == "CORP\\admin" && new ServerProfileViewModel(new ServerProfile { Name = "s", Host = "h", Username = "u" }, []).ProtocolGlyph == IconCatalog.Terminal, "Home shows the protocol and DOMAIN\\user");
        check(Close(main, view) && !main.Desktops.Contains(view) && !Descendants<WindowsFormsHost>(layer).Any(), "closing the tab removes the control");
        LiveCheck(main, store, check);
    }

    /// <summary>
    /// With a local account prepared by the workflow (RDP_TEST_USER/RDP_TEST_PASSWORD), sign in to the runner itself
    /// over RDP: the key prompt, the sign-in and a clean disconnect. Skipped (and said so) when the variables are absent.
    /// </summary>
    static void LiveCheck(MainWindow main, AppStore store, Action<bool, string> check)
    {
        var user = Environment.GetEnvironmentVariable("RDP_TEST_USER"); var password = Environment.GetEnvironmentVariable("RDP_TEST_PASSWORD");
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password)) { Console.WriteLine("SKIP live RDP sign-in: no RDP_TEST_USER on this runner."); return; }
        // Until the runner is known to accept RDP to itself, a failed live sign-in is reported, not fatal (RDP_LIVE_REQUIRED=1 makes it fatal).
        bool required = Environment.GetEnvironmentVariable("RDP_LIVE_REQUIRED") == "1";
        Action<bool, string> live = (ok, text) => { if (required) check(ok, text); else Console.WriteLine((ok ? "PASS " : "INFO (not required) ") + text); };
        var prompts = new List<HostKeyPrompt>(); main.DesktopCloseConfirmOverride = _ => true;
        // A native control that blocks the UI thread would otherwise hold the whole step until its time limit.
        string stage = "start";
        using var watchdog = new System.Threading.Timer(_ => { Console.WriteLine("FAIL live RDP blocked the UI thread at stage: " + stage); Environment.Exit(3); }, null, TimeSpan.FromMinutes(5), Timeout.InfiniteTimeSpan);
        Dialogs.RdpCertificateOverride = p => { prompts.Add(p); return TrustDecision.Permanent; };
        try
        {
            var profile = new ServerProfile { Name = "Этот компьютер по RDP", Host = Environment.GetEnvironmentVariable("RDP_TEST_HOST") ?? "127.0.0.2", Port = 3389, Username = user, Protocol = ConnectionProtocol.Rdp, Rdp = new RdpOptions { Domain = Environment.MachineName } };
            var view = main.AddDesktopForTest(profile); Pump(); Layout(main);
            stage = "connect"; var connect = view.ConnectAsync(password, CancellationToken.None);
            var watch = Stopwatch.StartNew(); while (!connect.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(120)) Pump();
            live(connect.IsCompleted && connect.Result && view.State == SessionState.Connected, $"live: signed in to the runner over RDP in {watch.Elapsed.TotalSeconds:0} s: " + (view.LastError ?? view.StatusText));
            live(prompts.Count == 1 && prompts[0].Fingerprint.StartsWith("SHA256:", StringComparison.Ordinal) && store.Trust.Records.Any(r => r.Algorithm == RdpTrust.Algorithm), "live: the server key was confirmed before sign-in and saved");
            for (int i = 0; i < 20; i++) Pump();
            // 0.6: the Windows key (Start menu) and a picture of the desktop.
            stage = "snapshot";
            var shot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sshx-rdp-" + Guid.NewGuid().ToString("N") + ".png");
            var shotErrors = new List<string>(); Ui.ErrorOverride = shotErrors.Add; RdpView.SnapshotPathOverride = () => shot;
            int colours = 0;
            try
            {
                view.StartMenu();
                // Right after sign-in the remote desktop may not be drawn yet: take pictures until it is (up to ~20 s).
                var shotWatch = Stopwatch.StartNew();
                do
                {
                    for (int i = 0; i < 10; i++) Pump();
                    view.Snapshot();
                    if (System.IO.File.Exists(shot))
                    {
                        using var picture = new System.Drawing.Bitmap(shot);
                        colours = Enumerable.Range(0, 100).Select(i => picture.GetPixel(picture.Width * (i % 10) / 10, picture.Height * (i / 10) / 10).ToArgb()).Distinct().Count();
                    }
                    if (colours > 1) break;
                    var pause = Stopwatch.StartNew(); while (pause.Elapsed < TimeSpan.FromSeconds(2)) Pump();
                } while (shotWatch.Elapsed < TimeSpan.FromSeconds(20));
            }
            finally { Ui.ErrorOverride = null; RdpView.SnapshotPathOverride = null; }
            live(colours > 1 && shotErrors.Count == 0, $"live: «Пуск» is sent and «Снимок» saves the desktop as PNG ({colours} colours in 100 samples) " + string.Join(" | ", shotErrors));
            stage = "disconnect"; var disconnect = view.DisconnectAsync(); watch.Restart();
            while (view.State != SessionState.Disconnected && watch.Elapsed < TimeSpan.FromSeconds(30)) Pump();
            live(view.State == SessionState.Disconnected && view.MessageText == "Подключение завершено.", "live: «Отключить» ends the session: " + view.MessageText);
            // A second sign-in reuses the saved key without asking.
            stage = "reconnect"; var again = view.ConnectAsync(password, CancellationToken.None); watch.Restart(); while (!again.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(120)) Pump();
            live(again.IsCompleted && again.Result && prompts.Count == 1, "live: the saved key is accepted next time without a prompt");
            stage = "disconnect again"; var disconnectAgain = view.DisconnectAsync(); watch.Restart(); while (view.State != SessionState.Disconnected && watch.Elapsed < TimeSpan.FromSeconds(30)) Pump();
            stage = "close"; live(Close(main, view), "live: the tab closes"); stage = "done";
        }
        finally { Dialogs.RdpCertificateOverride = null; main.DesktopCloseConfirmOverride = null; }
    }

    static bool Close(MainWindow main, RdpView view)
    {
        var tab = Descendants<TabControl>(main).First().Items.OfType<TabItem>().Single(t => ReferenceEquals(t.Content, view));
        var close = (Task)typeof(MainWindow).GetMethod("CloseTab", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(main, [tab])!;
        var watch = Stopwatch.StartNew(); while (!close.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(20)) Pump();
        return close.IsCompleted && !main.Desktops.Contains(view);
    }
    static int FreePort() { var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); l.Start(); int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return port; }
    static void Layout(Window window) { window.UpdateLayout(); Pump(); window.UpdateLayout(); Pump(); }
    static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false); Dispatcher.PushFrame(frame); }
    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match; foreach (var nested in Descendants<T>(child)) yield return nested; } }
}
