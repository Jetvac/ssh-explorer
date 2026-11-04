using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SSHExplorer.Core;

namespace SSHExplorer.App;

/// <summary>
/// «Занятые порты»: listening TCP/UDP sockets with process, PID and owner; search across every column.
/// Loaded when first shown and on «Обновить» — never on the monitor's poll interval.
/// </summary>
public sealed class PortsPanel : StackPanel
{
    readonly ObservableCollection<ListeningPort> rows = [];
    readonly TextBox search = new() { Name = "PortsSearch", MinHeight = 32, MinWidth = 220 };
    readonly TextBlock state = Ui.Text("", 12);
    readonly Button refresh;
    readonly ListCollectionView view;
    CancellationTokenSource? loading;
    bool loaded;
    internal DataGrid Table { get; } = new() { Name = "PortsTable", IsReadOnly = true, AutoGenerateColumns = false, HeadersVisibility = DataGridHeadersVisibility.Column, MaxHeight = 320, MinHeight = 76 };
    /// <summary>Runs the read-only listing on the server; null when the session has no shell.</summary>
    internal Func<string, CancellationToken, Task<CommandResult>>? Execute { get; set; }
    internal PortsSnapshot? Snapshot { get; private set; }

    public PortsPanel(Func<string, CancellationToken, Task<CommandResult>>? execute)
    {
        Execute = execute; Name = "PortsPanel";
        var title = Ui.Text("Занятые порты", 18, true); title.Margin = new Thickness(0, 12, 0, 6);
        refresh = Ui.AsyncCommandButton(Ui.RefreshGlyph, "Обновить", () => LoadAsync(), "Заново прочитать слушающие порты");
        var searchHost = new Grid(); searchHost.Children.Add(search);
        var hint = Ui.Text("Порт, процесс, PID, пользователь или адрес", 13); hint.IsHitTestVisible = false; hint.Margin = new Thickness(10, 0, 6, 0); hint.VerticalAlignment = VerticalAlignment.Center; hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); searchHost.Children.Add(hint);
        search.TextChanged += (_, _) => { hint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; view!.Refresh(); UpdateState(); };
        System.Windows.Automation.AutomationProperties.SetName(search, "Поиск по занятым портам");
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 6) }; DockPanel.SetDock(refresh, Dock.Right); bar.Children.Add(refresh); bar.Children.Add(searchHost);
        Table.SetResourceReference(StyleProperty, "ProcessTableStyle"); Table.ColumnHeaderHeight = 36;
        MainWindow.AddColumn(Table, "Протокол", nameof(ListeningPort.Protocol), 90); MainWindow.AddColumn(Table, "Адрес", nameof(ListeningPort.Address), 170);
        MainWindow.AddColumn(Table, "Порт", nameof(ListeningPort.Port), 80); MainWindow.AddColumn(Table, "Состояние", nameof(ListeningPort.State), 100);
        MainWindow.AddColumn(Table, "Процесс", nameof(ListeningPort.Process), 170); MainWindow.AddColumn(Table, "PID", nameof(ListeningPort.Pid), 80);
        MainWindow.AddColumn(Table, "Пользователь", nameof(ListeningPort.User), 130);
        var last = Table.Columns[^1]; last.MinWidth = 130; last.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
        view = (ListCollectionView)CollectionViewSource.GetDefaultView(rows);
        view.Filter = item => search.Text.Trim() is not { Length: > 0 } query || ((ListeningPort)item).SearchText.Contains(query, StringComparison.CurrentCultureIgnoreCase);
        Table.ItemsSource = view;
        TableSorting.Attach(Table, "monitor.ports", [new(nameof(ListeningPort.Port), r => ((ListeningPort)r).Port), new(nameof(ListeningPort.Protocol), r => ((ListeningPort)r).Protocol), new(nameof(ListeningPort.Address), r => ((ListeningPort)r).Address, true),
            new(nameof(ListeningPort.State), r => ((ListeningPort)r).State), new(nameof(ListeningPort.Process), r => ((ListeningPort)r).Process, true), new(nameof(ListeningPort.Pid), r => ((ListeningPort)r).Pid), new(nameof(ListeningPort.User), r => ((ListeningPort)r).User, true)], nameof(ListeningPort.Port));
        state.TextWrapping = TextWrapping.Wrap; state.Margin = new Thickness(0, 6, 0, 0); state.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); state.Name = "PortsState";
        Children.Add(title); Children.Add(bar); Children.Add(Table); Children.Add(state);
        IsVisibleChanged += async (_, e) => { if (e.NewValue is true && !loaded) await Ui.Guard(() => LoadAsync()); };
        Unloaded += (_, _) => loading?.Cancel();
        UpdateState();
    }

    public async Task LoadAsync()
    {
        if (Execute is null) { state.Text = "Сервер не разрешает выполнять команды (SSH exec): список портов недоступен."; return; }
        loading?.Cancel(); var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)); loading = cts; loaded = true;
        refresh.IsEnabled = false; state.Text = "Чтение слушающих портов…";
        try
        {
            var result = await Execute(ListeningPorts.Command, cts.Token);
            Apply(ListeningPorts.Parse(result.Output));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ReferenceEquals(loading, cts)) { }
        catch (Exception ex) { state.Text = "Не удалось прочитать порты: " + ErrorMessages.Describe(ex); }
        finally { if (ReferenceEquals(loading, cts)) { refresh.IsEnabled = true; loading = null; } cts.Dispose(); }
    }

    internal void Apply(PortsSnapshot snapshot)
    {
        Snapshot = snapshot; rows.Clear(); foreach (var port in snapshot.Ports) rows.Add(port); UpdateState();
    }

    void UpdateState()
    {
        if (Snapshot is null) { if (state.Text.Length == 0) state.Text = "Список читается при открытии раздела."; return; }
        if (Snapshot.Error is { } error) { state.Text = error; return; }
        var shown = view.Count; var total = Snapshot.Ports.Count;
        var text = (search.Text.Trim().Length > 0 ? $"Найдено {shown} из {total}" : $"Слушающих сокетов: {total}") + $" · источник: {Snapshot.Tool}";
        if (Snapshot.ProcessesHidden) text += ". Процессы других пользователей скрыты: для полного списка нужны права root на сервере.";
        state.Text = text;
    }
}
