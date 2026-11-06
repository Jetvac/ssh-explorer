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
/// user presses «Подтвердить».
/// </summary>
public sealed class CustomMetricsWindow : Window
{
    readonly ObservableCollection<CustomMetricDefinition> items;
    readonly ListBox list = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    readonly TextBox name = new(), command = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 110, FontFamily = new FontFamily("Cascadia Mono, Consolas"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, unit = new();
    readonly ComboBox interval = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly TextBlock result = Ui.Text("", 12), problem = Ui.Text("", 12), commandLabel;
    readonly Border unconfirmed = new() { CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 12), BorderThickness = new Thickness(1) };
    readonly ServerSession? session;
    readonly bool windows;
    readonly StackPanel editor = new(), empty = new() { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 440, Margin = new Thickness(8, 0, 8, 40) };
    readonly Button remove;
    bool loading;
    static readonly (string Label, int Seconds)[] Intervals = [("1 минута", 60), ("5 минут", 300), ("15 минут", 900), ("1 час", 3600), ("1 день", 86400)];

    /// <summary>Test hook: answers the dialog instead of a user (receives the window, returns its result).</summary>
    internal static Func<CustomMetricsWindow, bool?>? DialogOverride { get; set; }
    internal IReadOnlyList<CustomMetricDefinition> Items => items;
    internal bool EmptyStateVisible => empty.Visibility == Visibility.Visible;
    internal bool UnconfirmedVisible => unconfirmed.Visibility == Visibility.Visible;

    CustomMetricsWindow(IEnumerable<CustomMetricDefinition> source, ServerSession? session, bool windows, string title)
    {
        this.session = session; this.windows = windows;
        items = new(source.Select(m => m.Clone()));
        Title = title; Width = 840; Height = 580; MinWidth = 660; MinHeight = 480; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(StyleProperty, "DialogWindowStyle");
        list.ItemsSource = items; list.DisplayMemberPath = nameof(CustomMetricDefinition.Name); list.Name = "CustomMetricList";
        list.SelectionChanged += (_, _) => ShowMetric(list.SelectedItem as CustomMetricDefinition);
        foreach (var (label, seconds) in Intervals) interval.Items.Add(new ComboBoxItem { Content = label, Tag = seconds });

        // Left: the list in its own card with its commands at the bottom of that card.
        var add = Ui.FlyoutButton(Ui.AddGlyph, "Добавить", AddMenu, "Новая метрика: шаблон или своя команда"); add.Name = "CustomMetricAdd";
        remove = Ui.IconButton(Ui.DeleteGlyph, Remove, "Удалить выбранную метрику"); remove.Name = "CustomMetricRemove";
        var listCommands = new DockPanel { Margin = new Thickness(6, 4, 6, 6) }; DockPanel.SetDock(remove, Dock.Right); listCommands.Children.Add(remove); listCommands.Children.Add(add);
        var listTitle = Ui.Text("Метрики", 12, true); listTitle.Margin = new Thickness(12, 10, 12, 4); listTitle.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var left = new DockPanel(); DockPanel.SetDock(listTitle, Dock.Top); left.Children.Add(listTitle);
        var line = new Border { Height = 1 }; line.SetResourceReference(Border.BackgroundProperty, "Edge"); DockPanel.SetDock(line, Dock.Bottom);
        DockPanel.SetDock(listCommands, Dock.Bottom); left.Children.Add(listCommands); left.Children.Add(line); left.Children.Add(list);
        var card = new Border { Child = left, Width = 220, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 18, 0) };
        card.SetResourceReference(Border.BackgroundProperty, "Surface"); card.SetResourceReference(Border.BorderBrushProperty, "Edge");

