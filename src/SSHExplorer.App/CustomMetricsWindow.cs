using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SSHExplorer.Core;
using SSHExplorer.Monitoring;

namespace SSHExplorer.App;

/// <summary>
/// Custom metrics of a server or a folder: a command, its unit and interval, made by hand or from a template, and a
/// test run on the connected server. Every command is shown in full; one that came from a file stays off until the
/// user ticks «Запускать».
/// </summary>
public sealed class CustomMetricsWindow : Window
{
    readonly ObservableCollection<CustomMetricDefinition> items;
    readonly ListBox list = new() { Width = 200, Margin = new Thickness(0, 0, 14, 0) };
    readonly TextBox name = new(), command = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 110, FontFamily = new FontFamily("Cascadia Mono, Consolas"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, unit = new();
    readonly ComboBox interval = new() { Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
    readonly CheckBox approved = new() { Content = "Запускать эту команду (я её прочитал)", Margin = new Thickness(0, 10, 0, 0) };
    readonly TextBlock result = Ui.Text("", 12), problem = Ui.Text("", 12);
    readonly ServerSession? session;
    readonly bool windows;
    readonly StackPanel editor = new();
    bool loading;
    static readonly (string Label, int Seconds)[] Intervals = [("1 минута", 60), ("5 минут", 300), ("15 минут", 900), ("1 час", 3600), ("1 день", 86400)];

    /// <summary>Test hook: answers the dialog instead of a user (receives the window, returns its result).</summary>
    internal static Func<CustomMetricsWindow, bool?>? DialogOverride { get; set; }
    internal IReadOnlyList<CustomMetricDefinition> Items => items;

    CustomMetricsWindow(IEnumerable<CustomMetricDefinition> source, ServerSession? session, bool windows, string title)
    {
        this.session = session; this.windows = windows;
        items = new(source.Select(m => m.Clone()));
        Title = title; Width = 820; Height = 600; MinWidth = 640; MinHeight = 460; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(StyleProperty, "DialogWindowStyle");
        list.ItemsSource = items; list.DisplayMemberPath = nameof(CustomMetricDefinition.Name); list.Name = "CustomMetricList";
        list.SelectionChanged += (_, _) => ShowMetric(list.SelectedItem as CustomMetricDefinition);
        foreach (var (label, seconds) in Intervals) interval.Items.Add(new ComboBoxItem { Content = label, Tag = seconds });

        var left = new DockPanel();
        var add = Ui.FlyoutButton(Ui.AddGlyph, "Добавить", AddMenu, "Новая метрика: своя команда или шаблон"); add.Name = "CustomMetricAdd"; DockPanel.SetDock(add, Dock.Bottom); add.Margin = new Thickness(0, 8, 14, 0);
        left.Children.Add(add); left.Children.Add(list);

        name.Name = "CustomMetricName"; command.Name = "CustomMetricCommand"; unit.Name = "CustomMetricUnit"; approved.Name = "CustomMetricApproved";
        editor.Children.Add(Ui.Text("Имя (латиница, цифры, _ и -)")); editor.Children.Add(Spaced(name));
        editor.Children.Add(Ui.Text(windows ? "Команда PowerShell на сервере" : "Команда на сервере (sh)")); editor.Children.Add(Spaced(command));
        var note = Ui.Text("Выводит одно число или строки «имя=значение». Выполняется с правами пользователя SSH раз в выбранный интервал; приложение не добавляет sudo.", 12);
        note.TextWrapping = TextWrapping.Wrap; note.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); editor.Children.Add(Spaced(note));
        var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) }); row.ColumnDefinitions.Add(new ColumnDefinition());
        var unitBox = new StackPanel(); unitBox.Children.Add(Ui.Text("Единица (%, мс, МБ…)")); unitBox.Children.Add(unit); row.Children.Add(unitBox);
        var intervalBox = new StackPanel(); intervalBox.Children.Add(Ui.Text("Как часто")); intervalBox.Children.Add(interval); Grid.SetColumn(intervalBox, 2); row.Children.Add(intervalBox);
        editor.Children.Add(Spaced(row));
        editor.Children.Add(approved);
        var test = Ui.AsyncButton("Проверить на сервере", TestAsync, session is null ? "Откройте вкладку сервера, чтобы проверить команду" : "Выполнить команду один раз и показать разобранный результат");
        test.IsEnabled = session is not null; test.HorizontalAlignment = HorizontalAlignment.Left; test.Margin = new Thickness(0, 12, 0, 6); test.Name = "CustomMetricTest";
        var remove = Ui.Button("Удалить метрику", Remove); remove.HorizontalAlignment = HorizontalAlignment.Left; remove.Margin = new Thickness(0, 4, 0, 6);
        editor.Children.Add(Ui.Row(test, remove));
        result.TextWrapping = TextWrapping.Wrap; result.Name = "CustomMetricResult"; editor.Children.Add(result);
        problem.TextWrapping = TextWrapping.Wrap; problem.SetResourceReference(TextBlock.ForegroundProperty, "ValidationInk"); editor.Children.Add(problem);

        name.TextChanged += (_, _) => Store(); command.TextChanged += (_, _) => Store(); unit.TextChanged += (_, _) => Store();
        interval.SelectionChanged += (_, _) => Store(); approved.Click += (_, _) => Store();

        var body = new Grid { Margin = new Thickness(22, 18, 22, 12) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); body.ColumnDefinitions.Add(new ColumnDefinition());
        body.Children.Add(left); Grid.SetColumn(editor, 1); body.Children.Add(editor);
        var ok = Ui.Button("Готово", Accept); ok.IsDefault = true; ok.Name = "CustomMetricsOk";
        var cancel = Ui.Button("Отмена", () => DialogResult = false); cancel.IsCancel = true;
        Content = Ui.DialogSurface(body, ok, cancel);
        if (items.Count > 0) list.SelectedIndex = 0; else ShowMetric(null);
    }

    static FrameworkElement Spaced(FrameworkElement e) { e.Margin = new Thickness(0, 4, 0, 10); return e; }

    CustomMetricDefinition? Current => list.SelectedItem as CustomMetricDefinition;

    void ShowMetric(CustomMetricDefinition? m)
    {
        loading = true;
        editor.IsEnabled = m is not null;
        name.Text = m?.Name ?? ""; command.Text = m?.Command ?? ""; unit.Text = m?.Unit ?? ""; approved.IsChecked = m?.Approved ?? false;
        interval.SelectedItem = interval.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == (m?.IntervalSeconds ?? 60)) ?? interval.Items[0];
        result.Text = ""; problem.Text = m?.Validate() ?? "";
        loading = false;
    }

    void Store()
    {
        if (loading || Current is not { } m) return;
        bool commandChanged = m.Command != command.Text, nameChanged = m.Name != name.Text.Trim();
        m.Name = name.Text.Trim(); m.Command = command.Text; m.Unit = unit.Text.Trim();
        m.IntervalSeconds = interval.SelectedItem is ComboBoxItem { Tag: int s } ? s : 60;
        m.Approved = approved.IsChecked == true;
        problem.Text = m.Validate() ?? "";
        if (commandChanged) result.Text = "";
        if (nameChanged) list.Items.Refresh();
    }

    ContextMenu AddMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(Ui.Menu("Своя команда…", () => AddMetric(new CustomMetricDefinition { Name = Unique("metric"), Command = windows ? "(Get-Date).Second" : "echo 1", Approved = true })));
        menu.Items.Add(new Separator());
        foreach (var t in CustomMetricTemplates.All)
            menu.Items.Add(Ui.Menu(t.Title + (t.Parameter is null ? "" : "…"), () => FromTemplate(t)));
        return menu;
    }

    /// <summary>Test hook: the parameter of a template (null — cancel).</summary>
    internal static Func<CustomMetricTemplate, string?>? TemplateValueOverride { get; set; }

    internal void FromTemplate(CustomMetricTemplate t)
    {
        string? value = null;
        if (t.Parameter is not null)
        {
            value = TemplateValueOverride is { } fake ? fake(t) : Ui.Prompt(t.Title, t.Parameter + ":", t.DefaultValue ?? "");
            if (value is null) return;
        }
        AddMetric(new CustomMetricDefinition { Name = Unique(t.Name), Command = t.Command(windows, value), Unit = t.Unit, Template = t.Key, Approved = true });
    }

    string Unique(string baseName)
    {
        var n = CustomMetricTemplates.SafeName(baseName);
        if (items.All(i => i.Name != n)) return n;
        for (int i = 2; ; i++) if (items.All(x => x.Name != n + "_" + i.ToString(CultureInfo.InvariantCulture))) return n + "_" + i.ToString(CultureInfo.InvariantCulture);
    }

    void AddMetric(CustomMetricDefinition m) { items.Add(m); list.SelectedItem = m; }

    void Remove()
    {
        if (Current is not { } m) return;
        int index = list.SelectedIndex; items.Remove(m);
        if (items.Count > 0) list.SelectedIndex = Math.Min(index, items.Count - 1); else ShowMetric(null);
    }

    internal async Task TestAsync()
    {
        if (session is null || Current is not { } m) return;
        if (m.Validate() is { } error) { result.Text = error; return; }
        result.Text = "Выполняется…";
        var sampler = MetricsHub.SamplerFor(session);
        var r = await sampler.RunCustomAsync(new CustomMetricSpec(m.Id, m.Name, m.Command, m.Unit, m.IntervalSeconds), session.Lifetime);
        result.Text = r.Error is { } e ? "Ошибка: " + e : "Получено: " + string.Join(", ", r.Values.Select(v => MetricSeries.Label(v.Name) + " = " + MetricSeries.Format(v.Value, v.Unit)));
    }

    void Accept()
    {
        var bad = items.FirstOrDefault(m => m.Validate() is not null);
        if (bad is not null) { list.SelectedItem = bad; ShowMetric(bad); return; }
        var duplicate = items.GroupBy(m => m.Name).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) { problem.Text = $"Имя «{duplicate.Key}» используется дважды."; return; }
        if (IsLoaded && IsVisible) DialogResult = true;
    }

    /// <summary>Edits a list; null when cancelled.</summary>
    public static IReadOnlyList<CustomMetricDefinition>? Edit(Window? owner, IEnumerable<CustomMetricDefinition> source, ServerSession? session, bool windows, string title)
    {
        var w = new CustomMetricsWindow(source, session, windows, title) { Owner = owner };
        var answer = DialogOverride is { } fake ? fake(w) : w.ShowDialog();
        return answer == true ? w.items.ToArray() : null;
    }
}

