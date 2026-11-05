using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using SSHExplorer.App;
using SSHExplorer.Core;

/// <summary>0.3 C1–C4 on fixtures (no server): archive dialogs, content search, services page with its journal, viewer at a line.</summary>
internal static class GroupCUiChecks
{
    public static void Run(AppStore store, CiVisualEvidence evidence, Action<bool, string> check)
    {
        var errors = new List<string>(); Ui.ErrorOverride = message => { lock (errors) errors.Add(message); };
        try { RunCore(store, evidence, check); }
        finally { Ui.ErrorOverride = null; }
        lock (errors) check(errors.Count == 0, "no error dialog was raised during the archive/search/services checks" + (errors.Count > 0 ? ": " + string.Join(" | ", errors) : ""));
    }

    static void RunCore(AppStore store, CiVisualEvidence evidence, Action<bool, string> check)
    {
        // C1: create — a missing tool marks the format, a taken name blocks, switching the format keeps the typed stem.
        var create = new ArchiveCreateWindow("/srv/www", ["site", "logs"], new HashSet<string> { "tar", "gzip", "zip" }, new HashSet<string> { "site", "logs", "www.tar.gz" }) { ShowInTaskbar = false, Left = 60, Top = 60 };
        create.Show(); Layout(create);
        try
        {
            var format = Descendants<ComboBox>(create).Single(c => c.Name == "ArchiveFormat");
            var name = Descendants<TextBox>(create).Single(b => b.Name == "ArchiveName");
            var button = Descendants<Button>(create).Single(b => b.Name == "ArchiveCreate");
            var labels = format.Items.Cast<object>().Select(i => i.GetType().GetProperty("Label")!.GetValue(i) as string).ToArray();
            check(labels.Any(l => l!.Contains("нет xz", StringComparison.Ordinal)) && labels.Any(l => l!.Contains("нет bzip2", StringComparison.Ordinal)), "formats whose tools are missing on the server say so: " + string.Join(" | ", labels));
            check(name.Text == "www.tar.gz" && !button.IsEnabled && create.Problem!.Contains("уже есть", StringComparison.Ordinal), "the suggested name is taken: creating is blocked with a reason");
            name.Text = "backup.tar.gz"; Pump();
            check(button.IsEnabled && create.Problem is null, "a free name enables creating");
            format.SelectedIndex = 4; Pump();
            check(name.Text == "backup.zip" && create.Format == ArchiveFormat.Zip && button.IsEnabled, "switching to zip keeps the stem: " + name.Text);
            evidence.Capture((FrameworkElement)create.Content, "archive-create", new { theme = "Light", fixtureOnly = true });
        }
        finally { create.Close(); }

        // C1: extract — unsafe entries block; a free folder name is suggested; «here» extracts into the archive's folder.
        var evil = new ArchiveExtractWindow("/srv/site.tar.gz", Archives.ParseListing("a.txt\n../../etc/cron.d/x\n"), new HashSet<string>(), null) { ShowInTaskbar = false, Left = 60, Top = 60 };
        evil.Show(); Layout(evil);
        try { check(!Descendants<Button>(evil).Single(b => b.Name == "ExtractApply").IsEnabled && evil.Problem!.Contains("вне папки", StringComparison.Ordinal), "an archive with paths outside the destination is blocked: " + evil.Problem); }
        finally { evil.Close(); }
        var extract = new ArchiveExtractWindow("/srv/site.tar.gz", Archives.ParseListing("site/\nsite/index.html\n"), new HashSet<string> { "site", "site.tar.gz" }, null) { ShowInTaskbar = false, Left = 60, Top = 60 };
        extract.Show(); Layout(extract);
        try
        {
            var apply = Descendants<Button>(extract).Single(b => b.Name == "ExtractApply");
            check(apply.IsEnabled && extract.Mode == ExtractMode.NewFolder && extract.Destination == "/srv/site (2)", "a new folder that does not exist yet is suggested: " + extract.Destination);
            Descendants<TextBox>(extract).Single(b => b.Name == "ExtractFolder").Text = "site"; Pump();
            check(!apply.IsEnabled && extract.Problem!.Contains("уже есть", StringComparison.Ordinal), "an existing folder name blocks extracting into a new folder");
            Descendants<RadioButton>(extract).Single(r => r.Name == "ExtractHere").IsChecked = true; Pump();
            check(apply.IsEnabled && extract.Mode == ExtractMode.HereKeepExisting && extract.Destination == "/srv", "«into this folder» keeps existing files and targets the archive's folder");
            evidence.Capture((FrameworkElement)extract.Content, "archive-extract", new { theme = "Light", fixtureOnly = true });
        }
        finally { extract.Close(); }

        // C4: content search on a fixture grep output; Enter opens the match at its line.
        var opened = new List<(string Path, int Line, string Text)>(); string? sent = null;
        var search = new ContentSearchWindow(null, "/etc/nginx") { ShowInTaskbar = false, Left = 60, Top = 60,
            ExecuteOverride = (command, _) => { sent = command; return Task.FromResult(new CommandResult("./sites/a:b.conf\u000012:    listen 80;\n./nginx.conf\u00003:listen 443 ssl;\n", "", 0)); },
            OpenOverride = (path, line, text) => opened.Add((path, line, text)) };
        search.Show(); Layout(search);
        try
        {
            Descendants<TextBox>(search).Single(b => b.Name == "GrepPattern").Text = "listen";
            Complete(search.SearchAsync()); Pump();
            check(search.Results.Count == 2 && search.Results[0] is { Path: "sites/a:b.conf", Line: 12 } && search.StateText.Contains("Совпадений: 2 в файлах: 2", StringComparison.Ordinal), "matches are listed with file and line: " + search.StateText);
            check(sent is not null && sent.Contains("-e 'listen'", StringComparison.Ordinal) && sent.StartsWith("cd '/etc/nginx'", StringComparison.Ordinal), "grep runs in the chosen folder with the quoted pattern");
            search.Table.SelectedIndex = 0; search.Table.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(search.Table)!, 0, System.Windows.Input.Key.Enter) { RoutedEvent = UIElement.PreviewKeyDownEvent });
            check(opened.Count == 1 && opened[0] == ("/etc/nginx/sites/a:b.conf", 12, "listen"), "Enter opens the selected match at its line: " + string.Join(",", opened));
            evidence.Capture((FrameworkElement)search.Content, "content-search", new { theme = "Light", fixtureOnly = true });
        }
        finally { search.Close(); }

        // The viewer opened from a match shows that line with the text selected.
        var text = string.Join("\n", Enumerable.Range(1, 200).Select(i => i == 150 ? "    server_name Example.org;" : "line " + i));
        var viewer = new TextViewerWindow("site.conf", _ => Task.FromResult(System.Text.Encoding.UTF8.GetBytes(text))) { ShowInTaskbar = false, Left = 60, Top = 60, StartLine = 150, StartSearch = "example" };
        viewer.Show(); Layout(viewer);
        try
        {
            var box = Descendants<TextBox>(viewer).Single(b => b.Name == "ViewerText");
            for (int i = 0; i < 50 && box.Text.Length == 0; i++) Pump();
            check(viewer.CaretLine == 150 && box.SelectedText == "Example", $"the viewer opens at the match: line {viewer.CaretLine}, selected «{box.SelectedText}»");
        }
        finally { viewer.Close(); }

        // C2/C3: services page on a fixture executor; the journal streams for the selected service; actions are confirmed.
        var profile = new ServerProfile { Name = "Службы · фикстура", Host = "systemd-fixture.example.invalid", Username = "fixture" };
        var systemd = new SystemdService(new ServerSession(profile, store));
        var commands = new List<string>(); var confirmations = new List<string>();
        systemd.ExecuteOverride = (command, _) =>
        {
            lock (commands) commands.Add(command);
            var output = command.StartsWith("LC_ALL=C; export LC_ALL; if ! command -v systemctl", StringComparison.Ordinal)
                ? "#uid 1000\n#sudo\n#units\nnginx.service loaded active running A high performance web server\ncron.service loaded failed failed Regular background program processing daemon\nssh.service loaded active running OpenBSD Secure Shell server\n#files\nnginx.service enabled enabled\ncron.service enabled enabled\nssh.service enabled enabled\nbackup.service disabled enabled\n"
                : "";
            return Task.FromResult(new CommandResult(output, "", 0));
        };
        systemd.StreamOverride = (command, onLine, ct) =>
        {
            lock (commands) commands.Add(command);
            foreach (var l in new[] { "2026-11-05T10:00:00+0000 host nginx[1]: started", "2026-11-05T10:00:01+0000 host nginx[1]: GET / 200", "2026-11-05T10:00:02+0000 host nginx[1]: error: upstream timed out" }) onLine(l);
            return Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => (int?)null, TaskScheduler.Default);
        };
        var view = new ServicesView(systemd) { ConfirmOverride = t => { confirmations.Add(t); return true; } };
        var host = new Window { Width = 1180, Height = 780, Content = view, ShowInTaskbar = false, Left = 60, Top = 60 }; host.Show(); Layout(host);
        try
        {
            for (int i = 0; i < 50 && view.Table.Items.Count == 0; i++) Pump();
            var names = view.Table.Items.Cast<ServiceUnit>().Select(u => u.Name).ToArray();
            check(names.Length == 4 && names[0] == "cron.service" && Descendants<TextBlock>(view).Single(b => b.Name == "ServicesState").Text.Contains("sudo", StringComparison.Ordinal), "services are listed, failed first, with the rights in the footer: " + string.Join(",", names));
            view.Table.SelectedItem = view.Table.Items.Cast<ServiceUnit>().Single(u => u.Name == "nginx.service");
            for (int i = 0; i < 40 && !view.Journal.Shown.Contains("upstream", StringComparison.Ordinal); i++) { Wait(50); Pump(); }
            check(view.Journal.Shown.Contains("GET / 200", StringComparison.Ordinal) && view.Journal.Running && commands.Any(c => c.Contains("journalctl", StringComparison.Ordinal) && c.Contains("-f -u 'nginx.service'", StringComparison.Ordinal)), "the selected service's journal is followed: " + view.Journal.StateText);
            Descendants<TextBox>(view).Single(b => b.Name == "LogFilter").Text = "error"; Pump();
            check(view.Journal.Shown == "2026-11-05T10:00:02+0000 host nginx[1]: error: upstream timed out" && view.Journal.StateText.Contains("показано 1 из 3", StringComparison.Ordinal), "the filter narrows the journal without stopping it: " + view.Journal.StateText);
            Descendants<Button>(view).Single(b => b.Name == "ServicesRestart").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            for (int i = 0; i < 20 && !commands.Any(c => c.Contains("restart", StringComparison.Ordinal)); i++) Pump();
            check(confirmations.Count == 1 && confirmations[0].Contains("Перезапустить «nginx.service»", StringComparison.Ordinal) && commands.Contains("LC_ALL=C sudo -n systemctl --no-ask-password restart -- 'nginx.service' 2>&1"), "restart is confirmed, then sent through sudo -n: " + string.Join(" | ", confirmations));
            evidence.Capture((FrameworkElement)view, "services", new { theme = "Light", fixtureOnly = true });
            view.Table.SelectedItem = view.Table.Items.Cast<ServiceUnit>().Single(u => u.Name == "ssh.service"); Pump();
            Descendants<Button>(view).Single(b => b.Name == "ServicesStop").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            for (int i = 0; i < 20 && confirmations.Count < 2; i++) Pump();
            check(confirmations.Count == 2 && confirmations[1].Contains("Это служба SSH", StringComparison.Ordinal), "stopping the SSH service warns that this connection may drop");
            check(!Descendants<Button>(view).Single(b => b.Name == "ServicesEnable").IsEnabled, "enabling autostart is off for a service that already starts at boot");
            host.Content = null; Pump();
            check(!view.Journal.Running, "hiding the page stops the followed journal");
        }
        finally { host.Close(); view.Dispose(); }
    }

    static void Wait(int ms) { var until = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < until) Pump(); }
    static void Complete(Task task) { var frame = new DispatcherFrame(); task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default); Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult(); }
    static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false); Dispatcher.PushFrame(frame); }
    static void Layout(Window w) { w.UpdateLayout(); Pump(); }
    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match; foreach (var nested in Descendants<T>(child)) yield return nested; } }
}
