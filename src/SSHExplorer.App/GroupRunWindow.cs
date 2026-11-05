using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using SSHExplorer.Core;

namespace SSHExplorer.App;

/// <summary>One server of a group run: whether it takes part and how it went.</summary>
public sealed class GroupRunRow(ServerProfile profile, bool selected) : ViewModels.ObservableObject
{
    bool selected = selected; GroupRunResult? result;
    public ServerProfile Profile => profile;
    public bool Selected { get => selected; set => Set(ref selected, value); }
    public GroupRunResult? Result { get => result; set { if (Set(ref result, value)) { Raise(nameof(State)); Raise(nameof(Code)); Raise(nameof(Time)); Raise(nameof(Summary)); } } }
    public string Name => profile.Name;
    public string Group => profile.Group.Length > 0 ? profile.Group : "—";
    public string Address => profile.Username + "@" + profile.Endpoint;
    public string State => result?.State switch
    {
        GroupRunState.Waiting => "В очереди", GroupRunState.Connecting => "Подключение…", GroupRunState.Running => "Выполняется…", GroupRunState.Succeeded => "✓ Готово",
        GroupRunState.Failed => "✗ Ошибка", GroupRunState.Skipped => "Пропущен", GroupRunState.Cancelled => "Остановлено", _ => "",
    };
    public string Code => result?.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
    public string Time => result is { State: not (GroupRunState.Waiting or GroupRunState.Connecting or GroupRunState.Running) } r ? r.Duration.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture) + " с" : "";
    public string Summary => result?.Problem ?? LastLine(result?.Output ?? "");
    static string LastLine(string text) => text.TrimEnd().Split('\n').LastOrDefault()?.Trim() ?? "";
}

/// <summary>
/// «Выполнить на нескольких серверах»: one script (saved or typed here) on the chosen servers in parallel, a table of
/// results with each server's output, and a rerun on the failed ones. Saved credentials only; unknown host keys are
/// never accepted here.
/// </summary>
public sealed class GroupRunWindow : Window
{
    const string Custom = "Свои команды";
    readonly GroupRunner runner;
    readonly ComboBox source = new() { Name = "GroupRunSource", MinHeight = 32 }, parallel = new() { Name = "GroupRunParallel", MinHeight = 32, ItemsSource = new[] { "1", "2", "4", "8" }, SelectedIndex = 2 };
    readonly TextBox body = new() { Name = "GroupRunBody", AcceptsReturn = true, MinHeight = 90, MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
    readonly TextBox timeout = new() { Name = "GroupRunTimeout", MinHeight = 32, Width = 70, Text = "10" };
    readonly CheckBox stopOnError = new() { Name = "GroupRunStopOnError", IsChecked = true, Content = "Останавливаться на первой ошибке (set -e)", Margin = new Thickness(0, 6, 0, 0) };
    readonly TextBox output = new() { Name = "GroupRunOutput", IsReadOnly = true, MinHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Cascadia Mono, Consolas"), TextWrapping = TextWrapping.NoWrap };
    readonly TextBlock problem = Ui.Text("", 12), summary = Ui.Text("", 12);
    readonly DataGrid table;
    readonly Button start, retry, stop;
    readonly IReadOnlyList<SavedScript> saved;
    CancellationTokenSource? running;
    public ObservableCollection<GroupRunRow> Rows { get; }
    internal Func<string, bool>? ConfirmOverride { get; set; }
    internal string? Problem => problem.Text.Length > 0 ? problem.Text : null;
    internal string Summary => summary.Text;
    internal string Output => output.Text;
    internal Button StartButton => start;
    internal Button RetryButton => retry;
    internal DataGrid Table => table;
    internal void UseCommands(string text) { source.SelectedItem = Custom; body.Text = text; Update(); }
    internal Task Running { get; private set; } = Task.CompletedTask;

    public GroupRunWindow(IReadOnlyList<ServerProfile> profiles, IReadOnlyCollection<Guid> preselected, GroupRunner runner)
    {
        this.runner = runner;
        Title = "Выполнить на нескольких серверах"; Width = 1040; Height = 760; MinWidth = 760; MinHeight = 560; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(StyleProperty, "DialogWindowStyle");
        Rows = new(profiles.OrderBy(p => p.Group, StringComparer.CurrentCultureIgnoreCase).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).Select(p => new GroupRunRow(p, preselected.Contains(p.Id))));
        saved = profiles.SelectMany(p => p.Scripts).GroupBy(s => (s.Name, s.Body)).Select(g => g.First()).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var layout = new Grid { Margin = new Thickness(24, 20, 24, 12) };
        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, new GridLength(0.7, GridUnitType.Star) }) layout.RowDefinitions.Add(new RowDefinition { Height = h });
        var head = new StackPanel(); head.Children.Add(Ui.DialogHeading("Выполнить на нескольких серверах"));
        head.Children.Add(Muted("Каждый сервер — отдельное новое подключение с сохранёнными данными входа. Серверы без сохранённого пароля пропускаются; неподтверждённый ключ сервера здесь не принимается.", new Thickness(0, 2, 0, 10)));
        layout.Children.Add(head);