/// <summary>Alert rules on history: a series beyond a threshold for some minutes; the disk forecast.</summary>
public sealed class MetricAlertsWindow : Window
{
    sealed class Row
    {
        public MetricAlertRule Rule { get; init; } = new();
        public Grid View { get; init; } = new();
    }

    readonly StackPanel rows = new();
    readonly List<Row> entries = [];
    readonly TextBox forecast = new() { Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
    readonly TextBlock problem = Ui.Text("", 12);
    readonly Guid? focus;
    internal static Func<MetricAlertsWindow, bool?>? DialogOverride { get; set; }

    MetricAlertsWindow(Guid? server)
    {
        focus = server;
        Title = "Оповещения по истории"; Width = 860; Height = 560; MinWidth = 700; MinHeight = 380; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(StyleProperty, "DialogWindowStyle");
        var m = App.Store.Settings.Metrics;
        var body = new StackPanel { Margin = new Thickness(22, 18, 22, 12) };
        var intro = Ui.Text("Оповещение появляется, когда значение держится выше или ниже порога всё указанное время (по записанной истории). Работает для открытых вкладок и серверов с записью в фоне.", 12);
        intro.TextWrapping = TextWrapping.Wrap; intro.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); intro.Margin = new Thickness(0, 0, 0, 12); body.Children.Add(intro);
        body.Children.Add(rows);
        var add = Ui.Button("Добавить правило", () => AddRow(new MetricAlertRule { ServerId = focus })); add.HorizontalAlignment = HorizontalAlignment.Left; add.Margin = new Thickness(0, 8, 0, 16); add.Name = "MetricAlertAdd";
        body.Children.Add(add);
        body.Children.Add(Ui.Text("Прогноз заполнения дисков: предупредить, если диск заполнится за (дней, 0 — выключено)"));
        forecast.Text = m.ForecastDays.ToString(CultureInfo.CurrentCulture); forecast.Margin = new Thickness(0, 4, 0, 10); forecast.Name = "MetricForecastDays"; body.Children.Add(forecast);
        problem.SetResourceReference(TextBlock.ForegroundProperty, "ValidationInk"); problem.TextWrapping = TextWrapping.Wrap; body.Children.Add(problem);
        foreach (var r in m.Rules) AddRow(r.Clone());
        if (entries.Count == 0) AddRow(new MetricAlertRule { ServerId = focus, Series = MetricSeries.Cpu, Threshold = 90, Minutes = 15, Enabled = false });
        var ok = Ui.AsyncButton("Сохранить", SaveAsync); ok.IsDefault = true; ok.Name = "MetricAlertsSave";
        var cancel = Ui.Button("Отмена", () => DialogResult = false); cancel.IsCancel = true;
        Content = Ui.DialogSurface(body, ok, cancel);
    }

