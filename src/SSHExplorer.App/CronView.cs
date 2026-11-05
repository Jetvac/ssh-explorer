using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SSHExplorer.Core;

namespace SSHExplorer.App;

/// <summary>A cron job as a table row.</summary>
public sealed record CronRow(CronLine Line, DateTime ServerNow)
{
    public string Enabled => Line.Enabled ? "Да" : "Выкл.";
    public string When => Cron.Describe(Line.Schedule);
    public string Next => !Line.Enabled ? "—" : Line.Schedule == "@reboot" ? "при загрузке" : Cron.Next(Line.Schedule, ServerNow, 1) is [var n] ? n.ToString("dd.MM HH:mm", CultureInfo.CurrentCulture) : "—";
    public string Command => Line.Command;
    public string Raw => Line.Schedule;
}

/// <summary>
/// «Задания cron»: the crontab of this user (or root's through sudo) in words, with the next run in server time.
/// Every change re-reads the crontab first and is refused if it changed meanwhile; the previous one is kept as a backup.
/// </summary>
public sealed class CronView : UserControl, IDisposable
{
    readonly CronService cron;
    readonly ObservableCollection<CronRow> rows = [];
    readonly TextBlock info = Ui.Text("", 12), state = Ui.Text("", 12);
    readonly ComboBox whose = new() { Name = "CronWhose", MinHeight = 30, ItemsSource = new[] { "Мои задания", "Задания root (sudo)" }, SelectedIndex = 0, Width = 200 };
    readonly Dictionary<string, Button> commands = [];
    readonly CancellationTokenSource lifetime = new();
    CronTab? tab; bool loaded, busy;
    internal DataGrid Table { get; } = new() { Name = "CronTable", IsReadOnly = true, AutoGenerateColumns = false, HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Single };
    internal Func<CronTab, CronLine?, (string Line, string? Comment)?>? EditOverride { get; set; }
    internal Func<string, bool>? ConfirmOverride { get; set; }
    internal string StateText => state.Text;
    internal string InfoText => info.Text;
    internal ObservableCollection<CronRow> Rows => rows;
    CronRow? Selected => Table.SelectedItem as CronRow;

