using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SSHExplorer.App;
using SSHExplorer.App.ViewModels;
using SSHExplorer.Core;
using SSHExplorer.Monitoring;

/// <summary>
/// beta.6 user-action checks on real CI windows: tab strip after Show() without resize, address bar
/// entry through OS input, caption row geometry, header-click sorting with typed keys, slow-sample
/// graphs, settings/transfers/server panels and a WPF render of every command icon.
/// Input is CI-only software SendInput on the runner's own HWND; physical input stays NOT RUN.
/// </summary>
internal static class Beta6Tests
{
    static int count;
    static void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); count++; Console.WriteLine("PASS " + label); }

    public static int Run(string[] args)
    {
        CiVisualEvidence.RequireCi();
        try
        {
            var app = TestApplication.Create(); SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
            Theme.Apply("Light"); CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU"); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ru-RU");
            var store = new AppStore(Path.Combine(Environment.CurrentDirectory, ".tools", "beta6-ui-store", Guid.NewGuid().ToString("N"))); typeof(App).GetProperty(nameof(App.Store))!.SetValue(null, store);
            int index = Array.IndexOf(args, "--render-dir"); var evidence = new CiVisualEvidence(index < 0 ? "artifacts/ui-beta6" : args[index + 1]);
            TabStrip(store, evidence);
            AddressBar(store, evidence);
            Sorting(store, evidence);
            Graphs(evidence);
            Windows(store, evidence);
            Icons(evidence);
            app.Shutdown();
            Console.WriteLine($"RESULT: {count} beta.6 user-action checks passed; CI-only HWNDs and software SendInput; physical input, native DPI/High Contrast and motion NOT RUN");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
    }

    static MainWindow NewMain(double width = 1320, double height = 850)
    {
        var main = new MainWindow { Width = width, Height = height, ShowInTaskbar = false, ShowActivated = true, Left = 40, Top = 40, WindowStartupLocation = WindowStartupLocation.Manual };
        main.Show(); Layout(main); return main;
    }

    static (TabItem Tab, ServerSession Session) AddTab(MainWindow main, AppStore store, string name)
    {
        var profile = new ServerProfile { Name = name, Host = name.ToLowerInvariant().Replace(' ', '-') + ".example.invalid", Username = "fixture" }; store.Profiles.Add(profile);
        var session = new ServerSession(profile, store); var files = new FileBrowserView(session, main, new FileInteractionTests.Operations()); var monitor = new MonitorView(new MonitorViewModel());
        var view = new SessionView(session, main, files, monitor); view.SetActive(false);
        typeof(MainWindow).GetMethod("AddSessionTab", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [profile, session, view]);
        var tabs = (TabControl)Field(main, "tabs"); return ((TabItem)tabs.SelectedItem, session);
    }

    static void TabStrip(AppStore store, CiVisualEvidence evidence)
    {
        var main = NewMain();
        try
        {
            var tabs = (TabControl)Field(main, "tabs"); var home = (TabItem)Field(main, "home");
            var panel = Descendants<SessionHeaderPanel>(main).Single(); var scroll = (ScrollViewer)panel.Children[1]; var plus = (Button)panel.Children[3];
            Check(!panel.IsOverflowing && Visible(home, scroll), "Home-only strip: no overflow and Home fully inside the tab viewport");
            evidence.Capture((FrameworkElement)main.Content, "tabs-home-only", new { theme = "Light", fixtureOnly = true, extent = panel.TabExtent, viewport = panel.TabViewport });
            // The defect scenario: Show() already happened, layout settled, now open ONE server without any resize.
            var (first, _) = AddTab(main, store, "Первый сервер"); Pump(); Pump();
            Check(tabs.Items.Count == 2 && ReferenceEquals(tabs.SelectedItem, first), "first server tab is added and selected after Show() without resize");
            Check(!panel.IsOverflowing && Math.Abs(scroll.HorizontalOffset) < .5 && Math.Abs(panel.TabViewport - panel.TabExtent) < .5, "two tabs fit: viewport equals extent and the strip is not scrolled");
            Check(Visible(home, scroll) && Visible(first, scroll), "Home and the new server tab are both fully visible in the viewport");
            Check(HitsInside(main, home) && HitsInside(main, first), "hit-testing the centre of Home and of the server tab lands on those tabs");
            var plusLeft = plus.TransformToAncestor(panel).Transform(new Point()).X; var lastRight = first.TransformToAncestor(panel).TransformBounds(new Rect(first.RenderSize)).Right;
            Check(Math.Abs(plusLeft - lastRight) <= 4, $"«+» follows the last visible tab (gap {plusLeft - lastRight:0.0} DIP)");
            var close = Descendants<Button>((FrameworkElement)first.Header).Single(b => b.Name == "SessionTabCloseButton");
            Check(close.ActualWidth >= 24 && close.ActualHeight >= 24 && HitsInside(main, close), "close button has a ≥24 DIP target that receives the hit");
            evidence.Capture((FrameworkElement)main.Content, "tabs-home-plus-one", new { theme = "Light", fixtureOnly = true, extent = panel.TabExtent, viewport = panel.TabViewport, offset = scroll.HorizontalOffset });
            var created = new List<TabItem> { first };
            for (int i = 2; i <= 5; i++) created.Add(AddTab(main, store, $"Сервер {i}").Tab);
            Pump(); Check(panel.IsOverflowing ? panel.Children[0].IsVisible && panel.TabViewport < panel.TabExtent : !panel.Children[0].IsVisible && Math.Abs(panel.TabViewport - panel.TabExtent) < .5, "five tabs: arrows and viewport follow the real extent");
            for (int i = 6; i <= 12; i++) created.Add(AddTab(main, store, $"Сервер с длинным названием {i}").Tab);
            Pump(); Pump();
            Check(panel.IsOverflowing && panel.Children[0].IsVisible && panel.Children[2].IsVisible, "many tabs: arrows appear only on real overflow");
            Check(Visible((TabItem)tabs.SelectedItem, scroll), "the selected tab is revealed inside the viewport during overflow");
            evidence.Capture((FrameworkElement)main.Content, "tabs-overflow", new { theme = "Light", fixtureOnly = true, tabs = tabs.Items.Count });
            foreach (var tab in created.Skip(1).ToArray()) Complete(main.CloseSessionTabAsync(tab));
            Pump(); Pump();
            Check(!panel.IsOverflowing && !panel.Children[0].IsVisible && Math.Abs(scroll.HorizontalOffset) < .5 && Visible(home, scroll), "closing back to two tabs restores normal width without a window resize");
            main.Width = 760; Layout(main);
            Check(Visible(home, scroll) || panel.IsOverflowing, "narrow window keeps Home reachable (visible or via scrolling/list)");
            main.WindowState = WindowState.Maximized; Layout(main);
            var caption = main.Caption; var root = (FrameworkElement)main.Content;
            Check(root.Margin.Left > 0 && caption.MaximizeButton.ToolTip?.ToString() == "Восстановить", "maximized: content is inset by the hidden frame and Maximize becomes Restore");
            evidence.Capture(root, "caption-maximized", new { theme = "Light", fixtureOnly = true, inset = root.Margin.Left });
            main.WindowState = WindowState.Normal; main.Width = 1320; Layout(main);
            var surface = Descendants<Border>(tabs).First(b => b.Name == "HeaderSurface");
            var captionBounds = caption.TransformToAncestor(main).TransformBounds(new Rect(caption.RenderSize));
            Check(Math.Abs(surface.ActualWidth - root.ActualWidth) < 1, "caption surface spans the whole window width (no DWM strip under the buttons)");
            Check(Math.Abs(captionBounds.Right - root.ActualWidth) < 1.5 && caption.CloseButton.ActualWidth >= 45 && caption.CloseButton.ActualHeight >= 40, "caption buttons sit at the right edge with Windows 11 sizes");
            var actions = Descendants<StackPanel>(main).First(p => p.Name == "CaptionActions");
            var actionBounds = actions.TransformToAncestor(main).TransformBounds(new Rect(actions.RenderSize)); var panelBounds = panel.TransformToAncestor(main).TransformBounds(new Rect(panel.RenderSize));
            Check(actionBounds.Left >= panelBounds.Right - .5 && actionBounds.Right <= captionBounds.Left + .5, "tabs, window actions and caption buttons do not overlap");
            Check(AutomationProperties.GetName(caption.MinimizeButton) == "Свернуть" && AutomationProperties.GetName(caption.CloseButton) == "Закрыть", "caption buttons expose accessible names");
            caption.CloseButton.SimulatedHover = true; caption.CloseButton.UpdateVisual(); Pump();
            Check(caption.CloseButton.Background is SolidColorBrush { Color: { R: 0xC4, G: 0x2B, B: 0x1C } }, "Close hover uses the Windows 11 red");
            evidence.Capture(root, "caption-close-hover", new { theme = "Light", fixtureOnly = true, state = "Close hover (software state)" });
            caption.CloseButton.SimulatedHover = false; caption.CloseButton.UpdateVisual();
            foreach (var b in new[] { caption.MinimizeButton, caption.MaximizeButton, caption.CloseButton }) { b.WindowActive = false; b.UpdateVisual(); }
            Pump(); evidence.Capture(root, "caption-inactive", new { theme = "Light", fixtureOnly = true, state = "inactive glyph colour (software state)" });
            Theme.Apply("Dark"); Pump(); evidence.Capture(root, "caption-dark", new { theme = "Dark", fixtureOnly = true }); Theme.Apply("Light"); Pump();
        }
        finally { CloseMain(main); }
    }

    static void AddressBar(AppStore store, CiVisualEvidence evidence)
    {
        var main = NewMain();
        try
        {
            var profile = new ServerProfile { Name = "Адрес · стенд", Host = "127.0.0.1", Username = "fixture" }; store.Profiles.Add(profile);
            var session = new ServerSession(profile, store); var operations = new FileInteractionTests.Operations();
            var files = new FileBrowserView(session, main, operations); ((TreeView)Field(files, "tree")).Items.Clear();
            Complete(files.InitializeAsync("/srv/fixture", CancellationToken.None));
            var view = new SessionView(session, main, files, new MonitorView(new MonitorViewModel())); view.SetActive(false);
            typeof(MainWindow).GetMethod("AddSessionTab", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(main, [profile, session, view]); view.ShowSection("files"); Layout(main);
            var address = (TextBox)Field(files, "address"); var crumbs = Descendants<Border>(files.AddressBar).Single(b => b.Name == "AddressCrumbs");
            Check(!address.IsVisible && files.CurrentPath == "/srv/fixture", "initial state shows breadcrumbs, not the text field");
            using (var pointer = new CiSoftwarePointer(main))
            {
                // Free area right of the crumbs: beta.5 lost this click inside the ScrollViewer.
                pointer.MoveTo(crumbs, new Point(crumbs.ActualWidth - 12, crumbs.ActualHeight / 2)); pointer.Down(); pointer.Up(); Pump(); Pump();
            }
            Check(address.IsVisible && address.IsKeyboardFocused, "an OS mouse click on the free crumb area switches to text entry with keyboard focus");
            Check(address.SelectionStart == 0 && address.SelectionLength == address.Text.Length && address.Text == "/srv/fixture", "the confirmed path is selected for replacement");
            evidence.Capture((FrameworkElement)main.Content, "address-editing", new { theme = "Light", fixtureOnly = true });
            var keyboard = new CiSoftwareKeyboard(main);
            keyboard.Type("/missing папка"); keyboard.Press(Key.Enter); WaitFor(() => !Equals(Field(files, "addressPending"), true));
            Check(address.IsVisible && address.Text == "/missing папка" && Ui.GetHasValidationError(address), "a missing folder keeps the typed Unicode path and shows an error for correction");
            evidence.Capture((FrameworkElement)main.Content, "address-error", new { theme = "Light", fixtureOnly = true, typed = "/missing папка" });
            keyboard.Press(Key.A, ModifierKeys.Control); keyboard.Type("/correct"); keyboard.Press(Key.Enter); WaitFor(() => files.CurrentPath == "/correct");
            Check(files.CurrentPath == "/correct" && !address.IsVisible, "Ctrl+A, new text and Enter navigate to the corrected folder and restore breadcrumbs");
            files.Focus(); var table = (DataGrid)Field(files, "table"); table.Focus(); Pump();
            keyboard.Press(Key.L, ModifierKeys.Control); Pump();
            Check(address.IsVisible && address.IsKeyboardFocused, "Ctrl+L from the file table enters address editing");
            keyboard.Press(Key.Escape); Pump();
            Check(!address.IsVisible && files.CurrentPath == "/correct" && address.Text == "/correct", "Escape returns to the last confirmed path");
            var search = Descendants<TextBox>(files.AddressBar).First(t => !ReferenceEquals(t, address)); search.Focus(); Pump();
            keyboard.Press(Key.D, ModifierKeys.Alt); Pump();
            Check(address.IsVisible && address.IsKeyboardFocused, "Alt+D from the search box enters address editing");
            keyboard.Press(Key.Escape);
            var root = Descendants<Button>(files.AddressBar).First(b => AutomationProperties.GetName(b) == "Корень");
            using (var pointer = new CiSoftwarePointer(main)) { pointer.MoveTo(root, new Point(root.ActualWidth / 2, root.ActualHeight / 2)); pointer.Down(); pointer.Up(); }
            WaitFor(() => !Equals(Field(files, "addressPending"), true)); Pump();
            Check(!address.IsVisible || !address.IsKeyboardFocused, "a breadcrumb click navigates instead of entering text mode");
        }
        finally { CloseMain(main); }
    }

    static void Sorting(AppStore store, CiVisualEvidence evidence)
    {
        var main = NewMain();
        try
        {
            store.Profiles.Clear();
            foreach (var name in new[] { "file10", "file2", "File1" }) store.Profiles.Add(new ServerProfile { Name = name, Host = name + ".example.invalid", Username = "fixture" });
            Invoke(main, "RefreshHome"); Layout(main);
            var servers = (DataGrid)Field(main, "servers");
            string[] Names() => servers.Items.Cast<ServerProfileViewModel>().Select(p => p.Name).ToArray();
            ClickHeader(main, servers, "Название"); // default is Name ascending → first click toggles to descending
            var afterFirst = Names(); ClickHeader(main, servers, "Название"); var afterSecond = Names();
            var ascending = afterFirst[0] == "File1" ? afterFirst : afterSecond; var descending = ReferenceEquals(ascending, afterFirst) ? afterSecond : afterFirst;
            Check(ascending.SequenceEqual(["File1", "file2", "file10"]) && descending.SequenceEqual(["file10", "file2", "File1"]), "Home: header clicks give natural file2 < file10 order in both directions");
            Check(servers.Columns.Single(c => Equals(c.Header, "Название")).SortDirection is not null, "Home: the sorted column shows its direction indicator");
            Invoke(main, "RefreshHome"); Pump();
            Check(Names().SequenceEqual(TableSorting.CurrentDirection(servers) == System.ComponentModel.ListSortDirection.Ascending ? ascending : descending), "Home: refresh keeps the chosen order");
            Layout(main); var homeColumns = servers.Columns.Sum(c => c.ActualWidth);
            Check(homeColumns >= servers.ActualWidth - 24, $"Home: columns fill the table ({homeColumns:0} of {servers.ActualWidth:0} DIP), no empty filler header");
            // 0.3 A4: groups on Home — ordered by name, «Без группы» last, collapsible, flat again without groups.
            Check(CollectionViewSource.GetDefaultView(servers.ItemsSource).GroupDescriptions.Count == 0 && !Descendants<GroupItem>(servers).Any(), "Home without groups stays a flat list");
            store.Profiles.Single(p => p.Name == "file2").Group = "Тест"; store.Profiles.Single(p => p.Name == "file10").Group = "Прод"; store.Profiles.Add(new ServerProfile { Name = "file3", Host = "file3.example.invalid", Username = "fixture", Group = "прод" });
            Invoke(main, "RefreshHome"); Layout(main);
            var groups = CollectionViewSource.GetDefaultView(servers.ItemsSource).Groups!.Cast<CollectionViewGroup>().Select(g => $"{g.Name}:{g.ItemCount}").ToArray();
            Check(groups.SequenceEqual(["Прод:2", "Тест:1", "Без группы:1"]), "Home groups are ordered by name with «Без группы» last: " + string.Join(", ", groups));
            var headers = Descendants<GroupItem>(servers).Select(g => Descendants<Expander>(g).First()).ToArray();
            var headerTexts = headers.Select(h => h.Header?.ToString() ?? "").ToArray();
            Check(headers.Length == groups.Length && headerTexts.SequenceEqual(["Прод · 2", "Тест · 1", "Без группы · 1"]) && Descendants<TextBlock>(headers[0]).Any(t => t.Text == "Прод · 2" && t.IsVisible), $"group headers show the name and the number of servers ({headers.Length}/{groups.Length}: {string.Join(" | ", headerTexts)})");
            var test = headers.Single(h => (h.Header as string)?.StartsWith("Тест", StringComparison.Ordinal) == true); test.IsExpanded = false; Pump();
            Invoke(main, "RefreshHome"); Layout(main);
            var testAgain = Descendants<GroupItem>(servers).Select(g => Descendants<Expander>(g).First()).Single(h => (h.Header as string)?.StartsWith("Тест", StringComparison.Ordinal) == true);
            Check(!testAgain.IsExpanded && Descendants<DataGridRow>(testAgain).All(r => !r.IsVisible), "a collapsed group stays collapsed after a refresh");
            evidence.Capture((FrameworkElement)main.Content, "home-groups", new { theme = "Light", fixtureOnly = true });
            testAgain.IsExpanded = true; foreach (var p in store.Profiles) p.Group = ""; store.Profiles.RemoveAll(p => p.Name == "file3");
            Invoke(main, "RefreshHome"); Layout(main);
            Check(CollectionViewSource.GetDefaultView(servers.ItemsSource).GroupDescriptions.Count == 0, "removing every group returns Home to a flat list");
            // Submenu headers (no own command) must align with ordinary items: the Fluent template collapses the icon column without an Icon.
            Invoke(main, "AddMenu"); Pump(); Layout(main);
            var add = OpenMenu(); CheckSubmenuAlignment(add, "«+» (no checkable items)"); evidence.Capture(add, "menu-add-alignment", new { theme = "Light", fixtureOnly = true }); add.IsOpen = false; Pump();
            var (viewTab, _) = AddTab(main, store, "Меню вида"); Pump(); Layout(main);
            var viewButton = Descendants<Button>((FrameworkElement)viewTab.Content).First(b => b.IsVisible && Descendants<TextBlock>(b).Any(t => t.Text == "Просмотреть"));
            viewButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump(); Layout(main);
            var view = OpenMenu(); CheckSubmenuAlignment(view, "«Просмотреть» (checkable items)"); evidence.Capture(view, "menu-view-alignment", new { theme = "Light", fixtureOnly = true }); view.IsOpen = false; Pump();
            evidence.Capture((FrameworkElement)main.Content, "sort-home", new { theme = "Light", fixtureOnly = true });

            var model = new MonitorViewModel(); var monitor = new MonitorView(model);
            var window = new Window { Width = 900, Height = 700, Content = monitor, ShowInTaskbar = false, Left = 60, Top = 60 }; window.Show(); monitor.SetPage(MonitorPage.Performance); Layout(window);
            try
            {
                model.Networks.Add(new NetworkRow("eth0", "1 МБ/с", "0 Б/с", ReceiveRate: 1024 * 1024, SendRate: 0));
                model.Networks.Add(new NetworkRow("eth1", "900 КБ/с", "—", ReceiveRate: 900 * 1024, SendRate: null));
                model.Networks.Add(new NetworkRow("wg0", "—", "10 Б/с", ReceiveRate: null, SendRate: 10));
                var networks = (DataGrid)Field(monitor, "networks"); Layout(window);
                string[] Order() => networks.Items.Cast<NetworkRow>().Select(r => r.Name).ToArray();
                ClickHeader(window, networks, "Получение"); var first = Order(); ClickHeader(window, networks, "Получение"); var second = Order();
                var up = first[0] == "eth1" ? first : second; var down = ReferenceEquals(up, first) ? second : first;
                Check(up.SequenceEqual(["eth1", "eth0", "wg0"]) && down.SequenceEqual(["eth0", "eth1", "wg0"]), "Network: 900 КБ/с < 1 МБ/с by bytes/s, unknown last in both directions");
                model.Networks[1] = model.Networks[1] with { Receive = "950 КБ/с", ReceiveRate = 950 * 1024 }; Pump();
                Check(Order().Last() == "wg0", "Network: an update keeps unknown values at the end");
                Layout(window); var networkColumnsWidth = networks.Columns.Sum(c => c.ActualWidth); var disks = (DataGrid)Field(monitor, "disks");
                Check(networkColumnsWidth >= networks.ActualWidth - 24 && disks.Items.Count == 0 && Descendants<TextBlock>((DependencyObject)VisualTreeHelper.GetParent(disks)).Any(t => t.IsVisible && t.Text.StartsWith("Нет данных об устройствах", StringComparison.Ordinal)), $"performance tables fill their width ({networkColumnsWidth:0} of {networks.ActualWidth:0} DIP) and an empty disk table explains itself");
                evidence.Capture(monitor, "sort-network", new { theme = "Light", fixtureOnly = true });
                // 0.3 N1: listening ports under Network — loaded through the session executor, searchable, honest about hidden processes.
                var ports = monitor.Ports!;
                var portsOutput = string.Join('\n', "#tool ss",
                    "tcp   LISTEN 0 4096 0.0.0.0:22 0.0.0.0:* users:((\"sshd\",pid=900,fd=3))",
                    "tcp   LISTEN 0 128 127.0.0.1:5432 0.0.0.0:* users:((\"postgres\",pid=1200,fd=5))",
                    "tcp   LISTEN 0 511 [::]:8080 [::]:*",
                    "udp   UNCONN 0 0 127.0.0.53%lo:53 0.0.0.0:* users:((\"systemd-resolve\",pid=600,fd=13))",
                    "#ps", "900 root", "1200 postgres", "600 systemd-resolve", "#uid", "1000");
                string? sent = null;
                ports.Execute = (command, _) => { sent = command; return Task.FromResult(new CommandResult(portsOutput, "", 0)); };
                Complete(ports.LoadAsync()); Layout(window);
                var portsSearch = Descendants<TextBox>(ports).Single(b => b.Name == "PortsSearch"); var portsState = Descendants<TextBlock>(ports).Single(b => b.Name == "PortsState");
                Check(sent == ListeningPorts.Command && ports.Table.Items.Count == 4 && portsState.Text.Contains("скрыты", StringComparison.Ordinal), $"ports load all listening sockets and say that other users' processes are hidden ({ports.Table.Items.Count}: {portsState.Text})");
                portsSearch.Text = "postgres"; Pump();
                Check(ports.Table.Items.Count == 1 && ((ListeningPort)ports.Table.Items[0]).Port == 5432 && portsState.Text.StartsWith("Найдено 1 из 4", StringComparison.Ordinal), "searching a process name finds its port");
                portsSearch.Text = "8080"; Pump();
                Check(ports.Table.Items.Count == 1 && ((ListeningPort)ports.Table.Items[0]).Process is null, "searching a port finds a socket even without a visible process");
                portsSearch.Text = ""; Pump(); Layout(window);
                var portsColumns = ports.Table.Columns.Sum(c => c.ActualWidth);
                Check(portsColumns >= ports.Table.ActualWidth - 24, $"ports table fills its width ({portsColumns:0} of {ports.Table.ActualWidth:0} DIP)");
                ports.BringIntoView(); Layout(window); evidence.Capture(monitor, "ports", new { theme = "Light", fixtureOnly = true });
            }
            finally { window.Close(); }
        }
        finally { CloseMain(main); }
    }

    static void Graphs(CiVisualEvidence evidence)
    {
        var now = DateTimeOffset.UnixEpoch.AddHours(10); int call = 0;
        string Sample(int n) => "SSHXMON1\n" + string.Concat(new Dictionary<string, string>
        {
            ["sys/stat"] = $"cpu {100 + 25 * n + n * n % 7} 0 {100 + 50 * n} {800 + 100 * n} 0 0 0 0\ncpu0 1 0 1 1\n", ["sys/meminfo"] = "MemTotal: 1000 kB\nMemAvailable: 600 kB\n",
            ["sys/uptime"] = $"{100 + n * 3} 0\n", ["sys/boot_id"] = "b\n", ["meta/count"] = "0", ["meta/limited"] = "0",
        }.Select(p => p.Key + "\t" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(p.Value)) + "\n")) + "END\n";
        var monitor = new LinuxMonitor((_, _) => { call++; now = now.AddMilliseconds(2800); if (call == 9) throw new IOException("channel lost"); return Task.FromResult(new ExecResult(Sample(call), "", 0)); }, clock: () => now) { ExpectedInterval = TimeSpan.FromSeconds(2) };
        for (int i = 0; i < 16; i++) { try { monitor.SampleAsync().GetAwaiter().GetResult(); } catch (IOException) { } now = now.AddMilliseconds(250); }
        var history = monitor.History; var segments = MetricGraph.BuildSegments(history, GraphMetric.Cpu, now, TimeSpan.FromMinutes(1), monitor.ContinuityLimit, new Size(600, 160));
        Check(segments.Count == 2 && segments.All(s => s.Length >= 6), $"slow (2.8 s) successful samples form lines; the one real failure splits them ({segments.Count} segments)");
        var graph = new MetricGraph { Width = 640, Height = 180, History = history, Now = now, Window = TimeSpan.FromMinutes(1), GapThreshold = monitor.ContinuityLimit };
        var host = new Border { Padding = new Thickness(16), Child = graph }; host.SetResourceReference(Border.BackgroundProperty, "Paper");
        var window = new Window { SizeToContent = SizeToContent.WidthAndHeight, Content = host, ShowInTaskbar = false, Left = 60, Top = 60 }; window.Show(); Layout(window);
        try
        {
            evidence.Capture(host, "graph-slow-with-failure", new { theme = "Light", fixtureOnly = true, collectionSeconds = 2.8, intervalSeconds = 2, segments = segments.Count, points = history.Count(p => !p.IsGap) });
            using (var pointer = new CiSoftwarePointer(window)) pointer.MoveTo(graph, new Point(graph.ActualWidth * .55, graph.ActualHeight / 2));
            Pump(); Check(graph.Inspected is not null, "hovering the graph inspects the nearest real sample");
            evidence.Capture(host, "graph-hover", new { theme = "Light", fixtureOnly = true, inspected = graph.Inspected?.Timestamp });
        }
        finally { window.Close(); }
    }

    static void Windows(AppStore store, CiVisualEvidence evidence)
    {
        var main = NewMain();
        try
        {
            // The real "Сервер" navigation button (beta.6 crash: the section key was not accepted).
            var (serverTab, _) = AddTab(main, store, "Навигация по разделам"); Pump(); Layout(main);
            var sessionView = (SessionView)serverTab.Content; var navigation = (Dictionary<string, Button>)Field(sessionView, "navigationButtons");
            navigation["server"].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump(); Layout(main);
            Check(Descendants<ServerPanelView>(sessionView).Any(p => p.IsVisible), "clicking the actual «Сервер» navigation button shows the server panel without a crash");
            navigation["files"].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump();
            navigation["server"].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump(); Layout(main);
            Check(Descendants<ServerPanelView>(sessionView).Count(p => p.IsVisible) == 1, "returning to «Сервер» reuses its single panel");
            // 0.3 C2 crash: «Службы» had a navigation button but no accepted section key. Every button must be a known section.
            var unknown = navigation.Keys.Where(k => k is not ("processes" or "performance") && !SSHExplorer.App.ViewModels.SessionViewModel.IsSection(k)).ToArray();
            Check(unknown.Length == 0, "every navigation button opens a known section: " + string.Join(",", unknown));
            var servicesErrors = new List<string>(); Ui.ErrorOverride = servicesErrors.Add;
            string? servicesCrash = null;
            try { navigation["services"].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump(); Layout(main); } catch (Exception ex) { servicesCrash = ex.GetType().Name + ": " + ex.Message; }
            var servicesInTab = Descendants<ServicesView>(sessionView).SingleOrDefault();
            for (int i = 0; i < 50 && servicesInTab is not null && !Descendants<TextBlock>(servicesInTab).Any(b => b.Name == "ServicesProblem"); i++) Pump();
            Check(servicesCrash is null && servicesInTab is { IsVisible: true } && Descendants<TextBlock>(servicesInTab).Any(b => b.Name == "ServicesProblem" && b.IsVisible),
                "clicking the actual «Службы» button opens the page and explains an unconnected session in place" + (servicesCrash is null ? "" : ": " + servicesCrash));
            Check(Ui.GetIsNavigationActive(navigation["services"]) && navigation["processes"].IsVisible, "«Службы» is highlighted and the task manager pages stay visible around it");
            Ui.ErrorOverride = null; Check(servicesErrors.Count == 0, "opening «Службы» raised no error dialog" + (servicesErrors.Count > 0 ? ": " + string.Join(" | ", servicesErrors) : ""));
            navigation["files"].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump();
            // 0.3 G: the Docker module appears only when enabled, at once in open tabs; an unconnected session explains itself in place.
            App.Store.Settings.DockerModule = false; App.RaiseModulesChanged(); Pump();
            Check(!navigation["docker"].IsVisible, "Docker is hidden while the module is off");
            var tabErrors = new List<string>(); Ui.ErrorOverride = tabErrors.Add;
            App.Store.Settings.DockerModule = true; App.RaiseModulesChanged(); Pump(); Layout(main);
            Check(navigation["docker"].IsVisible, "enabling the module shows Docker in an already open tab");
            navigation["docker"].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump(); Layout(main);
            var dockerInTab = Descendants<DockerView>(sessionView).Single();
            for (int i = 0; i < 50 && !Descendants<TextBlock>(dockerInTab).Any(b => b.Name == "DockerProblem"); i++) Pump();
            Check(Descendants<TextBlock>(dockerInTab).Any(b => b.Name == "DockerProblem" && b.IsVisible && b.Text.StartsWith("Не удалось проверить Docker", StringComparison.Ordinal)), "opening Docker on an unconnected session explains the problem in place, without a dialog");
            App.Store.Settings.DockerModule = false; App.RaiseModulesChanged(); Pump();
            Check(!navigation["docker"].IsVisible && !Descendants<DockerView>(sessionView).Any(v => v.IsVisible), "disabling the module hides Docker and leaves its section");
            Ui.ErrorOverride = null; Check(tabErrors.Count == 0, "opening Docker in a tab raised no error dialog" + (tabErrors.Count > 0 ? ": " + string.Join(" | ", tabErrors) : ""));
            DockerUiChecks.Run(store, evidence, Check);
            // Modules sit after the task manager and its pages, never between them.
            var side = (Panel)VisualTreeHelper.GetParent(navigation["docker"]); var monitorPages = (Panel)VisualTreeHelper.GetParent(navigation["processes"]);
            Check(side.Children.IndexOf(navigation["docker"]) > side.Children.IndexOf(monitorPages) && side.Children.IndexOf(navigation["scripts"]) > side.Children.IndexOf(navigation["docker"]), "Docker and «Скрипты» come after the task manager pages");
            var scriptErrors = new List<string>(); Ui.ErrorOverride = scriptErrors.Add;
            App.Store.Settings.ScriptsModule = true; App.RaiseModulesChanged(); Pump(); Layout(main);
            navigation["scripts"].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump(); Layout(main);
            Check(navigation["scripts"].IsVisible && Descendants<ScriptsView>(sessionView).Any(v => v.IsVisible), "enabling «Скрипты» shows its section in an open tab");
            App.Store.Settings.ScriptsModule = false; App.RaiseModulesChanged(); Pump(); Ui.ErrorOverride = null;
            Check(!navigation["scripts"].IsVisible && scriptErrors.Count == 0, "disabling «Скрипты» hides it without errors: " + string.Join(" | ", scriptErrors));
            SidebarUiChecks.Run(sessionView, navigation, evidence, Check);
            ExchangeUiChecks.Run(main, store, evidence, Check);
            ScriptsUiChecks.Run(store, evidence, Check);
            CompareUiChecks.Run(store, evidence, Check);
            GroupCUiChecks.Run(store, evidence, Check);
            GroupDUiChecks.Run(store, evidence, Check);
            FirewallUiChecks.Run(store, evidence, Check);
            SecurityUiChecks.Run(store, evidence, Check);
            GroupRunUiChecks.Run(store, evidence, Check);
            SnippetUiChecks.Run(evidence, Check);
            // Middle click (press and release on the same tab) closes a server tab.
            var (middleTab, _) = AddTab(main, store, "Закрытие средней кнопкой"); Pump(); Layout(main);
            var allTabs = (TabControl)Field(main, "tabs");
            middleTab.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Middle) { RoutedEvent = UIElement.PreviewMouseDownEvent });
            middleTab.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Middle) { RoutedEvent = UIElement.PreviewMouseUpEvent });
            for (int i = 0; i < 50 && allTabs.Items.Contains(middleTab); i++) Pump();
            Check(!allTabs.Items.Contains(middleTab), "middle click closes the server tab");
            // 0.3 A1: import from an OpenSSH config — preview, duplicate unchecked, only the checked host is saved.
            var existingProfile = store.Profiles.First();
            var importText = string.Join("\n", "Host imported-web", " HostName 10.9.0.5", " User deploy", " ProxyJump bastion", " ProxyCommand nc %h %p",
                "Host dup-alias", $" HostName {existingProfile.Host}", $" User {existingProfile.Username}", $" Port {existingProfile.Port}", "Match exec true", " User x");
            var importConfig = SshConfig.Parse(importText, @"C:\fixture\.ssh", "config");
            var importCandidates = ProfileImport.FromSshConfig(importConfig, "fixture", @"C:\fixture", store.Profiles, _ => false);
            int profilesBefore = store.Profiles.Count;
            var importTask = main.ImportProfilesAsync(@"C:\fixture\.ssh\config", importCandidates, importConfig.Warnings, window =>
            {
                window.Show(); Layout(window);
                var rows = window.Rows.ToDictionary(r => r.Name);
                Check(rows["imported-web"].Selected && !rows["dup-alias"].Selected && rows["dup-alias"].Notes.Contains(existingProfile.Name, StringComparison.Ordinal), "import preview checks the new host and leaves the duplicate of an existing profile unchecked");
                Check(window.ImportButton.IsEnabled && Equals(window.ImportButton.Content, "Импортировать (1)") && rows["imported-web"].Notes.Contains("ProxyCommand", StringComparison.Ordinal) && Descendants<TextBlock>(window).Any(t => t.Name == "ImportWarnings" && t.Text.Contains("Match", StringComparison.Ordinal)), "import names its count, reports ignored directives and unapplied Match blocks");
                var importColumns = window.Table.Columns.Sum(c => c.ActualWidth);
                Check(importColumns >= window.Table.ActualWidth - 24, $"import table fills its width ({importColumns:0} of {window.Table.ActualWidth:0} DIP)");
                evidence.Capture((FrameworkElement)window.Content, "import-ssh-config", new { theme = "Light", fixtureOnly = true });
                window.Close(); return true;
            });
            Complete(importTask);
            // 0.3 A5: real HKCU read of a PuTTY session (CI runner only; the unique key is removed afterwards).
            var puttyKey = @"Software\SimonTatham\PuTTY\Sessions\SSHX%20CI%20" + Guid.NewGuid().ToString("N")[..8];
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(puttyKey)!) { key.SetValue("HostName", "putty-ci.example"); key.SetValue("PortNumber", 2201, Microsoft.Win32.RegistryValueKind.DWord); key.SetValue("UserName", "ci"); key.SetValue("Protocol", "ssh"); }
                var read = MainWindow.RegistrySessions(@"Software\SimonTatham\PuTTY\Sessions");
                var (puttyCandidates, _) = ExternalSessionImport.FromPutty(read, "fixture", store.Profiles);
                Check(puttyCandidates.Any(c => c.Profile is { Host: "putty-ci.example", Port: 2201, Username: "ci" } && c.Profile.Name.StartsWith("SSHX CI ", StringComparison.Ordinal)), "PuTTY sessions are read from the real HKCU registry with decoded names and DWORD ports");
            }
            finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(puttyKey, throwOnMissingSubKey: false); }
            var imported = store.Profiles.SingleOrDefault(p => p.Name == "imported-web");
            Check(importTask.Result == 1 && store.Profiles.Count == profilesBefore + 1 && imported is { Host: "10.9.0.5", Username: "deploy", ProxyJump: "bastion", Authentication: AuthenticationKind.Password }, "import saves exactly the checked host with its jump host and no secret");
            var settings = new SettingsWindow { ShowInTaskbar = false, Left = 80, Top = 80 }; settings.Show(); Layout(settings);
            try
            {
                var pageHost = (FrameworkElement)Field(settings, "page"); bool transitions = true;
                foreach (var section in SettingsWindow.Sections) { settings.Show(section.Key); transitions &= !UiMotion.Enabled || section.Key == "general" || UiMotion.ActiveClockCount(pageHost) > 0; UiMotion.Stop(pageHost); Layout(settings); evidence.Capture((FrameworkElement)settings.Content, "settings-" + section.Key, new { theme = "Light", fixtureOnly = true }); }
                Check(transitions && pageHost.Opacity == 1 && UiMotion.ActiveClockCount(pageHost) == 0, $"settings section change runs the shared surface transition (motion {(UiMotion.Enabled ? "on" : "off on this runner")}) and finishes at its final state");
                var theme = Descendants<ComboBox>(settings).FirstOrDefault(c => c.Name == "SettingsTheme");
                settings.Show("general"); Layout(settings);
                theme = Descendants<ComboBox>(settings).Single(c => c.Name == "SettingsTheme");
                var label = Descendants<TextBlock>((DependencyObject)VisualTreeHelper.GetParent(theme)).FirstOrDefault(t => t.Text == "Тема приложения");
                Check(label is not null, "«Тема приложения» label sits in the same group directly above its list");
                settings.Show("monitoring"); Layout(settings); var poll = Descendants<NumberField>(settings).Single(f => f.Label == "Интервал обновления"); poll.Input.Text = "0";
                var save = Descendants<Button>(settings).Single(b => b.Name == "SettingsSave"); save.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump();
                Check(settings.IsVisible && settings.CurrentSection == "monitoring" && !poll.Validate(), "an out-of-range interval keeps the window open on its section with an error");
                evidence.Capture((FrameworkElement)settings.Content, "settings-validation", new { theme = "Light", fixtureOnly = true });
            }
            finally { settings.Close(); }

            var transfers = new TransfersWindow { ShowInTaskbar = false, Left = 80, Top = 80 }; transfers.Show(); Layout(transfers);
            try
            {
                Check(transfers.EmptyState.IsVisible && !transfers.CancelButton.IsEnabled && !transfers.RetryButton.IsEnabled, "empty queue: short explanation, cancel and retry unavailable");
                evidence.Capture((FrameworkElement)transfers.Content, "transfers-empty", new { theme = "Light", fixtureOnly = true });
                var profile = new ServerProfile { Name = "web-01", Host = "web.example.invalid", Username = "deploy" }; var session = new ServerSession(profile, store);
                var running = Job("release.tar.gz", TransferKind.Upload, TransferState.Running, session, 400 * 1024 * 1024, 1024L * 1024 * 1024, null);
                var failed = Job("logs", TransferKind.Download, TransferState.Failed, session, 10, null, "Отказано в доступе: /var/log/secure");
                var done = Job("config.yml", TransferKind.ServerCopy, TransferState.Completed, session, 2048, 2048, null);
                transfers.RefreshRows(); Layout(transfers);
                transfers.Table.SelectedItem = transfers.Model.Rows.Single(r => r.Job == failed); Pump();
                Check(transfers.RetryButton.IsEnabled && !transfers.CancelButton.IsEnabled && transfers.Details.IsVisible, "failed transfer: retry available, cancel not, details visible");
                transfers.Table.SelectedItem = transfers.Model.Rows.Single(r => r.Job == running); Pump();
                Check(transfers.CancelButton.IsEnabled && !transfers.RetryButton.IsEnabled, "running transfer: cancel available, retry not");
                transfers.RefreshRows(); Pump();
                Check(ReferenceEquals((transfers.Table.SelectedItem as TransferRow)?.Job, running), "refresh keeps the selected transfer");
                Check(transfers.Model.Rows.Single(r => r.Job == done).Remaining == "—" && transfers.Model.Rows.Single(r => r.Job == failed).Speed == "—", "finished rows show unknown time/speed as «—», never zero");
                Layout(transfers);
                var runningBar = Descendants<ProgressBar>(transfers.Table).Single(b => ReferenceEquals((b.DataContext as TransferRow)?.Job, running)); var runningCell = Descendants<DataGridCell>(transfers.Table).First(c => c.IsAncestorOf(runningBar));
                Check(runningBar.ActualWidth >= runningCell.ActualWidth * .4 && Math.Abs(runningBar.Value - 39.0625) < .01, $"running transfer progress bar fills its cell ({runningBar.ActualWidth:0} of {runningCell.ActualWidth:0} DIP) at its real value");
                Check(TransferRow.FormatDuration(TimeSpan.FromMilliseconds(300)) == "< 1 с" && TransferRow.FormatDuration(TimeSpan.Zero) == "0 с", "sub-second remaining time reads «< 1 с», never a misleading zero");
                evidence.Capture((FrameworkElement)transfers.Content, "transfers-states", new { theme = "Light", fixtureOnly = true });
            }
            finally { transfers.Close(); ClearJobs(); }

            var serverProfile = new ServerProfile { Name = "Боевой сервер", Host = "10.0.0.5", Username = "root" }; var serverSession = new ServerSession(serverProfile, store);
            var panel = new ServerPanelView(serverSession, null, null, () => (1, 2));
            var info = ServerInfoCollector.Parse(ServerInfoFixture, DateTimeOffset.UtcNow);
            typeof(ServerPanelView).GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(ServerInfo)])!.Invoke(panel, [info]);
            typeof(ServerPanelView).GetProperty(nameof(ServerPanelView.Info))!.SetValue(panel, info);
            var host = new Window { Width = 1100, Height = 760, Content = panel, ShowInTaskbar = false, Left = 60, Top = 60 }; host.Show(); Layout(host);
            try
            {
                Check(Descendants<TextBox>(panel).Any(t => t.Text == "Ubuntu 24.04 LTS") && Descendants<TextBox>(panel).Any(t => t.Text.Contains("Доступна: systemctl reboot", StringComparison.Ordinal)), "server panel shows OS and the single permitted power method");
                var grid = Descendants<CardGridPanel>(panel).Single(); var cardBounds = grid.Children.OfType<Border>().Select(c => c.TransformToAncestor(grid).TransformBounds(new Rect(c.RenderSize))).ToArray();
                Check(grid.Columns >= 3 && Math.Abs(cardBounds.Max(b => b.Right) - grid.ActualWidth) < 1, $"server cards fill the panel width in {grid.Columns} columns without a right gutter");
                var factRows = Descendants<Grid>(grid).Where(r => r.ColumnDefinitions.Count == 2 && r.Children.Count == 2 && r.Children[0] is TextBlock && r.Children[1] is TextBox).ToArray();
                var gaps = factRows.Select(r =>
                {
                    var caption = (TextBlock)r.Children[0]; var value = (FrameworkElement)r.Children[1];
                    // The caption's layout slot (column minus its margin), not its unclipped text extent.
                    var captionRight = LayoutInformation.GetLayoutSlot(caption).Right - caption.Margin.Right; var valueLeft = LayoutInformation.GetLayoutSlot(value).Left;
                    return (caption.Text, Gap: valueLeft - captionRight, Fits: caption.DesiredSize.Width <= LayoutInformation.GetLayoutSlot(caption).Width + .5);
                }).ToArray();
                var worst = gaps.OrderBy(g => g.Gap).FirstOrDefault();
                var ipv6 = Descendants<TextBox>(grid).Single(t => t.Text.StartsWith("fe80::", StringComparison.Ordinal));
                Check(ipv6.Parent is Grid { Name: "StackedFact" } && ipv6.LineCount == 1, "a long IPv6 address sits under its label on one line instead of splitting mid-token");
                Check(factRows.Length >= 10 && gaps.All(g => g.Gap >= 8 && g.Fits), $"every server fact label fits its column and ends at least 8 DIP before its value ({factRows.Length} rows; smallest gap {worst.Gap:0} at «{worst.Text}»; overflowing: {string.Join(", ", gaps.Where(g => !g.Fits).Select(g => g.Text))})");
                evidence.Capture(panel, "server-panel", new { theme = "Light", fixtureOnly = true, note = "fixture facts; no power command sent" });
                Theme.Apply("Dark"); Pump(); evidence.Capture(panel, "server-panel-dark", new { theme = "Dark", fixtureOnly = true }); Theme.Apply("Light"); Pump();
            }
            finally { host.Close(); }
        }
        finally { CloseMain(main); }
    }

    static TransferJob Job(string name, TransferKind kind, TransferState state, ServerSession session, long bytes, long? total, string? error)
    {
        var job = new TransferJob();
        void Set(string property, object? value) => typeof(TransferJob).GetProperty(property)!.SetValue(job, value);
        Set(nameof(TransferJob.Name), name); Set(nameof(TransferJob.Kind), kind); Set(nameof(TransferJob.State), state); Set(nameof(TransferJob.TotalBytes), total); Set(nameof(TransferJob.Error), error);
        Set(nameof(TransferJob.Source), kind == TransferKind.Upload ? @"C:\Users\Fixture\" + name : "/srv/" + name); Set(nameof(TransferJob.Destination), kind == TransferKind.Download ? @"C:\Users\Fixture\Downloads" : "/srv/releases");
        typeof(TransferJob).GetField("Sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(job, new[] { session });
        typeof(TransferJob).GetField("_bytes", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(job, bytes);
        if (state == TransferState.Failed) typeof(TransferJob).GetField("RetryAction", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(job, (Func<Task>)(() => Task.CompletedTask));
        if (state == TransferState.Running) ((System.Diagnostics.Stopwatch)typeof(TransferJob).GetField("_watch", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(job)!).Start();
        var jobs = (List<TransferJob>)typeof(TransferQueue).GetField("_jobs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(App.Transfers)!;
        lock (jobs) jobs.Add(job);
        return job;
    }
    static void ClearJobs() { var jobs = (List<TransferJob>)typeof(TransferQueue).GetField("_jobs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(App.Transfers)!; lock (jobs) jobs.Clear(); }

    static void Icons(CiVisualEvidence evidence)
    {
        foreach (var theme in new[] { "Light", "Dark" })
        {
            Theme.Apply(theme);
            var grid = new WrapPanel { Orientation = Orientation.Horizontal, Width = 1200 };
            void Add(string key, string label)
            {
                var cell = new StackPanel { Width = 140, Margin = new Thickness(6) };
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                foreach (var size in new[] { 16d, 20d, 24d }) { var icon = Ui.Icon(key, size, false); icon.Margin = new Thickness(0, 0, 10, 0); row.Children.Add(icon); }
                cell.Children.Add(row); var text = Ui.Text(label, 12); text.TextWrapping = TextWrapping.Wrap; cell.Children.Add(text); grid.Children.Add(cell);
            }
            foreach (var entry in IconCatalog.SystemGlyphs) Add(entry.Glyph!, entry.Key + " " + entry.Source.Replace("Segoe Fluent Icons ", ""));
            foreach (var entry in IconCatalog.Vectors) Add(IconCatalog.Prefix + entry.Key, entry.Key + " (vector)");
            var appIcon = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 16, 6, 6) };
            foreach (var size in new[] { 16d, 24d, 32d, 48d, 64d }) appIcon.Children.Add(new Image { Source = AppIcon.ImageForSize(size, 1), Width = size, Height = size, Margin = new Thickness(0, 0, 12, 0) });
            var root = new StackPanel { Margin = new Thickness(12) }; root.Children.Add(grid); root.Children.Add(appIcon); root.SetResourceReference(Panel.BackgroundProperty, "Paper");
            TextElement.SetForeground(root, (Brush)root.FindResource("Ink"));
            var window = new Window { SizeToContent = SizeToContent.WidthAndHeight, Content = root, ShowInTaskbar = false, Left = 20, Top = 20 }; window.Show(); Layout(window);
            try { evidence.Capture(root, "icons-" + theme.ToLowerInvariant(), new { theme, fixtureOnly = true, systemGlyphs = IconCatalog.SystemGlyphs.Length, vectors = IconCatalog.Vectors.Length }); }
            finally { window.Close(); }
        }
        Check(IconCatalog.View("Small") != "\uE80B" && new[] { "Huge", "Large", "Medium", "Small", "List", "Details", "Tile", "Content" }.Select(IconCatalog.View).Distinct().Count() == 8, "eight view modes map to eight distinct layout icons (no CallControl phone)");
        Check(AppIcon.Image is System.Windows.Media.Imaging.BitmapSource { PixelWidth: 256 }, "application icon resource loads from the assembly (256 px frame)");
        Theme.Apply("Light");
    }

    static readonly string ServerInfoFixture = BuildInfoFixture();
    static string BuildInfoFixture()
    {
        string F(string key, string? value) => key + "\t" + (value is null ? "!" : Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value))) + "\n";
        return "SSHXINFO1\n" + F("os", "PRETTY_NAME=\"Ubuntu 24.04 LTS\"\nVERSION=\"24.04 LTS (Noble Numbat)\"\nID=ubuntu\n") + F("hostname", "web-01\n") + F("kernel", "6.8.0-45-generic\n")
            + F("cpuinfo", "processor\t: 0\nmodel name\t: AMD EPYC 7B13 64-Core Processor\nprocessor\t: 1\nmodel name\t: AMD EPYC 7B13 64-Core Processor\n")
            + F("meminfo", "MemTotal: 8000000 kB\nMemAvailable: 5200000 kB\nSwapTotal: 2000000 kB\nSwapFree: 1900000 kB\n") + F("uptime", "937215.4 0\n") + F("loadavg", "0.42 0.37 0.30 1/312 4411\n")
            + F("boot_id", "3f0e8a1b-0000-4000-8000-000000000001\n") + F("arch", "x86_64") + F("user", "root") + F("uid", "0") + F("systemd", "1") + F("tools", " systemctl busctl shutdown reboot poweroff ip df")
            + F("addr", "2: eth0    inet 10.0.0.5/24 brd 10.0.0.255 scope global eth0\n2: eth0    inet6 fe80::5054:ff:fe12:3456/64 scope link\n")
            + F("df", "Filesystem 1024-blocks Used Available Capacity Mounted on\n/dev/sda1 61255492 21734880 39504228 36% /\n/dev/sdb1 515928320 371426304 118276352 76% /srv\n") + "END\n";
    }

    static void ClickHeader(Window window, DataGrid grid, string header)
    {
        var target = Descendants<DataGridColumnHeader>(grid).Single(h => Equals(h.Column?.Header, header));
        using var pointer = new CiSoftwarePointer(window);
        pointer.MoveTo(target, new Point(Math.Min(24, target.ActualWidth / 2), target.ActualHeight / 2)); pointer.Down(); pointer.Up(); Pump(); Pump();
    }

    static bool Visible(FrameworkElement element, ScrollViewer viewport)
    {
        if (!element.IsVisible) return false;
        var bounds = element.TransformToAncestor(viewport).TransformBounds(new Rect(element.RenderSize));
        return bounds.Left >= -.5 && bounds.Right <= viewport.ViewportWidth + .5 && bounds.Width > 1;
    }
    static bool HitsInside(Window window, FrameworkElement element)
    {
        var center = element.TransformToAncestor(window).Transform(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
        var hit = (window.InputHitTest(center) as DependencyObject);
        for (var current = hit; current is not null; current = VisualTreeHelper.GetParent(current)) if (ReferenceEquals(current, element)) return true;
        return false;
    }
    static void CloseMain(MainWindow main) { bool closed = false; main.Closed += (_, _) => closed = true; main.Close(); WaitFor(() => closed); }
    static object Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    static ContextMenu OpenMenu() => PresentationSource.CurrentSources.Cast<PresentationSource>().Select(src => src.RootVisual).Where(v => v is not null).SelectMany(v => Descendants<ContextMenu>(v!)).Single(m => m.IsOpen);
    static void CheckSubmenuAlignment(ContextMenu menu, string name)
    {
        double X(FrameworkElement e) => e.TransformToAncestor(menu).Transform(new Point()).X;
        TextBlock HeaderText(MenuItem item) => Descendants<TextBlock>(item).First(t => t.Text == item.Header?.ToString());
        var items = menu.Items.OfType<MenuItem>().Where(i => i.IsVisible && i.Header is string).ToArray();
        var ordinary = items.First(i => !i.HasItems && i.Icon is not null); var submenus = items.Where(i => i.HasItems).ToArray(); var plain = items.First(i => !i.HasItems);
        var bad = submenus.Where(s => Math.Abs(X(HeaderText(s)) - X(HeaderText(plain))) > 2 || s.Icon is FrameworkElement icon && Math.Abs(X(icon) - X((FrameworkElement)ordinary.Icon)) > 2).Select(s => $"{s.Header}: icon {X((FrameworkElement)s.Icon!):0} vs {X((FrameworkElement)ordinary.Icon):0}, text {X(HeaderText(s)):0} vs {X(HeaderText(plain)):0}").ToArray();
        Check(submenus.Length > 0 && bad.Length == 0, $"{name}: submenu header icons and text align with ordinary items ({submenus.Length} submenus{(bad.Length > 0 ? "; " + string.Join("; ", bad) : "")})");
    }
    static void Invoke(object target, string name) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes)!.Invoke(target, null);
    static void Complete(Task task) { WaitFor(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    static void WaitFor(Func<bool> done) { var watch = System.Diagnostics.Stopwatch.StartNew(); while (!done()) { if (watch.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("beta.6 UI state did not settle."); Pump(); } }
    static void Layout(Window window) { window.UpdateLayout(); Pump(); window.UpdateLayout(); Pump(); }
    static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false); Dispatcher.PushFrame(frame); }
    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match; foreach (var nested in Descendants<T>(child)) yield return nested; } }
}
