using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SSHExplorer.Core;
using SSHExplorer.Monitoring;

namespace SSHExplorer.App;

/// <summary>
/// «История»: recorded metrics of one server over an hour, a day, a week, a month or a chosen period; comparison with
/// another server or the previous period, events, the busiest processes at a moment and CSV export.
/// </summary>
public sealed class HistoryView : UserControl, IDisposable
{
    sealed record Group(string Key, string Title, Func<string, bool> Match, bool Default);
    static readonly Color[] Palette = [Color.FromRgb(0x00, 0x78, 0xD4), Color.FromRgb(0x10, 0x89, 0x3E), Color.FromRgb(0xC2, 0x39, 0xB3), Color.FromRgb(0xCA, 0x50, 0x10), Color.FromRgb(0x87, 0x64, 0xB8), Color.FromRgb(0x03, 0x83, 0x87), Color.FromRgb(0x98, 0x6F, 0x0B), Color.FromRgb(0xE8, 0x11, 0x23)];
    static readonly Group[] SystemGroups =
    [
        new("cpu", "ЦП", n => n == MetricSeries.Cpu, true),
        new("mem", "Память", n => n == MetricSeries.Memory, true),
        new("swap", "Подкачка", n => n == MetricSeries.Swap, false),
        new("load", "Нагрузка", n => n == MetricSeries.Load1, false),
        new("net", "Сеть", n => n is MetricSeries.NetReceive or MetricSeries.NetSend, true),
        new("io", "Диски: чтение и запись", n => n is MetricSeries.DiskRead or MetricSeries.DiskWrite, false),
        new("fs", "Заполнение дисков", MetricSeries.IsFileSystem, true),
    ];