    public CronView(CronService service)
    {
        cron = service; Name = "CronView"; SetResourceReference(BackgroundProperty, "Paper");
        var title = Ui.Text("Задания cron", 18, true);
        var subtitle = Ui.Text("расписание команд · " + cron.Session.Profile.Name, 12); subtitle.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); subtitle.TextTrimming = TextTrimming.CharacterEllipsis; subtitle.MaxWidth = 340;
        var heading = new StackPanel { Margin = new Thickness(4, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center }; heading.Children.Add(title); heading.Children.Add(subtitle);
        var bar = new ResponsiveCommandBar { Name = "CronCommandBar", VerticalAlignment = VerticalAlignment.Center };
        void Command(string key, string glyph, string label, Func<Task> action, string tooltip, int priority)
        {
            var b = Ui.AsyncCommandButton(glyph, label, () => Run(action), tooltip); b.Name = "Cron" + key; commands[key] = b;
            bar.Add(key, b, () => Ui.AsyncMenu(label, () => Run(action), glyph: glyph), priority);
        }
        Command("Refresh", Ui.RefreshGlyph, "Обновить", () => RefreshAsync(), "Заново прочитать crontab", 100);
        Command("Add", Ui.AddGlyph, "Добавить…", AddAsync, "Новое задание", 95);
        Command("Edit", Ui.RenameGlyph, "Изменить…", EditAsync, "Изменить расписание или команду", 80);
        Command("Toggle", IconCatalog.Power, "Выключить", ToggleAsync, "Временно выключить или снова включить задание", 70);
        Command("Remove", Ui.DeleteGlyph, "Удалить…", RemoveAsync, "Удалить задание", 60);
        AutomationProperties.SetName(whose, "Чьё расписание"); whose.Margin = new Thickness(0, 0, 12, 0); whose.VerticalAlignment = VerticalAlignment.Center;
        whose.SelectionChanged += async (_, _) => { cron.AsRoot = whose.SelectedIndex == 1; if (loaded) await Ui.Guard(() => Run(() => RefreshAsync())); };
        var barRow = new DockPanel(); DockPanel.SetDock(heading, Dock.Left); barRow.Children.Add(heading); var divider = Ui.Line(); DockPanel.SetDock(divider, Dock.Left); barRow.Children.Add(divider); DockPanel.SetDock(whose, Dock.Right); barRow.Children.Add(whose); barRow.Children.Add(bar);
        var barHost = new Border { Child = barRow, Padding = new Thickness(10, 4, 10, 4) }; barHost.SetResourceReference(StyleProperty, "CommandBarStyle");
        info.Name = "CronInfo"; info.TextWrapping = TextWrapping.Wrap; info.Margin = new Thickness(16, 10, 16, 8); info.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        Table.SetResourceReference(StyleProperty, "ProcessTableStyle"); Table.Margin = new Thickness(16, 0, 16, 8);
        MainWindow.AddColumn(Table, "Вкл.", nameof(CronRow.Enabled), 60); MainWindow.AddColumn(Table, "Когда", nameof(CronRow.When), 230); MainWindow.AddColumn(Table, "Ближайший", nameof(CronRow.Next), 110);
        MainWindow.AddColumn(Table, "Команда", nameof(CronRow.Command), 360); MainWindow.AddColumn(Table, "Расписание", nameof(CronRow.Raw), 140);
        Table.Columns[3].Width = new DataGridLength(1, DataGridLengthUnitType.Star);
        Table.ItemsSource = rows; Table.SelectionChanged += (_, _) => UpdateCommands();
        Table.MouseDoubleClick += async (_, _) => { if (Selected is not null) await Ui.Guard(() => Run(EditAsync)); };
        state.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); state.Margin = new Thickness(16, 6, 16, 6); state.TextTrimming = TextTrimming.CharacterEllipsis; state.Name = "CronState";
        var footer = new Border { Child = state }; footer.SetResourceReference(StyleProperty, "StatusBarStyle");
        var root = new DockPanel(); DockPanel.SetDock(barHost, Dock.Top); root.Children.Add(barHost); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); DockPanel.SetDock(info, Dock.Top); root.Children.Add(info); root.Children.Add(Table);
        Content = root;
        UpdateCommands();
        IsVisibleChanged += async (_, e) => { if (e.NewValue is true && !loaded) await Ui.Guard(() => Run(() => RefreshAsync())); };
    }

    async Task Run(Func<Task> action)
    {
        if (busy) return; busy = true; UpdateCommands();
        try { await action(); }
        catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException or ArgumentException or Renci.SshNet.Common.SshException) { state.Text = ErrorMessages.Describe(ex); Ui.Error(ErrorMessages.Describe(ex)); }
        finally { busy = false; UpdateCommands(); }
    }

    public async Task RefreshAsync()
    {
        loaded = true;
        if (cron.Session.State != SessionState.Connected && cron.ExecuteOverride is null) { info.Text = "Сервер не подключён."; tab = null; rows.Clear(); return; }
        tab = await cron.ReadAsync(lifetime.Token);
        var selected = Selected?.Line.Raw;
        rows.Clear(); foreach (var j in tab.Jobs) rows.Add(new CronRow(j, tab.ServerNow));
        Table.SelectedItem = rows.FirstOrDefault(r => r.Line.Raw == selected) ?? rows.FirstOrDefault();
        info.Text = tab.Problem ?? (cron.AsRoot ? "Задания root (через sudo без пароля). " : $"Задания пользователя {tab.User}. ")
            + $"Время сервера: {tab.ServerNow:dd.MM.yyyy HH:mm} — cron запускает задания по нему. Перед каждым изменением crontab перечитывается; прежний сохраняется в ~/.cache/sshexplorer." + (tab.Lines.Any(l => l.IsEnvironment) ? " Переменные окружения в начале crontab сохраняются." : "");
        state.Text = $"Заданий: {rows.Count}" + (rows.Count(r => !r.Line.Enabled) is > 0 and var off ? $", выключено: {off}" : "");
    }

    void UpdateCommands()
    {
        bool ok = !busy && tab is { Problem: null };
        commands["Refresh"].IsEnabled = !busy; commands["Add"].IsEnabled = ok;
        commands["Edit"].IsEnabled = commands["Toggle"].IsEnabled = commands["Remove"].IsEnabled = ok && Selected is not null;
        if (commands["Toggle"].Content is Panel p) foreach (var t in p.Children.OfType<TextBlock>()) if (t.Text is "Выключить" or "Включить") t.Text = Selected?.Line.Enabled == false ? "Включить" : "Выключить";
    }

    bool Confirm(string text) => ConfirmOverride?.Invoke(text) ?? Ui.Confirm(text, "Задания cron");

    (string Line, string? Comment)? Edit(CronLine? line)
    {
        if (EditOverride is not null) return EditOverride(tab!, line);
        var window = new CronJobWindow(line, tab!.ServerNow) { Owner = Window.GetWindow(this) };
        return window.ShowDialog() == true ? (window.ResultLine, window.ResultComment) : null;
    }

    /// <summary>Re-reads, applies the change to the fresh crontab and writes it; a crontab changed meanwhile is not overwritten.</summary>
    async Task ChangeAsync(Func<CronTab, string?> build, string done)
    {
        var before = tab!;
        var fresh = await cron.ReadAsync(lifetime.Token);
        if (fresh.Text != before.Text) { await RefreshAsync(); Ui.Error("Crontab изменился на сервере, пока он был открыт здесь. Список обновлён — повторите действие."); return; }
        var text = build(fresh); if (text is null) return;
        var outcome = await cron.SaveAsync(fresh, text, lifetime.Token);
        await RefreshAsync();
        if (outcome == CronSaveOutcome.Changed) { Ui.Error("Crontab изменился на сервере во время сохранения — изменение не записано. Список обновлён."); return; }
        state.Text = done;
    }

    int IndexOf(CronTab t, CronLine line) => t.Lines.FirstOrDefault(l => l.Raw == line.Raw)?.Index ?? -1;

    async Task AddAsync()
    {
        if (Edit(null) is not { } r) return;
        await ChangeAsync(t => Cron.Edit(t, -1, r.Line, r.Comment), "Задание добавлено.");
    }

    async Task EditAsync()
    {
        if (Selected is not { } row || Edit(row.Line) is not { } r) return;
        await ChangeAsync(t => IndexOf(t, row.Line) is var i and >= 0 ? Cron.Edit(t, i, r.Line) : null, "Задание изменено.");
    }

    async Task ToggleAsync()
    {
        if (Selected is not { } row) return;
        await ChangeAsync(t => IndexOf(t, row.Line) is var i and >= 0 ? Cron.Edit(t, i, Cron.Toggle(row.Line)) : null, row.Line.Enabled ? "Задание выключено." : "Задание включено.");
    }

    async Task RemoveAsync()
    {
        if (Selected is not { } row) return;
        if (!Confirm($"Удалить задание?\n\n{row.When}\n{row.Command}\n\nПрежний crontab сохранится в ~/.cache/sshexplorer.")) return;
        await ChangeAsync(t => IndexOf(t, row.Line) is var i and >= 0 ? Cron.Edit(t, i, null) : null, "Задание удалено.");
    }

    public void Dispose() => lifetime.Cancel();
}

