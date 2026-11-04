using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using SSHExplorer.App.ViewModels;
using SSHExplorer.Core;

namespace SSHExplorer.App;

/// <summary>One row of the scripts table.</summary>
public sealed class ScriptRow(SavedScript script, Func<Guid, bool> running) : ObservableObject
{
    public SavedScript Script => script;
    public string Name => script.Name;
    public string Schedule => script.Schedule;
    public string LastRun => script.LastRunUtc is { } t ? t.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture) : "—";
    public string Result => running(script.Id) ? "Выполняется…" : script.LastExitCode is null ? "—" : script.LastExitCode == 0 ? "Успешно" : $"Ошибка (код {script.LastExitCode})";
    public int ResultRank => running(script.Id) ? 0 : script.LastExitCode is null ? 3 : script.LastExitCode == 0 ? 2 : 1;
    public void Refresh() => Raise(null);
}

/// <summary>
/// «Скрипты» module: saved command sequences of this server — run now, on connect, every N minutes or daily — with the
/// output of their runs. Automatic runs happen while the app is open and this server is connected.
/// </summary>
public sealed class ScriptsView : UserControl
{
    readonly ScriptRunner runner;
    readonly ServerSession session;
    readonly ObservableCollection<ScriptRow> rows = [];
    readonly Dictionary<string, Button> commands = [];
    readonly TextBox output = new() { Name = "ScriptOutput", IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0), Padding = new Thickness(12, 8, 12, 8), VerticalContentAlignment = VerticalAlignment.Top };
    readonly TextBlock outputTitle = Ui.Text("Вывод", 14, true), state = Ui.Text("", 12);
    internal DataGrid Table { get; } = new() { Name = "ScriptsTable", IsReadOnly = true, AutoGenerateColumns = false, HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Single };
    internal Func<SavedScript, SavedScript?>? EditOverride { get; set; }
    internal Func<string, bool>? ConfirmOverride { get; set; }
    /// <summary>UI fixtures have no live session.</summary>
    internal Func<bool>? ConnectedOverride { get; set; }
    bool Connected => ConnectedOverride?.Invoke() ?? session.State == SessionState.Connected;
    ScriptRow? Selected => Table.SelectedItem as ScriptRow;

    public ScriptsView(ServerSession session, ScriptRunner runner)
    {
        this.session = session; this.runner = runner; Name = "ScriptsView";
        SetResourceReference(BackgroundProperty, "Paper");
        var title = Ui.Text("Скрипты", 18, true);
        var subtitle = Ui.Text(session.Profile.Name + " · " + session.Profile.Username + "@" + session.Profile.Endpoint, 12); subtitle.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); subtitle.TextTrimming = TextTrimming.CharacterEllipsis; subtitle.MaxWidth = 340;
        var heading = new StackPanel { Margin = new Thickness(4, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center }; heading.Children.Add(title); heading.Children.Add(subtitle);
        var bar = new ResponsiveCommandBar { Name = "ScriptsCommandBar", VerticalAlignment = VerticalAlignment.Center };
        void Command(string key, string glyph, string label, Func<Task> action, string tooltip, int priority)
        {
            var b = Ui.AsyncCommandButton(glyph, label, action, tooltip); b.Name = "Script" + key; commands[key] = b;
            bar.Add(key, b, () => Ui.AsyncMenu(label, action, glyph: glyph), priority);
        }
        Command("Run", "", "Запустить", RunSelectedAsync, "Выполнить выбранный скрипт сейчас", 100);
        Command("Add", Ui.AddGlyph, "Добавить…", AddAsync, "Новый скрипт", 90);
        Command("Edit", Ui.RenameGlyph, "Изменить…", EditAsync, "Команды, расписание и параметры", 80);
        Command("Duplicate", "", "Дублировать", DuplicateAsync, "Копия скрипта (без расписания)", 40);
        Command("Remove", Ui.DeleteGlyph, "Удалить…", RemoveAsync, "Удалить скрипт из списка", 30);
        var barRow = new DockPanel(); DockPanel.SetDock(heading, Dock.Left); barRow.Children.Add(heading); var divider = Ui.Line(); DockPanel.SetDock(divider, Dock.Left); barRow.Children.Add(divider); barRow.Children.Add(bar);
        var barHost = new Border { Child = barRow, Padding = new Thickness(10, 4, 10, 4) }; barHost.SetResourceReference(StyleProperty, "CommandBarStyle");

        Table.SetResourceReference(StyleProperty, "ProcessTableStyle"); Table.Margin = new Thickness(16, 10, 16, 0);
        MainWindow.AddColumn(Table, "Название", nameof(ScriptRow.Name), 240); MainWindow.AddColumn(Table, "Запуск", nameof(ScriptRow.Schedule), 200);
        MainWindow.AddColumn(Table, "Последний запуск", nameof(ScriptRow.LastRun), 150); MainWindow.AddColumn(Table, "Результат", nameof(ScriptRow.Result), 160);
        var last = Table.Columns[^1]; last.MinWidth = 160; last.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
        Table.ItemsSource = rows;
        TableSorting.Attach(Table, "scripts", [new(nameof(ScriptRow.Name), r => ((ScriptRow)r).Name, true), new(nameof(ScriptRow.Schedule), r => ((ScriptRow)r).Script.Trigger.ToString()),
            new(nameof(ScriptRow.LastRun), r => ((ScriptRow)r).Script.LastRunUtc), new(nameof(ScriptRow.Result), r => ((ScriptRow)r).ResultRank)], nameof(ScriptRow.Name));
        Table.SelectionChanged += (_, _) => { UpdateCommands(); ShowOutput(); };
        Table.MouseDoubleClick += async (_, _) => { if (Selected is not null) await Ui.Guard(EditAsync); };

        var note = Ui.Text("Скрипт выполняется на сервере по SSH от вашей учётной записи. Автоматический запуск (при подключении, по интервалу, ежедневно) работает, пока приложение открыто и этот сервер подключён.", 12);
        note.TextWrapping = TextWrapping.Wrap; note.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); note.Margin = new Thickness(16, 8, 16, 0);
        var outputBar = new DockPanel { Margin = new Thickness(16, 10, 16, 4) }; outputBar.Children.Add(outputTitle);
        var outputBorder = new Border { Child = output, BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(16, 0, 16, 0) }; outputBorder.SetResourceReference(Border.BorderBrushProperty, "Edge"); output.SetResourceReference(BackgroundProperty, "Surface");
        var grid = new Grid();
        foreach (var h in new[] { new GridLength(3, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(2, GridUnitType.Star) }) grid.RowDefinitions.Add(new RowDefinition { Height = h, MinHeight = h.IsStar ? 100 : 0 });
        var splitter = new GridSplitter { Height = 6, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent };
        Grid.SetRow(Table, 0); Grid.SetRow(note, 1); Grid.SetRow(splitter, 2); Grid.SetRow(outputBar, 3); Grid.SetRow(outputBorder, 4);
        grid.Children.Add(Table); grid.Children.Add(note); grid.Children.Add(splitter); grid.Children.Add(outputBar); grid.Children.Add(outputBorder);
        state.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); state.Margin = new Thickness(16, 6, 16, 6); state.Name = "ScriptsState";
        var footer = new Border { Child = state }; footer.SetResourceReference(StyleProperty, "StatusBarStyle");
        var root = new DockPanel(); DockPanel.SetDock(barHost, Dock.Top); root.Children.Add(barHost); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); root.Children.Add(grid);
        Content = root;
        runner.Changed += OnRunnerChanged;
        Unloaded += (_, _) => runner.Changed -= OnRunnerChanged;
        Loaded += (_, _) => { runner.Changed -= OnRunnerChanged; runner.Changed += OnRunnerChanged; Reload(); };
        Reload();
    }

    void OnRunnerChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => { foreach (var r in rows) r.Refresh(); UpdateCommands(); ShowOutput(); UpdateState(); });

    ServerProfile? Profile => App.Store.Profiles.FirstOrDefault(p => p.Id == session.Profile.Id);
    List<SavedScript> Scripts => Profile?.Scripts ?? session.Profile.Scripts;

    void Reload()
    {
        var selected = Selected?.Script.Id;
        rows.Clear(); foreach (var s in Scripts) rows.Add(new ScriptRow(s, runner.IsRunning));
        Table.SelectedItem = rows.FirstOrDefault(r => r.Script.Id == selected) ?? rows.FirstOrDefault();
        UpdateCommands(); UpdateState(); ShowOutput();
    }

    void UpdateState()
    {
        int automatic = Scripts.Count(s => s.Trigger != ScriptTrigger.Manual && s.Enabled);
        state.Text = Scripts.Count == 0 ? "Скриптов пока нет — «Добавить…» создаёт первый." : $"Скриптов: {Scripts.Count}, с автоматическим запуском: {automatic}" + (Connected ? "" : " · сервер не подключён: запуск недоступен");
    }

    void UpdateCommands()
    {
        var s = Selected?.Script;
        commands["Run"].IsEnabled = s is not null && !runner.IsRunning(s.Id) && Connected;
        commands["Edit"].IsEnabled = commands["Duplicate"].IsEnabled = commands["Remove"].IsEnabled = s is not null;
    }

    void ShowOutput()
    {
        if (Selected?.Script is not { } s) { outputTitle.Text = "Вывод"; output.Text = ""; return; }
        var run = runner.History.FirstOrDefault(h => h.ScriptId == s.Id);
        if (runner.IsRunning(s.Id)) { outputTitle.Text = $"Вывод: {s.Name} · выполняется…"; return; }
        if (run is null) { outputTitle.Text = $"Вывод: {s.Name}"; output.Text = s.LastRunUtc is null ? "Скрипт ещё не запускался." : "Вывод хранится только для запусков в этом сеансе приложения."; return; }
        outputTitle.Text = $"Вывод: {s.Name} · {run.StartedUtc.ToLocalTime():HH:mm:ss} · {run.Reason} · {(run.TimedOut ? "таймаут" : "код " + run.ExitCode)} · {run.Duration.TotalSeconds:0.0} с";
        output.Text = run.Output.Length == 0 ? "(нет вывода)" : run.Output; output.ScrollToEnd();
    }

    async Task SaveAsync() { if (Profile is not null) await App.Store.SaveAsync(); }

    async Task RunSelectedAsync()
    {
        if (Selected?.Script is not { } s) return;
        var task = runner.RunAsync(s); OnRunnerChanged(this, EventArgs.Empty);
        await task; await SaveAsync();
    }

    SavedScript? Edit(SavedScript script)
    {
        if (EditOverride is not null) return EditOverride(script);
        var window = new ScriptEditorWindow(script) { Owner = Window.GetWindow(this) };
        return window.ShowDialog() == true ? window.Accepted : null;
    }

    async Task AddAsync()
    {
        if (Edit(new SavedScript { Name = "Новый скрипт" }) is not { } created) return;
        Scripts.Add(created); if (!ReferenceEquals(Scripts, session.Profile.Scripts)) session.Profile.Scripts.Add(created.Clone());
        await SaveAsync(); Reload(); Table.SelectedItem = rows.FirstOrDefault(r => r.Script.Id == created.Id);
    }

    async Task EditAsync()
    {
        if (Selected?.Script is not { } s) return;
        if (Edit(s.Clone()) is not { } changed) return;
        Replace(Scripts, changed); if (!ReferenceEquals(Scripts, session.Profile.Scripts)) Replace(session.Profile.Scripts, changed);
        await SaveAsync(); Reload();
    }

    static void Replace(List<SavedScript> list, SavedScript changed) { int i = list.FindIndex(x => x.Id == changed.Id); if (i >= 0) list[i] = changed; else list.Add(changed); }

    async Task DuplicateAsync()
    {
        if (Selected?.Script is not { } s) return;
        var copy = s.Clone(); copy.Id = Guid.NewGuid(); copy.Name = s.Name + " (копия)"; copy.Trigger = ScriptTrigger.Manual; copy.LastRunUtc = null; copy.LastExitCode = null;
        Scripts.Add(copy); if (!ReferenceEquals(Scripts, session.Profile.Scripts)) session.Profile.Scripts.Add(copy.Clone());
        await SaveAsync(); Reload(); Table.SelectedItem = rows.FirstOrDefault(r => r.Script.Id == copy.Id);
    }

    async Task RemoveAsync()
    {
        if (Selected?.Script is not { } s) return;
        if (!(ConfirmOverride?.Invoke(s.Name) ?? Ui.Confirm($"Удалить скрипт «{s.Name}» из списка сервера {session.Profile.Name}?\n\nНа сервере ничего не удаляется."))) return;
        Scripts.RemoveAll(x => x.Id == s.Id); session.Profile.Scripts.RemoveAll(x => x.Id == s.Id);
        await SaveAsync(); Reload();
    }
}