        // Right: the editor, or — while there is nothing to edit — what a custom metric is and the usual starting points.
        name.Name = "CustomMetricName"; command.Name = "CustomMetricCommand"; unit.Name = "CustomMetricUnit";
        unconfirmed.Name = "CustomMetricUnconfirmed"; unconfirmed.SetResourceReference(Border.BorderBrushProperty, "ValidationInk");
        var warn = Ui.Text("Команда пришла из файла и не запускается, пока вы её не прочитаете и не подтвердите.", 12); warn.TextWrapping = TextWrapping.Wrap; warn.VerticalAlignment = VerticalAlignment.Center;
        var confirm = Ui.Button("Подтвердить", Confirm); confirm.Name = "CustomMetricConfirm"; confirm.Margin = new Thickness(12, 0, 0, 0); confirm.VerticalAlignment = VerticalAlignment.Center;
        var warnRow = new DockPanel(); DockPanel.SetDock(confirm, Dock.Right); warnRow.Children.Add(confirm); warnRow.Children.Add(warn); unconfirmed.Child = warnRow;
        editor.Children.Add(unconfirmed);
        editor.Children.Add(Label("Имя")); editor.Children.Add(Spaced(name));
        commandLabel = Label(windows ? "Команда PowerShell на сервере" : "Команда на сервере (sh)"); editor.Children.Add(commandLabel); editor.Children.Add(Spaced(command, 4));
        var note = Ui.Text("Выводит одно число или строки «имя=значение». Выполняется с правами пользователя SSH; приложение не добавляет sudo.", 12);
        note.TextWrapping = TextWrapping.Wrap; note.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); editor.Children.Add(Spaced(note, 14));
        var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) }); row.ColumnDefinitions.Add(new ColumnDefinition());
        var unitBox = new StackPanel(); unitBox.Children.Add(Label("Единица (необязательно)")); unitBox.Children.Add(unit); row.Children.Add(unitBox);
        var intervalBox = new StackPanel(); intervalBox.Children.Add(Label("Как часто")); intervalBox.Children.Add(interval); Grid.SetColumn(intervalBox, 2); row.Children.Add(intervalBox);
        editor.Children.Add(Spaced(row, 16));
        var test = Ui.AsyncButton("Проверить на сервере", TestAsync, session is null ? "Проверка доступна из «Истории» открытой вкладки сервера" : "Выполнить команду один раз и показать, что будет записано");
        test.IsEnabled = session is not null; test.HorizontalAlignment = HorizontalAlignment.Left; test.Name = "CustomMetricTest";
        editor.Children.Add(test);
        if (session is null) { var why = Ui.Text("Проверить команду можно из «Истории» открытой вкладки сервера.", 12); why.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); why.Margin = new Thickness(0, 6, 0, 0); editor.Children.Add(why); }
        result.TextWrapping = TextWrapping.Wrap; result.Name = "CustomMetricResult"; result.Margin = new Thickness(0, 8, 0, 0); editor.Children.Add(result);
        problem.TextWrapping = TextWrapping.Wrap; problem.SetResourceReference(TextBlock.ForegroundProperty, "ValidationInk"); problem.Margin = new Thickness(0, 6, 0, 0); editor.Children.Add(problem);

        empty.Name = "CustomMetricEmpty";
        var emptyTitle = Ui.Text("Своих метрик пока нет", 16, true); emptyTitle.Margin = new Thickness(0, 0, 0, 8); empty.Children.Add(emptyTitle);
        var emptyText = Ui.Text("Своя метрика — команда на сервере, вывод которой записывается в историю рядом с ЦП и памятью: очередь, соединения nginx, время ответа сайта. Начните с шаблона:", 13);
        emptyText.TextWrapping = TextWrapping.Wrap; emptyText.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); emptyText.Margin = new Thickness(0, 0, 0, 12); empty.Children.Add(emptyText);
        var quick = new WrapPanel();
        foreach (var key in new[] { "nginx", "http", "service", "docker-cpu", "postgres" })
        {
            var t = CustomMetricTemplates.All.First(x => x.Key == key);
            var b = Ui.Button(t.Title, () => FromTemplate(t)); b.Margin = new Thickness(0, 0, 8, 8); quick.Children.Add(b);
        }
        var own = Ui.Button("Своя команда", AddOwn); own.Margin = new Thickness(0, 0, 8, 8); quick.Children.Add(own);
        empty.Children.Add(quick);

        name.TextChanged += (_, _) => Store(); command.TextChanged += (_, _) => Store(); unit.TextChanged += (_, _) => Store();
        interval.SelectionChanged += (_, _) => Store();

        var right = new Grid(); right.Children.Add(new ScrollViewer { Content = editor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }); right.Children.Add(empty);
        var body = new Grid { Margin = new Thickness(22, 18, 22, 16) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); body.ColumnDefinitions.Add(new ColumnDefinition());
        body.Children.Add(card); Grid.SetColumn(right, 1); body.Children.Add(right);
        var ok = Ui.Button("Готово", Accept); ok.IsDefault = true; ok.Name = "CustomMetricsOk"; ok.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        var cancel = Ui.Button("Отмена", () => DialogResult = false); cancel.IsCancel = true;
        var root = new DockPanel(); root.SetResourceReference(Panel.BackgroundProperty, "Paper");
        var footer = new Border { Child = Ui.Actions(cancel, ok) }; footer.SetResourceReference(StyleProperty, "DialogFooterStyle"); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        root.Children.Add(body);
        Content = root;
        if (items.Count > 0) list.SelectedIndex = 0; else ShowMetric(null);
    }

    static TextBlock Label(string text) { var t = Ui.Text(text, 13); t.Margin = new Thickness(0, 0, 0, 4); return t; }
    static FrameworkElement Spaced(FrameworkElement e, double bottom = 12) { e.Margin = new Thickness(0, 0, 0, bottom); return e; }

    CustomMetricDefinition? Current => list.SelectedItem as CustomMetricDefinition;

    void ShowMetric(CustomMetricDefinition? m)
    {
        loading = true;
        bool any = items.Count > 0;
        editor.Visibility = any && m is not null ? Visibility.Visible : Visibility.Collapsed;
        empty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        remove.IsEnabled = m is not null;
        unconfirmed.Visibility = m is { Approved: false } ? Visibility.Visible : Visibility.Collapsed;
        name.Text = m?.Name ?? ""; command.Text = m?.Command ?? ""; unit.Text = m?.Unit ?? "";
        interval.SelectedItem = interval.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == (m?.IntervalSeconds ?? 60)) ?? interval.Items[0];
        result.Text = ""; problem.Text = m?.Validate() ?? "";
        loading = false;
    }

    void Confirm() { if (Current is { } m) { m.Approved = true; ShowMetric(m); } }

    void Store()
    {
        if (loading || Current is not { } m) return;
        bool commandChanged = m.Command != command.Text, nameChanged = m.Name != name.Text.Trim();
        m.Name = name.Text.Trim(); m.Command = command.Text; m.Unit = unit.Text.Trim();
        m.IntervalSeconds = interval.SelectedItem is ComboBoxItem { Tag: int s } ? s : 60;
        problem.Text = m.Validate() ?? "";
        if (commandChanged) result.Text = "";
        if (nameChanged) list.Items.Refresh();
    }

    void AddOwn() => AddMetric(new CustomMetricDefinition { Name = Unique("metric"), Command = windows ? "(Get-Date).Second" : "echo 1", Approved = true });

    ContextMenu AddMenu()
    {
        var menu = new ContextMenu();
        foreach (var t in CustomMetricTemplates.All)
            menu.Items.Add(Ui.Menu(t.Title + (t.Parameter is null ? "" : "…"), () => FromTemplate(t)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.Menu("Своя команда", AddOwn));
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
        var header = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        foreach (var w in ColumnWidths) header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
        string[] headings = ["Вкл.", "Сервер", "Что", "Условие", "Порог", "Сколько", ""];
        for (int i = 0; i < headings.Length; i++) { var h = Ui.Text(headings[i], 12); h.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); Grid.SetColumn(h, i); header.Children.Add(h); }
        body.Children.Add(header);
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

    static readonly double[] ColumnWidths = [44, 190, 200, 90, 90, 110, 40];

    void AddRow(MetricAlertRule rule)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        foreach (var w in ColumnWidths) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
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