        var script = new Grid { Margin = new Thickness(0, 0, 0, 10) }; Grid.SetRow(script, 1);
        script.ColumnDefinitions.Add(new ColumnDefinition()); script.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) }); script.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        var left = new StackPanel(); left.Children.Add(Label("Что выполнить")); source.ItemsSource = new[] { Custom }.Concat(saved.Select(s => s.Name)).ToArray(); source.SelectedIndex = 0; AutomationProperties.SetName(source, "Что выполнить"); left.Children.Add(source);
        body.Margin = new Thickness(0, 6, 0, 0); AutomationProperties.SetName(body, "Команды"); left.Children.Add(body); script.Children.Add(left);
        var right = new StackPanel(); Grid.SetColumn(right, 2);
        right.Children.Add(Label("Одновременно серверов")); AutomationProperties.SetName(parallel, "Одновременно серверов"); right.Children.Add(parallel);
        var t = Label("Таймаут на сервер, мин"); t.Margin = new Thickness(0, 8, 0, 4); right.Children.Add(t); AutomationProperties.SetName(timeout, "Таймаут на сервер, минут"); timeout.HorizontalAlignment = HorizontalAlignment.Left; right.Children.Add(timeout);
        right.Children.Add(stopOnError);
        script.Children.Add(right); layout.Children.Add(script);

        table = new DataGrid { Name = "GroupRunTable", AutoGenerateColumns = false, CanUserAddRows = false, HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Single, ItemsSource = Rows };
        table.SetResourceReference(StyleProperty, "ProcessTableStyle"); Grid.SetRow(table, 2);
        table.Columns.Add(new DataGridCheckBoxColumn { Header = "", Binding = new Binding(nameof(GroupRunRow.Selected)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 44 });
        MainWindow.AddColumn(table, "Сервер", nameof(GroupRunRow.Name), 180); MainWindow.AddColumn(table, "Группа", nameof(GroupRunRow.Group), 110); MainWindow.AddColumn(table, "Адрес", nameof(GroupRunRow.Address), 190);
        MainWindow.AddColumn(table, "Состояние", nameof(GroupRunRow.State), 120); MainWindow.AddColumn(table, "Код", nameof(GroupRunRow.Code), 56); MainWindow.AddColumn(table, "Время", nameof(GroupRunRow.Time), 70);
        MainWindow.AddColumn(table, "Итог", nameof(GroupRunRow.Summary), 200); table.Columns[^1].Width = new DataGridLength(1, DataGridLengthUnitType.Star);
        table.IsReadOnly = false; foreach (var c in table.Columns.Skip(1)) c.IsReadOnly = true;
        layout.Children.Add(table);

        var mid = new DockPanel { Margin = new Thickness(0, 8, 0, 4) }; Grid.SetRow(mid, 3);
        var tools = new WrapPanel(); DockPanel.SetDock(tools, Dock.Left);
        var all = Ui.Button("Выбрать все", () => { foreach (var r in Rows) r.Selected = true; }); all.Margin = new Thickness(0, 0, 8, 0); tools.Children.Add(all);
        var none = Ui.Button("Снять все", () => { foreach (var r in Rows) r.Selected = false; }); none.Margin = new Thickness(0, 0, 12, 0); tools.Children.Add(none);
        mid.Children.Add(tools);
        summary.Name = "GroupRunSummary"; summary.VerticalAlignment = VerticalAlignment.Center; summary.TextWrapping = TextWrapping.Wrap; mid.Children.Add(summary);
        layout.Children.Add(mid);
        var bottom = new DockPanel(); Grid.SetRow(bottom, 4);
        problem.Name = "GroupRunProblem"; problem.TextWrapping = TextWrapping.Wrap; problem.Margin = new Thickness(0, 6, 0, 0); problem.SetResourceReference(TextBlock.ForegroundProperty, "DangerText"); DockPanel.SetDock(problem, Dock.Bottom); bottom.Children.Add(problem);
        var outLabel = Label("Вывод выбранного сервера"); DockPanel.SetDock(outLabel, Dock.Top); bottom.Children.Add(outLabel);
        AutomationProperties.SetName(output, "Вывод выбранного сервера"); bottom.Children.Add(output);
        layout.Children.Add(bottom);

        stop = Ui.Button("Остановить", () => running?.Cancel()); stop.Name = "GroupRunStop"; stop.IsEnabled = false;
        retry = Ui.Button("Повторить на неудачных", () => Start(retryOnly: true)); retry.Name = "GroupRunRetry"; retry.IsEnabled = false;
        start = Ui.Button("Запустить", () => Start(retryOnly: false)); start.Name = "GroupRunStart"; start.IsDefault = true; start.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        var close = Ui.Button("Закрыть", () => { running?.Cancel(); DialogResult = false; }); close.IsCancel = true;
        Content = Ui.DialogSurface(layout, close, stop, retry, start);
        source.SelectionChanged += (_, _) => { if (source.SelectedItem is string name && saved.FirstOrDefault(s => s.Name == name) is { } s) { body.Text = s.Body; stopOnError.IsChecked = s.StopOnError; timeout.Text = s.TimeoutMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture); } Update(); };
        body.TextChanged += (_, _) => Update(); timeout.TextChanged += (_, _) => Update();
        foreach (var r in Rows) r.PropertyChanged += (_, _) => Update();
        table.SelectionChanged += (_, _) => ShowOutput();
        Closing += (_, _) => running?.Cancel();
        Update();
    }

    static TextBlock Label(string text) { var l = Ui.Text(text, 12); l.Margin = new Thickness(0, 0, 0, 4); l.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); return l; }
    static TextBlock Muted(string text, Thickness margin) { var t = Ui.Text(text, 12); t.TextWrapping = TextWrapping.Wrap; t.Margin = margin; t.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); return t; }

    SavedScript Script() => new()
    {
        Name = source.SelectedItem as string is { } n && n != Custom ? n : "Команды на группе", Body = body.Text, StopOnError = stopOnError.IsChecked == true,
        TimeoutMinutes = int.TryParse(timeout.Text, out var m) ? m : 0,
    };

    void Update()
    {
        if (running is not null) return;
        int n = Rows.Count(r => r.Selected);
        string? error = body.Text.Trim().Length == 0 ? "Введите команды или выберите сохранённый скрипт." : n == 0 ? "Отметьте серверы." : GroupRunner.Validate(Script());
        problem.Text = error is not null && body.Text.Trim().Length > 0 ? error : "";
        start.IsEnabled = error is null; start.Content = n > 0 ? $"Запустить ({n})" : "Запустить";
        retry.IsEnabled = error is null && Rows.Any(r => r.Result?.Retryable == true);
    }

    void ShowOutput()
    {
        if (table.SelectedItem is not GroupRunRow row || row.Result is not { } r) { output.Text = ""; return; }
        output.Text = (r.Problem is { } p ? p + "\n\n" : "") + r.Output;
    }

    void Start(bool retryOnly)
    {
        var targets = Rows.Where(r => retryOnly ? r.Result?.Retryable == true : r.Selected).ToArray();
        if (targets.Length == 0) return;
        var script = Script();
        var preview = string.Join("\n", script.Body.Split('\n').Take(6)) + (script.Body.Split('\n').Length > 6 ? "\n…" : "");
        if (!(ConfirmOverride?.Invoke(preview) ?? Ui.Confirm($"Выполнить на {targets.Length} серверах ({string.Join(", ", targets.Take(5).Select(t => t.Name))}{(targets.Length > 5 ? ", …" : "")})?\n\n{preview}", "Выполнить на нескольких серверах"))) return;
        Running = RunAsync(targets, script);
    }

    async Task RunAsync(IReadOnlyList<GroupRunRow> targets, SavedScript script)
    {
        running = new CancellationTokenSource(); start.IsEnabled = retry.IsEnabled = false; stop.IsEnabled = true; problem.Text = "";
        foreach (var row in targets) row.Result = null;
        var byId = targets.ToDictionary(r => r.Profile.Id);
        runner.Parallelism = int.Parse((string)parallel.SelectedItem, System.Globalization.CultureInfo.InvariantCulture);
        void Report(GroupRunResult r) => Dispatcher.InvokeAsync(() => { if (byId.TryGetValue(r.ProfileId, out var row)) { row.Result = r; if (ReferenceEquals(table.SelectedItem, row)) ShowOutput(); Count(); } });
        try { await Task.Run(() => runner.RunAsync(targets.Select(t => t.Profile).ToArray(), script, Report, running.Token)); }
        catch (ArgumentException ex) { problem.Text = ex.Message; }
        finally
        {
            await Dispatcher.InvokeAsync(() => { });
            running.Dispose(); running = null; stop.IsEnabled = false; Count(); Update();
            if (table.SelectedItem is null) table.SelectedItem = targets.FirstOrDefault(t => t.Result?.State != GroupRunState.Succeeded) ?? targets.FirstOrDefault();
            ShowOutput();
        }
    }

    void Count()
    {
        var done = Rows.Where(r => r.Result is not null).ToArray();
        int ok = done.Count(r => r.Result!.State == GroupRunState.Succeeded), bad = done.Count(r => r.Result!.State == GroupRunState.Failed), skipped = done.Count(r => r.Result!.State == GroupRunState.Skipped);
        int active = done.Count(r => r.Result!.State is GroupRunState.Waiting or GroupRunState.Connecting or GroupRunState.Running), stopped = done.Count(r => r.Result!.State == GroupRunState.Cancelled);
        summary.Text = $"Готово: {ok} · ошибок: {bad}" + (skipped > 0 ? $" · пропущено: {skipped}" : "") + (stopped > 0 ? $" · остановлено: {stopped}" : "") + (active > 0 ? $" · идёт: {active}" : "");
    }
}
