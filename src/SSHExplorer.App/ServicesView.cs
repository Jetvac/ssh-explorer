using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using SSHExplorer.Core;

namespace SSHExplorer.App;

/// <summary>
/// «Службы» page of the task manager: systemd services with state and autostart, start/stop/restart/reload and autostart
/// changes after a confirmation, and the selected service's journal (journalctl -u, followed).
/// </summary>
public sealed class ServicesView : UserControl, IDisposable
{
    readonly SystemdService systemd;
    readonly ObservableCollection<ServiceUnit> rows = [];
    readonly ListCollectionView view;
    readonly TextBox search = new() { Name = "ServicesSearch", MinHeight = 32, MinWidth = 220 };
    readonly ComboBox show = new() { ItemsSource = new[] { "Все", "Работают", "Со сбоем", "Остановлены", "С автозапуском" }, SelectedIndex = 0, MinWidth = 150, Name = "ServicesShow", Margin = new Thickness(8, 0, 0, 0) };
    readonly TextBlock subtitle = Ui.Text("", 12), state = Ui.Text("", 12);
    readonly Dictionary<string, Button> commands = [];
    readonly Border placeholder = new() { Padding = new Thickness(24), Visibility = Visibility.Collapsed };
    readonly Grid content = new();
    readonly DispatcherTimer journalDelay = new() { Interval = TimeSpan.FromMilliseconds(350) };
    readonly CancellationTokenSource lifetime = new();
    bool loaded, busy, available;
    internal LogView Journal { get; } = new();
    internal DataGrid Table { get; } = new() { Name = "ServicesTable", IsReadOnly = true, AutoGenerateColumns = false, HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Single };
    internal Func<string, bool>? ConfirmOverride { get; set; }
    ServiceUnit? Selected => Table.SelectedItem as ServiceUnit;

    public ServicesView(SystemdService service)
    {
        systemd = service; Name = "ServicesView";
        SetResourceReference(BackgroundProperty, "Paper");
        var title = Ui.Text("Службы", 18, true);
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); subtitle.TextTrimming = TextTrimming.CharacterEllipsis; subtitle.MaxWidth = 340; subtitle.Text = (systemd.Windows ? "Службы Windows · " : "systemd · ") + systemd.Session.Profile.Name;
        var heading = new StackPanel { Margin = new Thickness(4, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center }; heading.Children.Add(title); heading.Children.Add(subtitle);
        var bar = new ResponsiveCommandBar { Name = "ServicesCommandBar", VerticalAlignment = VerticalAlignment.Center };
        void Command(string key, string glyph, string label, Func<Task> action, string tooltip, int priority)
        {
            var b = Ui.AsyncCommandButton(glyph, label, () => Run(action), tooltip); b.Name = "Services" + key; commands[key] = b;
            bar.Add(key, b, () => Ui.AsyncMenu(label, () => Run(action), glyph: glyph), priority);
        }
        Command("Refresh", Ui.RefreshGlyph, "Обновить", () => RefreshAsync(), "Заново прочитать список служб", 100);
        Command("Start", "", "Запустить", () => Act(ServiceAction.Start), "systemctl start", 90);
        Command("Stop", "", "Остановить", () => Act(ServiceAction.Stop), "systemctl stop", 90);
        Command("Restart", IconCatalog.Restart, "Перезапустить", () => Act(ServiceAction.Restart), "systemctl restart", 80);
        Command("Reload", Ui.RefreshGlyph, "Перечитать настройки", () => Act(ServiceAction.Reload), "systemctl reload — без остановки, если служба это умеет", 40);
        Command("Enable", "", "Включить автозапуск", () => Act(ServiceAction.Enable), "systemctl enable — запускать при загрузке", 30);
        Command("Disable", "", "Отключить автозапуск", () => Act(ServiceAction.Disable), "systemctl disable — не запускать при загрузке", 20);
        var barRow = new DockPanel(); DockPanel.SetDock(heading, Dock.Left); barRow.Children.Add(heading); var divider = Ui.Line(); DockPanel.SetDock(divider, Dock.Left); barRow.Children.Add(divider); barRow.Children.Add(bar);
        var barHost = new Border { Child = barRow, Padding = new Thickness(10, 4, 10, 4) }; barHost.SetResourceReference(StyleProperty, "CommandBarStyle");

        var searchGrid = new Grid(); searchGrid.Children.Add(search);
        var hint = Ui.Text("Имя, описание или состояние", 13); hint.IsHitTestVisible = false; hint.Margin = new Thickness(10, 0, 6, 0); hint.VerticalAlignment = VerticalAlignment.Center; hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); searchGrid.Children.Add(hint);
        var searchHost = new DockPanel { Margin = new Thickness(16, 10, 16, 6) }; DockPanel.SetDock(show, Dock.Right); searchHost.Children.Add(show); searchHost.Children.Add(searchGrid);
        search.TextChanged += (_, _) => { hint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; view!.Refresh(); UpdateState(); };
        show.SelectionChanged += (_, _) => { view!.Refresh(); UpdateState(); };
        System.Windows.Automation.AutomationProperties.SetName(search, "Поиск служб");