/// <summary>A cron job: a ready schedule or five fields, the command, the schedule in words and the next runs.</summary>
public sealed class CronJobWindow : Window
{
    static readonly (string Label, string Schedule)[] Presets =
    [
        ("Своё расписание", ""), ("Каждую минуту", "* * * * *"), ("Каждые 5 минут", "*/5 * * * *"), ("Каждые 15 минут", "*/15 * * * *"), ("Каждый час", "0 * * * *"),
        ("Каждый день в 03:00", "0 3 * * *"), ("По будням в 09:00", "0 9 * * 1-5"), ("По воскресеньям в 04:00", "0 4 * * 0"), ("1-го числа в 05:00", "0 5 1 * *"), ("При загрузке сервера", "@reboot"),
    ];
    readonly ComboBox preset = new() { Name = "CronPreset", MinHeight = 32, ItemsSource = Presets.Select(p => p.Label).ToArray(), SelectedIndex = 0 };
    readonly TextBox schedule = new() { Name = "CronSchedule", MinHeight = 32, FontFamily = new FontFamily("Cascadia Mono, Consolas") }, command = new() { Name = "CronCommand", MinHeight = 32, FontFamily = new FontFamily("Cascadia Mono, Consolas") }, comment = new() { Name = "CronComment", MinHeight = 32 };
    readonly CheckBox enabled = new() { Name = "CronEnabled", IsChecked = true, Content = "Включено", Margin = new Thickness(0, 8, 0, 0) };
    readonly TextBlock words = Ui.Text("", 13), next = Ui.Text("", 12), problem = Ui.Text("", 12);
    readonly Button save;
    readonly DateTime now;
    bool presetting;
    public string ResultLine { get; private set; } = "";
    public string? ResultComment { get; private set; }
    internal string Words => words.Text;
    internal string NextText => next.Text;
    internal string? Problem => problem.Text.Length > 0 ? problem.Text : null;
    internal Button SaveButton => save;
    internal void Set(string when, string what) { schedule.Text = when; command.Text = what; }
    internal void Pick(int index) => preset.SelectedIndex = index;

