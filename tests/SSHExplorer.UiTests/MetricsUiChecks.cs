using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SSHExplorer.App;
using SSHExplorer.App.ViewModels;
using SSHExplorer.Core;
using SSHExplorer.Monitoring;

/// <summary>
/// 0.6 in the real windows: «История» with recorded data (charts, zoom, comparison, processes, marks, CSV), the custom
/// metrics editor and templates, history alerts, the settings page, the Home load column, profile and folder options,
/// and «Файлы по SSH» from a remote desktop profile.
/// </summary>
internal static class MetricsUiChecks
{
    public static void Run(MainWindow main, AppStore store, CiVisualEvidence evidence, Action<bool, string> check)
    {
        var profile = new ServerProfile { Name = "История · стенд", Host = "history.example.invalid", Username = "ops", Group = "Мониторинг" };
        var other = new ServerProfile { Name = "История · второй", Host = "history2.example.invalid", Username = "ops" };
        store.Profiles.Add(profile); store.Profiles.Add(other);
        var temp = Path.Combine(Path.GetTempPath(), "sshx-metrics-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
        HistoryWindow? window = null;
        try
        {
            // Three hours of minute samples, an event and the busiest processes; a second server for comparison.
            var metrics = MetricsHub.Store; var now = DateTimeOffset.UtcNow;
            for (int m = 180; m >= 0; m--)
            {
                var at = now - TimeSpan.FromMinutes(m);
                metrics.Append(profile.Id, at, [new(MetricSeries.Cpu, m < 20 ? 85 : 30 + 20 * Math.Sin(m / 10d), "%"), new(MetricSeries.Memory, 40 + m % 7, "%"), new(MetricSeries.NetReceive, 1000 + m * 10, "B/s"), new(MetricSeries.NetSend, 500, "B/s"),
                    new(MetricSeries.FileSystem("/"), 60 + m / 100d, "%"), new(MetricSeries.Custom("queue"), m % 13, "шт")]);
                metrics.Append(other.Id, at, [new(MetricSeries.Cpu, 10, "%"), new(MetricSeries.Memory, 20, "%")]);
            }
            metrics.AddEvent(profile.Id, new MetricEvent(now - TimeSpan.FromMinutes(90), "mark", "релиз 1.2"));
            metrics.AppendProcesses(profile.Id, now - TimeSpan.FromMinutes(60), [new ProcessTopEntry("postgres", 4242, "postgres", 71.5, 512L << 20)]);

            main.HistoryWindowOverride = w => window = w;
            main.ShowHistory(profile);
            check(window is not null, "«История…» on Home opens the history window of the server");
            window!.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -10000; window.Top = -10000; window.ShowInTaskbar = false; window.Show(); Pump();
            var view = window.View; Wait(view.Loading!);
            Pump(); window.UpdateLayout();
            var titles = view.Charts.Select(c => c.Title).ToArray();
            var cpu = view.Charts.FirstOrDefault(c => c.Title == "ЦП");
            check(titles.Contains("ЦП") && titles.Contains("Память") && titles.Contains("Сеть") && titles.Contains("Заполнение дисков") && titles.Contains("queue") && cpu is { Series.Count: 1 } && cpu.Series[0].Points.Count >= 170,
                "the last 24 hours: CPU, memory, network, disks and the custom metric, every recorded minute: " + string.Join(", ", titles) + " cpu points " + cpu?.Series[0].Points.Count);
            check(view.EventLines.Any(l => l.Contains("релиз 1.2", StringComparison.Ordinal)) && view.StatusText.Length > 0, "events of the period are listed; the recording state is explained: " + view.StatusText);
            evidence.Capture((FrameworkElement)window.Content, "history-24h", new { theme = "Light", fixtureOnly = true });

            check(!view.BackVisible, "«Назад» is hidden until a part of a graph is zoomed");
            view.Zoom(now - TimeSpan.FromMinutes(30), now); Wait(view.Loading!);
            check(view.BackVisible, "after zooming «Назад» appears next to the period");
            Pump(); window.UpdateLayout(); evidence.Capture((FrameworkElement)window.Content, "history-zoomed", new { theme = "Light", fixtureOnly = true });
            var zoomed = view.Charts.First(c => c.Title == "ЦП").Series[0].Points;
            check(zoomed.Count is >= 28 and <= 32, "selecting a part of a graph shows only that time: " + zoomed.Count);
            view.Back(); Wait(view.Loading!);
            check(view.Charts.First(c => c.Title == "ЦП").Series[0].Points.Count >= 170, "«Назад» returns to the previous period");

            view.SetCompare(other.Id, false); Wait(view.Loading!);
            var compared = view.Charts.First(c => c.Title == "ЦП").Series;
            check(compared.Count == 2 && compared[1].Dashed && compared[1].Label.Contains(other.Name, StringComparison.Ordinal) && compared[1].Points.All(p => p.Average == 10), "another server is drawn dashed on the same graph");
            view.SetCompare(null, true); Wait(view.Loading!);
            var previous = view.Charts.First(c => c.Title == "ЦП").Series;
            check(previous.Count == 1 || previous.Count == 2 && previous[1].Shift > TimeSpan.Zero, "the previous period is shifted onto this one (and absent when nothing was recorded then)");
            view.SetCompare(null, false); Wait(view.Loading!);

            view.ShowProcesses(now - TimeSpan.FromMinutes(60));
            check(view.ProcessLines.Any(l => l.StartsWith("postgres (4242) — ЦП 72 %", StringComparison.Ordinal)), "a click on a graph lists the busiest processes of that minute: " + string.Join(" | ", view.ProcessLines));

            HistoryView.MarkTextOverride = () => "обновил nginx";
            view.AddMark(); Wait(view.Loading!);
            check(view.EventLines.Any(l => l.Contains("обновил nginx", StringComparison.Ordinal)) && metrics.Events(profile.Id, now.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1)).Any(e => e.Kind == "mark"), "a user's mark is stored and shown");

            var csv = Path.Combine(temp, "history.csv"); HistoryView.ExportPathOverride = () => csv;
            view.ExportCsv();
            var lines = File.ReadAllLines(csv);
            check(lines.Length >= 170 && lines[0].StartsWith("Время;ЦП, %", StringComparison.Ordinal) && lines[0].Contains("queue", StringComparison.Ordinal), "CSV export of the shown series: " + lines.FirstOrDefault());

            // History alerts: a rule over the recorded data, evaluated as after a sample.
            store.Settings.Metrics.Rules = [new MetricAlertRule { ServerId = profile.Id, Series = MetricSeries.Cpu, Above = true, Threshold = 80, Minutes = 15 }];
            var issues = MetricsHub.Evaluate(profile.Id, DateTimeOffset.UtcNow);
            check(issues.Any(i => i.Key.StartsWith("metric:", StringComparison.Ordinal) && i.Text.StartsWith("ЦП выше 80 % дольше 15 мин", StringComparison.Ordinal)), "CPU above 80 % for 15 minutes raises a history alert: " + string.Join(" | ", issues.Select(i => i.Text)));
            store.Settings.Metrics.Rules[0].Threshold = 90;
            check(!MetricsHub.Evaluate(profile.Id, DateTimeOffset.UtcNow).Any(i => i.Key.StartsWith("metric:", StringComparison.Ordinal)), "below the threshold there is no alert");
            MetricAlertsWindow.DialogOverride = w => { ShowOffscreen(w); evidence.Capture((FrameworkElement)w.Content, "metric-alerts", new { theme = "Light", fixtureOnly = true }); Wait(w.SaveAsync()); w.Close(); return true; };
            MetricAlertsWindow.Open(main, profile.Id);
            check(store.Settings.Metrics.Rules.Count == 1 && store.Settings.Metrics.Rules[0].Threshold == 90 && store.Settings.Metrics.ForecastDays == 14, "the rules window saves the rules as shown");

            // Custom metrics editor: an empty list explains itself with starting points; an imported command asks for confirmation.
            bool emptyShown = false, bannerShown = false;
            CustomMetricsWindow.DialogOverride = w => { ShowOffscreen(w); emptyShown = w.EmptyStateVisible && !w.UnconfirmedVisible; evidence.Capture((FrameworkElement)w.Content, "custom-metrics-empty", new { theme = "Light", fixtureOnly = true }); w.Close(); return false; };
            CustomMetricsWindow.Edit(main, [], null, false, "Свои метрики — пусто");
            CustomMetricsWindow.DialogOverride = w => { ShowOffscreen(w); bannerShown = w.UnconfirmedVisible && !w.EmptyStateVisible; evidence.Capture((FrameworkElement)w.Content, "custom-metrics-unconfirmed", new { theme = "Light", fixtureOnly = true }); w.Close(); return false; };
            CustomMetricsWindow.Edit(main, [new CustomMetricDefinition { Name = "imported", Command = "echo 1", Approved = false }], null, false, "Свои метрики — из файла");
            check(emptyShown && bannerShown, $"the editor shows starting points when empty ({emptyShown}) and a confirmation for an imported command ({bannerShown})");
            // Custom metrics editor: a template with a parameter becomes a confirmed metric with a quoted command.
            CustomMetricsWindow.TemplateValueOverride = _ => "nginx";
            CustomMetricsWindow.DialogOverride = w => { w.FromTemplate(CustomMetricTemplates.All.Single(x => x.Key == "service")); return true; };
            var edited = CustomMetricsWindow.Edit(main, [], null, false, "проверка");
            check(edited is [{ Name: "service", Approved: true } made] && made.Command == "systemctl is-active --quiet 'nginx' && echo 1 || echo 0", "a template becomes a custom metric: " + edited?.FirstOrDefault()?.Command);

            // Profile dialog model keeps the history options when the profile is saved again.
            profile.RecordHistory = true; profile.CustomMetrics = [new CustomMetricDefinition { Name = "queue", Command = "echo 1", Approved = true }];
            var model = new ProfileViewModel(new ProfilePersistence(store), profile);
            Wait(model.SaveAsync(""));
            check(model.SavedProfile is { RecordHistory: true, CustomMetrics: [{ Name: "queue", Approved: true }] }, "saving a profile keeps background recording and its custom metrics");

            // Folder settings: background recording for the whole folder; its metrics survive an edit of the user.
            store.Settings.Folders.Add(new FolderDefaults { Path = "Мониторинг", CustomMetrics = [new CustomMetricDefinition { Name = "folder_metric", Command = "echo 2", Approved = true }] });
            main.FolderWindowOverride = w => { w.RecordHistoryBox.IsChecked = true; w.UserBox.Text = "ops2"; w.Accept(); return true; };
            Wait(main.EditFolderAsync("Мониторинг"));
            var folder = store.Settings.Folders.Single(f => f.Path == "Мониторинг");
            check(folder is { RecordHistory: true, Username: "ops2", CustomMetrics: [{ Name: "folder_metric" }] } && MetricsProfile.RecordsInBackground(new ServerProfile { Group = "Мониторинг/web" }, store.Settings.Folders), "folder settings switch on background recording and keep the folder's metrics");

            // Home: the load column reads the recorded history in the background.
            var watch = Stopwatch.StartNew(); while (HomeMetrics.Get(profile.Id) is null && watch.Elapsed < TimeSpan.FromSeconds(20)) { HomeMetrics.Refresh(true); Pump(); }
            var row = new ServerProfileViewModel(profile, []);
            check(row.LoadHistory is { Length: 48 } && row.LoadText.Contains("ЦП 85 %", StringComparison.Ordinal) && row.LoadRank == 85, "Home shows the CPU line of 24 hours and the latest values: " + row.LoadText);

            // Settings: the history page.
            var settings = new SettingsWindow("history") { WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, ShowInTaskbar = false };
            settings.Show(); Pump();
            check(settings.CurrentSection == "history" && FindName<CheckBox>(settings, "SettingsMetricsOpenTabs") is { IsChecked: true } && FindName<CheckBox>(settings, "SettingsPrometheus") is { IsChecked: false }, "«Параметры» → «История метрик»: recording on, Prometheus off by default");
            evidence.Capture((FrameworkElement)settings.Content, "settings-history", new { theme = "Light", fixtureOnly = true });
            settings.Close();

            // «Файлы по SSH» from a remote desktop profile: the same server's SSH profile, or a temporary one on port 22.
            var desktop = new ServerProfile { Name = "Склад RDP", Host = "sklad.example.invalid", Port = 3389, Username = "kladovshik", Protocol = ConnectionProtocol.Rdp, Rdp = new RdpOptions { Domain = "CORP" } };
            store.Profiles.Add(desktop);
            (ServerProfile Profile, SessionCredentials? Credentials)? opened = null;
            main.FilesOverSshOverride = (p, c) => { opened = (p, c); return Task.CompletedTask; };
            Wait(main.OpenFilesOverSshAsync(desktop));
            check(opened is { Profile: { IsRdp: false, Port: 22, Host: "sklad.example.invalid", Username: "CORP\\kladovshik" } } && opened.Value.Profile.Name == "Склад RDP · SSH" && !store.Profiles.Contains(opened.Value.Profile), "an RDP profile opens its files over SSH with a temporary profile");
            var twin = new ServerProfile { Name = "Склад SSH", Host = "SKLAD.example.invalid", Username = "admin" }; store.Profiles.Add(twin);
            Wait(main.OpenFilesOverSshAsync(desktop));
            check(opened?.Profile == twin, "an existing SSH profile of the same server is preferred");
            store.Profiles.Remove(desktop); store.Profiles.Remove(twin);

            MetricsHub.Delete(profile.Id);
            check(metrics.SizeOf(profile.Id) == 0, "a deleted profile's history can be removed");
        }
        finally
        {
            window?.Close();
            main.HistoryWindowOverride = null; main.FolderWindowOverride = null; main.FilesOverSshOverride = null;
            HistoryView.MarkTextOverride = null; HistoryView.ExportPathOverride = null; MetricAlertsWindow.DialogOverride = null; CustomMetricsWindow.DialogOverride = null; CustomMetricsWindow.TemplateValueOverride = null;
            store.Settings.Metrics.Rules.Clear();
            store.Settings.Folders.RemoveAll(f => f.Path == "Мониторинг");
            store.Profiles.RemoveAll(p => p.Id == profile.Id || p.Id == other.Id);
            MetricsHub.Delete(other.Id);
            try { Directory.Delete(temp, true); } catch (IOException) { }
        }
    }

    static void ShowOffscreen(Window w) { w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = -10000; w.Top = -10000; w.ShowInTaskbar = false; w.Show(); Pump(); w.UpdateLayout(); Pump(); }

    static T? FindName<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T t && t.Name == name) return t;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) if (FindName<T>(child, name) is { } found) return found;
        return null;
    }
    static void Wait(Task task) { var watch = Stopwatch.StartNew(); while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(30)) Pump(); task.GetAwaiter().GetResult(); }
    static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false); Dispatcher.PushFrame(frame); }
}