        Table.SetResourceReference(StyleProperty, "ProcessTableStyle"); Table.Margin = new Thickness(16, 0, 16, 0);
        MainWindow.AddColumn(Table, "Служба", nameof(ServiceUnit.Name), 240); MainWindow.AddColumn(Table, "Состояние", nameof(ServiceUnit.StateText), 150);
        MainWindow.AddColumn(Table, "Запуск", nameof(ServiceUnit.StartupText), 130); MainWindow.AddColumn(Table, "Описание", nameof(ServiceUnit.Description), 260);
        var last = Table.Columns[^1]; last.MinWidth = 160; last.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
        view = (ListCollectionView)CollectionViewSource.GetDefaultView(rows);
        view.Filter = item =>
        {
            var u = (ServiceUnit)item;
            bool kind = show.SelectedIndex switch { 1 => u.IsRunning, 2 => u.IsFailed, 3 => !u.IsRunning && !u.IsFailed, 4 => u.UnitFileState is "enabled" or "enabled-runtime", _ => true };
            return kind && (search.Text.Trim() is not { Length: > 0 } q || u.SearchText.Contains(q, StringComparison.CurrentCultureIgnoreCase));
        };
        Table.ItemsSource = view;
        TableSorting.Attach(Table, "systemd.services", [new(nameof(ServiceUnit.Name), r => ((ServiceUnit)r).Name, true), new(nameof(ServiceUnit.StateText), r => ((ServiceUnit)r).IsFailed ? 0 : ((ServiceUnit)r).IsRunning ? 1 : 2),
            new(nameof(ServiceUnit.StartupText), r => ((ServiceUnit)r).StartupText, true), new(nameof(ServiceUnit.Description), r => ((ServiceUnit)r).Description, true)], nameof(ServiceUnit.StateText));
        Table.SelectionChanged += (_, _) => { UpdateCommands(); journalDelay.Stop(); journalDelay.Start(); };
        journalDelay.Tick += (_, _) => { journalDelay.Stop(); OpenJournal(); };

        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star), MinHeight = 120 });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star), MinHeight = 140 });
        var splitter = new GridSplitter { Height = 6, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent };
        Grid.SetRow(searchHost, 0); Grid.SetRow(Table, 1); Grid.SetRow(splitter, 2); Grid.SetRow(Journal, 3);
        content.Children.Add(searchHost); content.Children.Add(Table); content.Children.Add(splitter); content.Children.Add(Journal);

        state.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); state.Margin = new Thickness(16, 6, 16, 6); state.TextTrimming = TextTrimming.CharacterEllipsis; state.Name = "ServicesState";
        var footer = new Border { Child = state }; footer.SetResourceReference(StyleProperty, "StatusBarStyle");
        var root = new DockPanel(); DockPanel.SetDock(barHost, Dock.Top); root.Children.Add(barHost); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var body = new Grid(); body.Children.Add(content); body.Children.Add(placeholder); root.Children.Add(body);
        Content = root;
        UpdateCommands();
        IsVisibleChanged += async (_, e) =>
        {
            if (e.NewValue is true && !loaded) await Ui.Guard(() => RefreshAsync());
            // A followed journal holds a connection: it stops when the page is hidden and resumes when it is shown again.
            if (e.NewValue is false) Journal.Close(); else if (loaded && Selected is not null) OpenJournal();
        };
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
        ServicesSnapshot snapshot;
        try { snapshot = await systemd.ListAsync(lifetime.Token); }
        catch (Exception ex) when (ex is not OperationCanceledException) { ShowProblem("Не удалось прочитать список служб: " + ErrorMessages.Describe(ex)); return; }
        if (!snapshot.Available) { ShowProblem(snapshot.Error ?? "systemd недоступен."); return; }
        available = true; placeholder.Visibility = Visibility.Collapsed; content.Visibility = Visibility.Visible;
        var selected = Selected?.Name;
        rows.Clear(); foreach (var u in snapshot.Units) rows.Add(u);
        Table.SelectedItem = rows.FirstOrDefault(u => u.Name == selected);
        UpdateState(); UpdateCommands();
    }

    void ShowProblem(string text)
    {
        available = false;
        var message = Ui.Text(text, 14); message.TextWrapping = TextWrapping.Wrap; message.MaxWidth = 640; message.HorizontalAlignment = HorizontalAlignment.Left; message.Name = "ServicesProblem";
        placeholder.Child = message; placeholder.Visibility = Visibility.Visible; content.Visibility = Visibility.Collapsed;
        state.Text = "Службы недоступны"; UpdateCommands();
    }

    string Rights => systemd.Windows ? (systemd.IsRoot ? "действия от администратора Windows" : "без прав администратора: изменять службы Windows нельзя") : systemd.IsRoot ? "действия от root" : systemd.Sudo ? "действия через sudo без пароля" : "без повышения прав: изменять службы сервер может не разрешить";

    void UpdateState()
    {
        if (!available) return;
        int running = rows.Count(u => u.IsRunning), failed = rows.Count(u => u.IsFailed);
        state.Text = (search.Text.Trim().Length > 0 || show.SelectedIndex > 0 ? $"Показано {view.Count} из {rows.Count}" : $"Служб: {rows.Count}, работают: {running}")
            + (failed > 0 ? $", со сбоем: {failed}" : "") + " · " + Rights;
    }

    void UpdateCommands()
    {
        var u = Selected; bool ok = available && !busy && u is not null;
        commands["Refresh"].IsEnabled = !busy;
        commands["Start"].IsEnabled = ok && !u!.IsRunning && u.Load != "masked";
        commands["Stop"].IsEnabled = ok && u!.IsRunning;
        commands["Restart"].IsEnabled = ok && u!.Load != "masked";
        commands["Reload"].IsEnabled = ok && u!.IsRunning && !systemd.Windows;
        commands["Enable"].IsEnabled = ok && u!.UnitFileState is "disabled";
        commands["Disable"].IsEnabled = ok && u!.UnitFileState is "enabled";
    }

    bool Confirm(string text) => ConfirmOverride?.Invoke(text) ?? Ui.Confirm(text);

    async Task Act(ServiceAction action)
    {
        if (Selected is not { } u) return;
        var warning = Systemd.MayDropConnection(u.Name, action) ? "\n\nЭто служба SSH: текущие подключения, включая это, могут прерваться, а новые — стать невозможными до её запуска." : "";
        var effect = action switch
        {
            ServiceAction.Stop => "Служба будет остановлена; зависящие от неё службы тоже могут остановиться.",
            ServiceAction.Restart => "Служба будет остановлена и запущена заново; её клиенты на время потеряют соединение.",
            ServiceAction.Reload => "Служба перечитает настройки без остановки (если она это поддерживает).",
            ServiceAction.Enable => "Служба будет запускаться при загрузке сервера. Сейчас она не запускается.",
            ServiceAction.Disable => "Служба перестанет запускаться при загрузке. Сейчас она не останавливается.",
            _ => "Служба будет запущена.",
        };
        if (!Confirm($"{Systemd.Label(action)} «{u.Name}» на {systemd.Session.Profile.Name}?\n\n{effect}{warning}\n\nВыполняется {(systemd.IsRoot ? "от root" : systemd.Sudo ? "через sudo без пароля" : "от вашей учётной записи")}.")) return;
        state.Text = Systemd.Label(action) + " «" + u.Name + "»…";
        await systemd.ActAsync(u.Name, action, lifetime.Token);
        MetricsHub.Note(systemd.Session.Profile.Id, "change", $"{Systemd.Label(action)} «{u.Name}»");
        await RefreshAsync();
        state.Text = $"{Systemd.Label(action)} «{u.Name}» — выполнено · " + Rights;
    }

    void OpenJournal()
    {
        if (Selected is not { } u || !IsVisible) { if (Selected is null) Journal.Close(); return; }
        Journal.Open("Журнал · " + u.Name, (lines, follow, onLine, ct) => systemd.JournalAsync(u.Name, lines, follow, onLine, ct));
    }

    public void Dispose() { lifetime.Cancel(); journalDelay.Stop(); Journal.Dispose(); }
}