    readonly Guid server;
    readonly ServerSession? session;
    readonly StackPanel charts = new() { Margin = new Thickness(12, 4, 12, 12) };
    readonly WrapPanel groupPanel = new() { Margin = new Thickness(12, 2, 12, 4) };
    readonly StackPanel rangePanel = new() { Orientation = Orientation.Horizontal };
    readonly TextBlock status = Ui.Text("", 12), periodText = Ui.Text("", 12), processTitle = Ui.Text("Процессы", 13, true), errors = Ui.Text("", 12), processHint = Ui.Text("Щёлкните по графику — здесь появятся самые загруженные процессы той минуты.", 12);
    readonly StackPanel processes = new(), eventList = new();
    readonly CheckBox background = new() { Content = "Записывать в фоне", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
    readonly Button backButton, compareChip;
    readonly HashSet<string> selected = new(StringComparer.Ordinal);
    readonly Stack<(DateTimeOffset From, DateTimeOffset To, TimeSpan? Live)> back = new();
    readonly Action<Guid> updated;
    TimeSpan? live = TimeSpan.FromHours(24);
    DateTimeOffset from, to;
    Guid? compareServer; bool comparePrevious;
    int loadVersion; bool disposed;
    DateTimeOffset lastAutoLoad;
    /// <summary>Test hook: the CSV file chosen in the save dialog.</summary>
    internal static Func<string?>? ExportPathOverride { get; set; }
    /// <summary>Test hook: the text of a new mark.</summary>
    internal static Func<string?>? MarkTextOverride { get; set; }
    internal Task? Loading { get; private set; }
    internal IReadOnlyList<HistoryChart> Charts => charts.Children.OfType<HistoryChart>().ToArray();
    internal string StatusText => status.Text;
    internal IReadOnlyList<string> ProcessLines => processes.Children.OfType<TextBlock>().Select(t => t.Text).ToArray();
    internal IReadOnlyList<string> EventLines => eventList.Children.OfType<TextBlock>().Select(t => t.Text).ToArray();
    internal bool BackVisible => backButton.Visibility == Visibility.Visible;

    static void SetLines(StackPanel panel, IEnumerable<string> lines)
    {
        panel.Children.Clear();
        foreach (var line in lines)
        {
            var t = Ui.Text(line, 12); t.TextWrapping = TextWrapping.Wrap; t.Margin = new Thickness(0, 0, 0, 6);
            panel.Children.Add(t);
        }
    }

    public HistoryView(Guid server, ServerSession? session = null)
    {
        this.server = server; this.session = session;
        Name = "HistoryView";
        var root = new DockPanel(); root.SetResourceReference(Panel.BackgroundProperty, "Paper");

        // Toolbar: the period as one segmented group on the left, the other commands in a bar that folds into «…» when narrow.
        foreach (var (label, span) in new (string, TimeSpan)[] { ("1 ч", TimeSpan.FromHours(1)), ("24 ч", TimeSpan.FromHours(24)), ("7 дн", TimeSpan.FromDays(7)), ("30 дн", TimeSpan.FromDays(30)) })
        {
            var b = Ui.Button(label, () => SetLive(span), $"Последние {label}"); b.Tag = span; b.MinWidth = 52; rangePanel.Children.Add(b);
        }
        var period = Ui.Button("Период…", ChoosePeriod, "Показать выбранные дни"); period.Name = "HistoryPeriod"; rangePanel.Children.Add(period);
        foreach (var b in rangePanel.Children.OfType<Button>()) { b.Margin = new Thickness(0, 0, 2, 0); b.Padding = new Thickness(10, 4, 10, 4); }
        var commands = new ResponsiveCommandBar { Name = "HistoryCommands", Margin = new Thickness(12, 0, 0, 0) };
        var compare = Ui.FlyoutButton(IconCatalog.Performance, "Сравнить", CompareMenu, "Другой сервер или прошлый период на тех же графиках"); compare.Name = "HistoryCompare";
        commands.Add("compare", compare, () => { var m = new MenuItem { Header = "Сравнить" }; foreach (var i in CompareMenu().Items.OfType<Control>().ToArray()) { ((ItemsControl)i.Parent).Items.Remove(i); m.Items.Add(i); } return m; }, 60);
        commands.Add("mark", Ui.CommandButton(Ui.AddGlyph, "Отметка…", AddMark, "Своя отметка на графиках (например, «обновил nginx»)"), () => Ui.Menu("Отметка…", AddMark, glyph: Ui.AddGlyph), 40);
        commands.Add("export", Ui.CommandButton(Ui.FileGlyph, "Экспорт CSV…", ExportCsv, "Сохранить показанные ряды в CSV"), () => Ui.Menu("Экспорт CSV…", ExportCsv, glyph: Ui.FileGlyph), 30);
        commands.Add("custom", Ui.CommandButton(IconCatalog.Settings, "Свои метрики…", EditCustomMetrics, "Команды на сервере, вывод которых записывается как метрика"), () => Ui.Menu("Свои метрики…", EditCustomMetrics, glyph: IconCatalog.Settings), 20);
        commands.Add("alerts", Ui.CommandButton(IconCatalog.Clock, "Оповещения…", () => MetricAlertsWindow.Open(Window.GetWindow(this), server), "Правила оповещений по истории"), () => Ui.Menu("Оповещения…", () => MetricAlertsWindow.Open(Window.GetWindow(this), server), glyph: IconCatalog.Clock), 10);
        var refresh = Ui.IconButton(Ui.RefreshGlyph, () => { MetricsHub.Scheduler.Wake(server); Reload(); }, "Обновить: перечитать историю и записать точку сейчас"); refresh.Name = "HistoryRefresh";
        var bar = new DockPanel { Margin = new Thickness(10, 4, 10, 4), LastChildFill = true };
        DockPanel.SetDock(rangePanel, Dock.Left); bar.Children.Add(rangePanel);
        DockPanel.SetDock(refresh, Dock.Right); bar.Children.Add(refresh);
        bar.Children.Add(commands);
        var commandBorder = new Border { Child = bar, BorderThickness = new Thickness(0, 0, 0, 1) }; commandBorder.SetResourceReference(StyleProperty, "CommandBarStyle");
        DockPanel.SetDock(commandBorder, Dock.Top); root.Children.Add(commandBorder);

        // What is shown and whether it is recorded; «Назад» and the comparison appear only while they apply.
        backButton = Ui.Button("← Назад", ZoomBack, "Вернуться к периоду до увеличения"); backButton.Name = "HistoryBack"; backButton.Margin = new Thickness(0, 0, 8, 0); backButton.Padding = new Thickness(8, 2, 8, 2);
        compareChip = Ui.Button("", () => SetCompare(null, false), "Убрать сравнение"); compareChip.Name = "HistoryCompareChip"; compareChip.Margin = new Thickness(0, 0, 8, 0); compareChip.Padding = new Thickness(8, 2, 8, 2);
        background.Name = "HistoryBackground"; background.ToolTip = "Раз в минуту, даже если вкладка сервера закрыта: отдельное подключение без терминала, с сохранённым паролем или ключом.";
        background.Click += async (_, _) => await SetBackgroundAsync(background.IsChecked == true);
        periodText.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); periodText.VerticalAlignment = VerticalAlignment.Center; periodText.TextTrimming = TextTrimming.CharacterEllipsis;
        status.TextWrapping = TextWrapping.Wrap; status.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); status.Name = "HistoryStatus";
        errors.TextWrapping = TextWrapping.Wrap; errors.SetResourceReference(TextBlock.ForegroundProperty, "ValidationInk");
        var periodRow = new DockPanel(); DockPanel.SetDock(backButton, Dock.Left); periodRow.Children.Add(backButton); DockPanel.SetDock(compareChip, Dock.Left); periodRow.Children.Add(compareChip); periodRow.Children.Add(periodText);
        var statusRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) }; DockPanel.SetDock(background, Dock.Right); statusRow.Children.Add(background); statusRow.Children.Add(status);
        var info = new StackPanel { Margin = new Thickness(14, 8, 14, 0) };
        info.Children.Add(periodRow); info.Children.Add(statusRow); info.Children.Add(errors);
        DockPanel.SetDock(info, Dock.Top); root.Children.Add(info);
        groupPanel.Margin = new Thickness(14, 6, 14, 2);
        DockPanel.SetDock(groupPanel, Dock.Top); root.Children.Add(groupPanel);

        // Body: graphs on the left; the processes of a picked minute and the events of the period in a side column.
        var side = new StackPanel { Margin = new Thickness(16, 10, 14, 14) };
        side.Children.Add(processTitle);
        processHint.TextWrapping = TextWrapping.Wrap; processHint.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); processHint.Margin = new Thickness(0, 4, 0, 0);
        side.Children.Add(processHint); processes.Name = "HistoryProcesses"; processes.Margin = new Thickness(0, 6, 0, 0); side.Children.Add(processes);
        var eventsTitle = Ui.Text("События", 13, true); eventsTitle.Margin = new Thickness(0, 18, 0, 6); side.Children.Add(eventsTitle);
        eventList.Name = "HistoryEvents"; side.Children.Add(eventList);
        var sideColumn = new Border { Width = 300, BorderThickness = new Thickness(1, 0, 0, 0), Child = new ScrollViewer { Content = side, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
        sideColumn.SetResourceReference(Border.BorderBrushProperty, "Edge");
        DockPanel.SetDock(sideColumn, Dock.Right); root.Children.Add(sideColumn);
        charts.Margin = new Thickness(14, 4, 10, 14);
        root.Children.Add(new ScrollViewer { Content = charts, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = root;

        updated = id => { if (id == server && live is not null && !disposed && DateTimeOffset.UtcNow - lastAutoLoad > TimeSpan.FromSeconds(20)) Reload(); };
        MetricsHub.Updated += updated;
        Loaded += (_, _) => { if (charts.Children.Count == 0) Reload(); };
        UpdateBackground();
        Reload();
    }

    ServerProfile? Profile => App.Store.Profiles.FirstOrDefault(p => p.Id == server);

    void UpdateBackground()
    {
        var p = Profile;
        background.IsEnabled = p is not null;
        background.IsChecked = p?.RecordHistory == true;
        if (p is not null && !p.RecordHistory && MetricsProfile.RecordsInBackground(p, App.Store.Settings.Folders)) { background.IsChecked = true; background.IsEnabled = false; background.ToolTip = "Включено для всей папки в «Параметрах папки»."; }
    }

    async Task SetBackgroundAsync(bool on)
    {
        if (Profile is not { } p) return;
        p.RecordHistory = on;
        try { await App.Store.SaveAsync(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Ui.Error("Не удалось сохранить профиль: " + ex.Message); }
        MetricsHub.Scheduler.Wake(server);
        Reload();
    }

    internal void SetLive(TimeSpan span) { back.Clear(); live = span; Reload(); }
    internal void SetCompare(Guid? server, bool previous) { compareServer = server; comparePrevious = previous; Reload(); }
    internal void Back() => ZoomBack();

    void ZoomBack()
    {
        if (back.Count == 0) return;
        var (f, t, l) = back.Pop(); live = l; from = f; to = t; Reload();
    }

    internal void Zoom(DateTimeOffset start, DateTimeOffset end)
    {
        back.Push((from, to, live)); live = null; from = start; to = end; Reload();
    }

    void ChoosePeriod()
    {
        var w = new Window { Title = "Период", Width = 420, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Window.GetWindow(this) };
        w.SetResourceReference(StyleProperty, "DialogWindowStyle");
        var start = new DatePicker { SelectedDate = DateTime.Today.AddDays(-7), Margin = new Thickness(0, 4, 0, 10) };
        var end = new DatePicker { SelectedDate = DateTime.Today, Margin = new Thickness(0, 4, 0, 10) };
        var body = new StackPanel { Margin = new Thickness(22) };
        body.Children.Add(Ui.Text("С (включительно)")); body.Children.Add(start); body.Children.Add(Ui.Text("По (включительно)")); body.Children.Add(end);
        var ok = Ui.Button("Показать", () => w.DialogResult = true); ok.IsDefault = true; var cancel = Ui.Button("Отмена", () => w.DialogResult = false); cancel.IsCancel = true;
        w.Content = Ui.DialogSurface(body, ok, cancel);
        if (w.ShowDialog() != true || start.SelectedDate is not { } a || end.SelectedDate is not { } b) return;
        if (b < a) (a, b) = (b, a);
        back.Push((from, to, live)); live = null;
        from = new DateTimeOffset(a.Date, TimeZoneInfo.Local.GetUtcOffset(a.Date));
        to = new DateTimeOffset(b.Date.AddDays(1).AddSeconds(-1), TimeZoneInfo.Local.GetUtcOffset(b.Date));
        Reload();
    }

    ContextMenu CompareMenu()
    {
        var menu = new ContextMenu();
        var none = Ui.Menu("Без сравнения", () => { compareServer = null; comparePrevious = false; Reload(); }); none.IsCheckable = true; none.IsChecked = compareServer is null && !comparePrevious; menu.Items.Add(none);
        var previous = Ui.Menu("С прошлым периодом", () => { compareServer = null; comparePrevious = true; Reload(); }); previous.IsCheckable = true; previous.IsChecked = comparePrevious; menu.Items.Add(previous);
        menu.Items.Add(new Separator());
        var others = MetricsHub.Store.Servers().Where(id => id != server).Select(id => (Id: id, Name: App.Store.Profiles.FirstOrDefault(p => p.Id == id)?.Name)).Where(x => x.Name is not null).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        if (others.Length == 0) menu.Items.Add(new MenuItem { Header = "Других серверов с историей нет", IsEnabled = false });
        foreach (var (id, name) in others)
        {
            var item = Ui.Menu("С сервером «" + name + "»", () => { compareServer = id; comparePrevious = false; Reload(); }); item.IsCheckable = true; item.IsChecked = compareServer == id; menu.Items.Add(item);
        }
        return menu;
    }

    internal void AddMark()
    {
        var text = MarkTextOverride is { } fake ? fake() : Ui.Prompt("Отметка на графиках", "Что произошло (например, «обновил nginx»):");
        if (string.IsNullOrWhiteSpace(text)) return;
        MetricsHub.Note(server, "mark", text.Trim());
        Reload();
    }

    internal void ExportCsv()
    {
        var shown = Charts.SelectMany(c => c.Series).Where(s => s.Shift == TimeSpan.Zero && !s.Dashed).GroupBy(s => s.Name).Select(g => g.First()).ToArray();
        if (shown.Length == 0) { Ui.Info("Нечего сохранять: за выбранный период нет записанных значений."); return; }
        string? path = ExportPathOverride is { } fake ? fake() : null;
        if (ExportPathOverride is null)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Title = "Экспорт истории", Filter = "CSV (разделитель ;)|*.csv", FileName = $"{Profile?.Name ?? "server"} {from.ToLocalTime():yyyy-MM-dd} — {to.ToLocalTime():yyyy-MM-dd}.csv" };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            path = dialog.FileName;
        }
        if (string.IsNullOrEmpty(path)) return;
        var csv = MetricsExport.Csv(shown.ToDictionary(s => s.Name, s => s.Points, StringComparer.Ordinal), TimeZoneInfo.Local, CultureInfo.CurrentCulture);
        try { File.WriteAllText(path, csv, new System.Text.UTF8Encoding(true)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Ui.Error("Не удалось сохранить файл: " + ex.Message); }
    }

    void EditCustomMetrics()
    {
        if (Profile is not { } p) return;
        var edited = CustomMetricsWindow.Edit(Window.GetWindow(this), p.CustomMetrics, session is { State: SessionState.Connected } ? session : null, session?.IsWindows ?? false, "Свои метрики — " + p.Name);
        if (edited is null) return;
        p.CustomMetrics = edited.ToList();
        _ = SaveAndWakeAsync();
    }

    async Task SaveAndWakeAsync()
    {
        try { await App.Store.SaveAsync(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Ui.Error("Не удалось сохранить профиль: " + ex.Message); return; }
        MetricsHub.Scheduler.Wake(server); Reload();
    }

    IEnumerable<Group> Groups(IReadOnlyCollection<string> names)
    {
        foreach (var g in SystemGroups) if (names.Any(g.Match)) yield return g;
        foreach (var metric in names.Where(MetricSeries.IsCustom).Select(n => n[MetricSeries.CustomPrefix.Length..].Split('.')[0]).Distinct().Order(StringComparer.Ordinal))
            yield return new Group("custom:" + metric, metric, n => n == MetricSeries.Custom(metric) || n.StartsWith(MetricSeries.Custom(metric) + ".", StringComparison.Ordinal), true);
    }

    void BuildGroupToggles(IReadOnlyList<Group> groups)
    {
        groupPanel.Children.Clear();
        foreach (var g in groups)
        {
            var box = new CheckBox { Content = g.Title, IsChecked = Shown(g, selected, hidden), Margin = new Thickness(0, 2, 14, 2), Tag = g.Key };
            box.Click += (_, _) => { if (box.IsChecked == true) { selected.Add(g.Key); hidden.Remove(g.Key); } else { selected.Remove(g.Key); hidden.Add(g.Key); } Reload(); };
            groupPanel.Children.Add(box);
        }
    }
    /// <summary>Groups shown by default (system basics and every custom metric) unless hidden; others only when ticked.</summary>
    readonly HashSet<string> hidden = new(StringComparer.Ordinal);
    static bool Shown(Group g, HashSet<string> shown, HashSet<string> hiddenKeys) => g.Default ? !hiddenKeys.Contains(g.Key) : shown.Contains(g.Key);

    TimeSpan StepOf(MetricResolution resolution) => resolution switch
    {
        MetricResolution.Raw => TimeSpan.FromSeconds(Math.Clamp(App.Store.Settings.Metrics.IntervalSeconds, 30, 3600)),
        MetricResolution.FiveMinutes => TimeSpan.FromMinutes(5),
        _ => TimeSpan.FromHours(1),
    };

    /// <summary>Reads the period again (in the background) and redraws.</summary>
    public void Reload()
    {
        if (disposed) return;
        lastAutoLoad = DateTimeOffset.UtcNow;
        if (live is { } span) { to = DateTimeOffset.UtcNow; from = to - span; }
        foreach (var b in rangePanel.Children.OfType<Button>())
        {
            bool current = b.Tag is TimeSpan s && s == live || b.Name == "HistoryPeriod" && live is null && back.Count == 0;
            b.FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal;
            if (current) b.SetResourceReference(BackgroundProperty, "Selected"); else b.ClearValue(BackgroundProperty);
        }
        backButton.Visibility = back.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var compareName = compareServer is { } cs ? App.Store.Profiles.FirstOrDefault(p => p.Id == cs)?.Name ?? "другой сервер" : comparePrevious ? "прошлый период" : null;
        compareChip.Content = compareName is null ? "" : "Сравнение: " + compareName + "  ✕";
        compareChip.Visibility = compareName is null ? Visibility.Collapsed : Visibility.Visible;
        var version = ++loadVersion;
        var (start, end) = (from, to);
        var resolution = MetricsStore.ResolutionFor(end - start);
        var step = StepOf(resolution);
        var store = MetricsHub.Store;
        var other = compareServer; var previous = comparePrevious;
        var wanted = selected.ToHashSet(StringComparer.Ordinal); var unwanted = hidden.ToHashSet(StringComparer.Ordinal);
        Loading = LoadAsync();
        async Task LoadAsync()
        {
            var result = await Task.Run(() =>
            {
                var units = store.SeriesOf(server);
                var groups = Groups(units.Keys.ToArray()).ToArray();
                var names = units.Keys.Where(n => groups.Any(g => Shown(g, wanted, unwanted) && g.Match(n))).ToArray();
                var data = store.Read(server, names, start, end, resolution);
                IReadOnlyDictionary<string, IReadOnlyList<MetricPoint>>? extra = null;
                if (other is { } o) extra = store.Read(o, names, start, end, resolution);
                else if (previous) extra = store.Read(server, names, start - (end - start), start, resolution);
                var marks = store.Events(server, start, end);
                return (units, groups, data, extra, marks);
            });
            if (version != loadVersion || disposed) return;
            Render(result.units, result.groups, result.data, result.extra, result.marks, start, end, step, resolution);
        }
    }

    void Render(IReadOnlyDictionary<string, string> units, IReadOnlyList<Group> groups, IReadOnlyDictionary<string, IReadOnlyList<MetricPoint>> data,
        IReadOnlyDictionary<string, IReadOnlyList<MetricPoint>>? extra, IReadOnlyList<MetricEvent> marks, DateTimeOffset start, DateTimeOffset end, TimeSpan step, MetricResolution resolution)
    {
        BuildGroupToggles(groups);
        var otherName = compareServer is { } o ? App.Store.Profiles.FirstOrDefault(p => p.Id == o)?.Name ?? "другой сервер" : null;
        string detail = resolution switch { MetricResolution.Raw => "каждый замер", MetricResolution.FiveMinutes => "средние за 5 минут, полоса — мин–макс", _ => "средние за час, полоса — мин–макс" };
        periodText.Text = $"{start.ToLocalTime():dd.MM.yyyy HH:mm} — {end.ToLocalTime():dd.MM.yyyy HH:mm} · {detail} · выделите участок графика, чтобы увеличить";
        charts.Children.Clear();
        foreach (var g in groups.Where(g => Shown(g, selected, hidden)))
        {
            var names = data.Keys.Where(g.Match).Order(StringComparer.Ordinal).ToArray();
            if (names.Length == 0) continue;
            var list = new List<ChartSeries>();
            for (int i = 0; i < names.Length; i++)
            {
                var n = names[i]; var unit = units.TryGetValue(n, out var u) && u.Length > 0 ? u : MetricSeries.UnitOf(n);
                var color = Palette[i % Palette.Length];
                list.Add(new ChartSeries(n, MetricSeries.Label(n), unit, data[n], color));
                if (extra is not null && extra.TryGetValue(n, out var e) && e.Count > 0)
                    list.Add(new ChartSeries(n, MetricSeries.Label(n) + (otherName is not null ? " · " + otherName : " · прошлый период"), unit, e, color, true, otherName is null ? end - start : TimeSpan.Zero));
            }
            var chart = new HistoryChart { Title = g.Title, Margin = new Thickness(0, 6, 0, 6) };
            chart.SetData(list, start, end, step, marks);
            chart.RangeSelected += Zoom;
            chart.PointPicked += ShowProcesses;
            charts.Children.Add(chart);
        }
        if (charts.Children.Count == 0)
        {
            var empty = Ui.Text(units.Count == 0 ? "История этого сервера ещё не записана. Она пишется раз в минуту, пока открыта вкладка сервера, или в фоне, если включить «Записывать в фоне»." : "Выберите, что показать, — флажки над графиками.", 14);
            empty.TextWrapping = TextWrapping.Wrap; empty.Margin = new Thickness(0, 24, 0, 0); empty.MaxWidth = 640; empty.HorizontalAlignment = HorizontalAlignment.Left;
            charts.Children.Add(empty);
        }
        var eventLines = Enumerable.Reverse(marks).Select(e => $"{e.Time.ToLocalTime():dd.MM HH:mm} — {e.Text}").ToList();
        SetLines(eventList, eventLines.Count > 0 ? eventLines : ["Событий за период нет"]);
        UpdateStatus();
    }

    void UpdateStatus()
    {
        var state = MetricsHub.Scheduler.StatusOf(server);
        bool open = session is { State: SessionState.Connected } && App.Store.Settings.Metrics.RecordOpenTabs;
        status.Text = state switch
        {
            { Problem: { } problem } => problem,
            { LastSample: { } last } => $"Записывается{(state.Background ? " (в том числе в фоне)" : "")}: последний замер в {last.ToLocalTime():HH:mm}.",
            { Background: true } => "Запись в фоне включена; первый замер — в течение минуты.",
            _ when open => "Записывается, пока открыта вкладка сервера; первый замер — в течение минуты.",
            _ => "Сейчас не записывается: откройте вкладку сервера или включите «Записывать в фоне».",
        };
        var p = Profile;
        IReadOnlyList<CustomMetricDefinition> custom = p is null ? [] : MetricsProfile.CustomMetrics(p, App.Store.Settings.Folders);
        var problems = MetricsHub.CustomErrors(server).Select(e => (custom.FirstOrDefault(m => m.Id == e.Key)?.Name ?? "метрика") + ": " + e.Value)
            .Concat(custom.Where(m => !m.Approved).Select(m => m.Name + ": команда не подтверждена — откройте «Свои метрики…» и проверьте её.")).ToArray();
        errors.Text = string.Join("\n", problems);
        errors.Visibility = problems.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateBackground();
    }

    internal void ShowProcesses(DateTimeOffset time)
    {
        var resolution = MetricsStore.ResolutionFor(to - from);
        var tolerance = TimeSpan.FromSeconds(Math.Max(120, StepOf(resolution).TotalSeconds));
        var top = MetricsHub.Store.ProcessesAt(server, time, tolerance);
        processHint.Visibility = Visibility.Collapsed;
        if (top is null)
        {
            processTitle.Text = $"Процессы в {time.ToLocalTime():dd.MM HH:mm}";
            SetLines(processes, ["Нет записи процессов рядом с этой минутой."]);
            return;
        }
        processTitle.Text = $"Процессы в {top.Time.ToLocalTime():dd.MM HH:mm}";
        SetLines(processes, top.Processes.OrderByDescending(p => p.Cpu ?? -1).Select(p => $"{p.Name} ({p.Pid}) — ЦП {(p.Cpu is { } c ? MetricSeries.Format(c, MetricSeries.Percent) : "—")}, память {(p.Memory is { } m ? MetricSeries.Format(m, MetricSeries.Bytes) : "—")}" + (p.User.Length > 0 ? ", " + p.User : "")));
    }

    public void Dispose() { disposed = true; MetricsHub.Updated -= updated; }
}

/// <summary>«История» of a server from «Главная», without opening a tab.</summary>
public sealed class HistoryWindow : Window
{
    public HistoryView View { get; }
    public HistoryWindow(ServerProfile profile, Window? owner)
    {
        Title = "История — " + profile.Name; Width = 1180; Height = 780; MinWidth = 720; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Owner = owner;
        SetResourceReference(BackgroundProperty, "Paper");
        View = new HistoryView(profile.Id);
        Content = View;
        Closed += (_, _) => View.Dispose();
    }
}