    public CronJobWindow(CronLine? line, DateTime serverNow)
    {
        now = serverNow;
        Title = line is null ? "Новое задание cron" : "Задание cron"; Width = 620; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(StyleProperty, "DialogWindowStyle");
        var body = new StackPanel { Margin = new Thickness(24) };
        body.Children.Add(Ui.DialogHeading(line is null ? "Новое задание" : "Изменить задание"));
        StackPanel Field(string label, Control box) { var p = new StackPanel { Margin = new Thickness(0, 0, 0, 10) }; var l = Ui.Text(label, 12); l.Margin = new Thickness(0, 0, 0, 4); l.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); p.Children.Add(l); p.Children.Add(box); AutomationProperties.SetName(box, label); return p; }
        body.Children.Add(Field("Готовое расписание", preset));
        body.Children.Add(Field("Расписание: минута, час, день месяца, месяц, день недели (0 — воскресенье)", schedule));
        words.Name = "CronWords"; words.TextWrapping = TextWrapping.Wrap; body.Children.Add(words);
        next.Name = "CronNext"; next.TextWrapping = TextWrapping.Wrap; next.Margin = new Thickness(0, 2, 0, 10); next.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); body.Children.Add(next);
        body.Children.Add(Field("Команда (выполняется через /bin/sh; % в cron означает перевод строки — пишите \\%)", command));
        if (line is null) body.Children.Add(Field("Комментарий над заданием (необязательно)", comment));
        body.Children.Add(enabled);
        problem.Name = "CronProblem"; problem.TextWrapping = TextWrapping.Wrap; problem.Margin = new Thickness(0, 8, 0, 0); problem.SetResourceReference(TextBlock.ForegroundProperty, "DangerText"); body.Children.Add(problem);
        var cancel = Ui.Button("Отмена", () => DialogResult = false); cancel.IsCancel = true;
        save = Ui.Button("Сохранить", () => { if (save!.IsEnabled) { ResultLine = Cron.JobLine(schedule.Text, command.Text, enabled.IsChecked == true); ResultComment = comment.Text.Trim().Length > 0 ? comment.Text.Trim() : null; DialogResult = true; } });
        save.Name = "CronSave"; save.IsDefault = true; save.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        Content = Ui.DialogSurface(body, cancel, save);
        if (line is not null) { schedule.Text = line.Schedule; command.Text = line.Command; enabled.IsChecked = line.Enabled; }
        else { schedule.Text = "0 3 * * *"; }
        preset.SelectedIndex = Math.Max(0, Array.FindIndex(Presets, p => p.Schedule == schedule.Text.Trim()));
        preset.SelectionChanged += (_, _) => { if (preset.SelectedIndex > 0) { presetting = true; schedule.Text = Presets[preset.SelectedIndex].Schedule; presetting = false; } Update(); };
        schedule.TextChanged += (_, _) => { if (!presetting) { var i = Array.FindIndex(Presets, p => p.Schedule == schedule.Text.Trim()); preset.SelectedIndex = Math.Max(0, i); } Update(); };
        command.TextChanged += (_, _) => Update(); comment.TextChanged += (_, _) => Update();
        Loaded += (_, _) => command.Focus();
        Update();
    }

    void Update()
    {
        var error = Cron.ValidateSchedule(schedule.Text);
        if (error is null) { words.Text = Cron.Describe(schedule.Text); var runs = Cron.Next(schedule.Text, now); next.Text = schedule.Text.Trim() == "@reboot" ? "Запускается один раз после каждой загрузки сервера." : runs.Count == 0 ? "В ближайшие годы не сработает." : "Ближайшие запуски (время сервера): " + string.Join(", ", runs.Select(r => r.ToString("ddd dd.MM HH:mm", CultureInfo.CurrentCulture))); }
        else { words.Text = ""; next.Text = ""; }
        error ??= Cron.ValidateCommand(command.Text);
        if (error is null && comment.Text.Contains('\n')) error = "Комментарий — одна строка.";
        bool quiet = error is not null && command.Text.Length == 0 && Cron.ValidateSchedule(schedule.Text) is null;
        problem.Text = error is not null && !quiet ? error : ""; save.IsEnabled = error is null;
    }
}