    internal static readonly (string Series, string Label)[] SeriesChoices =
    [
        (MetricSeries.Cpu, "ЦП, %"), (MetricSeries.Memory, "Память, %"), (MetricSeries.Swap, "Подкачка, %"), (MetricSeries.Load1, "Средняя нагрузка"),
        ("fs:*", "Любой диск, % заполнения"), (MetricSeries.NetReceive, "Сеть: получение, Б/с"), (MetricSeries.NetSend, "Сеть: отправка, Б/с"),
    ];

    void AddRow(MetricAlertRule rule)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        foreach (var w in new[] { 34d, 190, 190, 80, 90, 120, 40 }) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
        var enabled = new CheckBox { IsChecked = rule.Enabled, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Правило включено" };
        var server = new ComboBox { Margin = new Thickness(0, 0, 6, 0) };
        server.Items.Add(new ComboBoxItem { Content = "Все серверы", Tag = null });
        foreach (var p in App.Store.Profiles.Where(p => !p.IsRdp).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)) server.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Id });
        server.SelectedItem = server.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, rule.ServerId)) ?? server.Items[0];
        var series = new ComboBox { IsEditable = true, Margin = new Thickness(0, 0, 6, 0), ToolTip = "Ряд: из списка или имя своей метрики (custom:имя)" };
        foreach (var (s, label) in SeriesChoices) series.Items.Add(new ComboBoxItem { Content = label, Tag = s });
        series.Text = SeriesChoices.FirstOrDefault(c => c.Series == rule.Series).Label ?? rule.Series;
        var direction = new ComboBox { Margin = new Thickness(0, 0, 6, 0) }; direction.Items.Add("выше"); direction.Items.Add("ниже"); direction.SelectedIndex = rule.Above ? 0 : 1;
        var threshold = new TextBox { Text = rule.Threshold.ToString(CultureInfo.CurrentCulture), Margin = new Thickness(0, 0, 6, 0), ToolTip = "Порог" };
        var minutes = new TextBox { Text = rule.Minutes.ToString(CultureInfo.CurrentCulture), Margin = new Thickness(0, 0, 6, 0), ToolTip = "Минут подряд" };
        var remove = Ui.IconButton(Ui.DeleteGlyph, () => { rows.Children.Remove(grid); entries.RemoveAll(e => ReferenceEquals(e.View, grid)); }, "Удалить правило");
        var minutesLabel = Ui.Text("мин", 12); minutesLabel.VerticalAlignment = VerticalAlignment.Center;
        var cells = new FrameworkElement[] { enabled, server, series, direction, threshold, Ui.Row(minutes, minutesLabel), remove };
        minutes.Width = 60;
        for (int i = 0; i < cells.Length; i++) { Grid.SetColumn(cells[i], i); grid.Children.Add(cells[i]); }
        grid.Tag = (enabled, server, series, direction, threshold, minutes);
        rows.Children.Add(grid);
        entries.Add(new Row { Rule = rule, View = grid });
    }

    /// <summary>Reads the rows back; null and a message when one is wrong.</summary>
    internal List<MetricAlertRule>? Collect(out string? error)
    {
        error = null; var list = new List<MetricAlertRule>();
        foreach (var e in entries)
        {
            var (enabled, server, series, direction, threshold, minutes) = ((CheckBox, ComboBox, ComboBox, ComboBox, TextBox, TextBox))e.View.Tag;
            var text = series.Text.Trim();
            var key = SeriesChoices.FirstOrDefault(c => c.Label == text).Series ?? text;
            if (key.Length == 0) { error = "Укажите ряд для каждого правила."; return null; }
            if (!double.TryParse(threshold.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var t) && !double.TryParse(threshold.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out t)) { error = $"Порог «{threshold.Text}» — не число."; return null; }
            if (!int.TryParse(minutes.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var min) || min is < 1 or > 1440) { error = "Минуты — от 1 до 1440."; return null; }
            list.Add(new MetricAlertRule { Id = e.Rule.Id, Enabled = enabled.IsChecked == true, ServerId = (server.SelectedItem as ComboBoxItem)?.Tag as Guid?, Series = key, Above = direction.SelectedIndex == 0, Threshold = t, Minutes = min });
        }
        return list;
    }

    internal async Task SaveAsync()
    {
        var rules = Collect(out var error);
        if (rules is null) { problem.Text = error; return; }
        if (!int.TryParse(forecast.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var days) || days is < 0 or > 365) { problem.Text = "Прогноз — от 0 до 365 дней."; return; }
        var m = App.Store.Settings.Metrics;
        m.Rules = rules; m.ForecastDays = days;
        try { await App.Store.SaveAsync(); } catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { problem.Text = "Не удалось сохранить: " + ex.Message; return; }
        if (IsLoaded && IsVisible) DialogResult = true;
    }

    public static void Open(Window? owner, Guid? server)
    {
        var w = new MetricAlertsWindow(server) { Owner = owner };
        if (DialogOverride is { } fake) fake(w); else w.ShowDialog();
    }
}
