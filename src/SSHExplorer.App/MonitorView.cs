using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using SSHExplorer.Core;
using SSHExplorer.Monitoring;
using SSHExplorer.App.ViewModels;

namespace SSHExplorer.App;

public sealed class MonitorView : UserControl, IDisposable
{
    readonly ServerSession session; readonly LinuxMonitor monitor;
    readonly DataGrid processes = new(); readonly TextBox search = new() { Width = 300, ToolTip = "Поиск по имени процесса, PID или пользователю" };
    readonly TextBlock freshness = Ui.Text("Ещё нет замера"), summary = Ui.Text(""), limits = Ui.Text("");
    readonly TextBlock cpu = Ui.Text("Недоступно", 22, true), memory = Ui.Text("Недоступно", 22, true), systemInfo = Ui.Text("");
    readonly DataGrid networks = new(), disks = new(), storage = new();
    readonly MetricGraph cpuGraph = new() { Height = 130 }, memoryGraph = new() { Height = 130 };
    readonly CancellationTokenSource lifetime = new(); readonly System.Windows.Threading.DispatcherTimer timer = new();
    readonly MonitorViewModel viewModel = new();
    SortDescription[] processSorts = []; bool applyingProcesses, disposed;
    bool active { get => viewModel.IsActive; set => viewModel.IsActive = value; }
    bool busy { get => viewModel.IsBusy; set => viewModel.IsBusy = value; }
    public MonitorView(ServerSession session)
    {
        this.session = session; monitor = new LinuxMonitor(async (command, ct) => { var r = await session.ExecuteAsync(command, ct); return new ExecResult(r.Output, r.Error, r.ExitStatus); });
        DataContext = viewModel;
        freshness.SetBinding(TextBlock.TextProperty, new Binding(nameof(MonitorViewModel.Freshness)));
        summary.SetBinding(TextBlock.TextProperty, new Binding(nameof(MonitorViewModel.Summary)));
        cpu.SetBinding(TextBlock.TextProperty, new Binding(nameof(MonitorViewModel.CpuText)));
        memory.SetBinding(TextBlock.TextProperty, new Binding(nameof(MonitorViewModel.MemoryText)));
        systemInfo.SetBinding(TextBlock.TextProperty, new Binding(nameof(MonitorViewModel.SystemInfo)));
        limits.SetBinding(TextBlock.TextProperty, new Binding(nameof(MonitorViewModel.Limitations)));
        networks.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MonitorViewModel.Networks)));
        disks.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MonitorViewModel.Disks)));
        storage.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MonitorViewModel.Storage)));
        cpuGraph.SetBinding(MetricGraph.ValuesProperty, new Binding(nameof(MonitorViewModel.CpuHistory)));
        memoryGraph.SetBinding(MetricGraph.ValuesProperty, new Binding(nameof(MonitorViewModel.MemoryHistory)));
        processes.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MonitorViewModel.Processes)));
        search.SetBinding(TextBox.TextProperty, new Binding(nameof(MonitorViewModel.SearchQuery)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        viewModel.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(MonitorViewModel.Processes) or nameof(MonitorViewModel.SelectedIdentity)) ApplyProcesses(); };
        processes.SelectionChanged += (_, _) => { if (!applyingProcesses) viewModel.SelectedIdentity = (processes.SelectedItem as ProcessItemViewModel)?.Metric.Identity; };
        processes.Sorting += (_, _) => Dispatcher.BeginInvoke(() => processSorts = CollectionViewSource.GetDefaultView(processes.ItemsSource)?.SortDescriptions.ToArray() ?? []);
        var root = new DockPanel(); root.SetResourceReference(Panel.BackgroundProperty, "Paper");
        var header = new DockPanel { Margin = new Thickness(20, 10, 16, 10) }; var terminate = Ui.AsyncCommandButton(Ui.CloseGlyph, "Завершить задачу", () => Signal(15), "Завершить выбранный процесс (SIGTERM)"); terminate.SetBinding(IsEnabledProperty, new Binding(nameof(MonitorViewModel.CanSignalSelected)));
        var more = Ui.IconButton(Ui.MoreGlyph, () => { var menu = new ContextMenu { DataContext = viewModel }; var kill = Ui.AsyncMenu("Принудительно завершить (SIGKILL)…", () => Signal(9)); kill.SetBinding(IsEnabledProperty, new Binding(nameof(MonitorViewModel.CanSignalSelected))); menu.Items.Add(kill); menu.Items.Add(new Separator()); menu.Items.Add(Ui.AsyncMenu("Открыть htop / top в терминале", OpenTop)); menu.IsOpen = true; }, "Дополнительные действия"); var buttons = Ui.Row(terminate, more); DockPanel.SetDock(buttons, Dock.Right); header.Children.Add(buttons); header.Children.Add(Ui.Text("Диспетчер задач", 18, true)); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var footer = new StackPanel { Margin = new Thickness(18, 7, 18, 10) }; freshness.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); freshness.FontSize = 12; footer.Children.Add(freshness); var scale = Ui.Text(SystemMetric.CpuScale + ". Сеть измеряется по интерфейсам, не по процессам.", 12); scale.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); scale.TextWrapping = TextWrapping.Wrap; footer.Children.Add(scale); limits.TextWrapping = TextWrapping.Wrap; limits.FontSize = 12; footer.Children.Add(limits); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var tabs = new TabControl(); var processTab = new TabItem { Header = "Процессы" }; var performance = new TabItem { Header = "Производительность" }; tabs.Items.Add(processTab); tabs.Items.Add(performance); root.Children.Add(tabs);
        var processRoot = new DockPanel(); var searchBar = new DockPanel { Margin = new Thickness(16, 8, 16, 8) }; var searchHost = new Grid { MinWidth = 180, MaxWidth = 440, Width = 350 }; search.Width = double.NaN; search.MinWidth = 0; search.Padding = new Thickness(32, 6, 10, 6); searchHost.Children.Add(search); var searchIcon = Ui.Icon(Ui.SearchGlyph, 14, false); searchIcon.IsHitTestVisible = false; searchIcon.HorizontalAlignment = HorizontalAlignment.Left; searchIcon.Margin = new Thickness(10, 0, 0, 0); searchIcon.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); searchHost.Children.Add(searchIcon); var hint = Ui.Text("Имя, пользователь или PID", 13); hint.IsHitTestVisible = false; hint.Margin = new Thickness(32, 0, 10, 0); hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); searchHost.Children.Add(hint); search.TextChanged += (_, _) => hint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; DockPanel.SetDock(searchHost, Dock.Right); searchBar.Children.Add(searchHost); summary.FontSize = 12; summary.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); searchBar.Children.Add(summary); DockPanel.SetDock(searchBar, Dock.Top); processRoot.Children.Add(searchBar); SizeChanged += (_, _) => searchHost.Width = Math.Clamp(ActualWidth * .42, 180, 440);
        MainWindow.AddColumn(processes, "Имя", "Name", 240); processes.Columns[0].Width = new DataGridLength(1, DataGridLengthUnitType.Star); processes.Columns[0].MinWidth = 180; MainWindow.AddColumn(processes, "PID", "Pid", 72); MainWindow.AddColumn(processes, "Пользователь", "User", 110); MainWindow.AddColumn(processes, "Состояние", "State", 112);
        processes.ColumnHeaderHeight = 60; AddMetricColumn("ЦП", "Cpu", "CpuText", 96, nameof(MonitorViewModel.CpuHeader)); AddMetricColumn("Память", "MemoryFraction", "MemoryText", 128, nameof(MonitorViewModel.MemoryHeader)); MainWindow.AddColumn(processes, "Чтение, Б/с", "ReadText", 130); MainWindow.AddColumn(processes, "Запись, Б/с", "WriteText", 130);
        var menu = new ContextMenu { DataContext = viewModel }; foreach (var item in new[] { Ui.AsyncMenu("Завершить процесс (SIGTERM)…", () => Signal(15)), Ui.AsyncMenu("Принудительно завершить (SIGKILL)…", () => Signal(9)) }) { item.SetBinding(IsEnabledProperty, new Binding(nameof(MonitorViewModel.CanSignalSelected))); menu.Items.Add(item); } menu.Items.Add(Ui.Menu("Показать командную строку", ShowCommand)); processes.ContextMenu = menu; processRoot.Children.Add(processes); processTab.Content = processRoot;
        var perf = new StackPanel { Margin = new Thickness(20, 16, 20, 16) }; perf.Children.Add(Ui.Text("Видимое пространство имён Linux (хост или контейнер)", 14, true));
        var graphGrid = new Grid { Margin = new Thickness(0, 16, 0, 16) }; graphGrid.ColumnDefinitions.Add(new ColumnDefinition()); graphGrid.ColumnDefinitions.Add(new ColumnDefinition());
        var cpuPanel = new StackPanel { Margin = new Thickness(0, 0, 12, 0) }; cpuPanel.Children.Add(Ui.Text("ЦП")); cpuPanel.Children.Add(cpu); cpuPanel.Children.Add(cpuGraph); graphGrid.Children.Add(cpuPanel);
        var memPanel = new StackPanel { Margin = new Thickness(12, 0, 0, 0) }; memPanel.Children.Add(Ui.Text("Память")); memPanel.Children.Add(memory); memPanel.Children.Add(memoryGraph); Grid.SetColumn(memPanel, 1); graphGrid.Children.Add(memPanel); perf.Children.Add(graphGrid);
        systemInfo.TextWrapping = TextWrapping.Wrap; systemInfo.Margin = new Thickness(0, 0, 0, 16); perf.Children.Add(systemInfo);
        MainWindow.AddColumn(networks, "Интерфейс", "Name", 160); MainWindow.AddColumn(networks, "Получение", "Receive", 190); MainWindow.AddColumn(networks, "Отправка", "Send", 190); networks.Height = 150; perf.Children.Add(Ui.Text("Сеть", 17, true)); perf.Children.Add(networks);
        MainWindow.AddColumn(disks, "Устройство", "Name", 160); MainWindow.AddColumn(disks, "Чтение", "Read", 190); MainWindow.AddColumn(disks, "Запись", "Write", 190); disks.Height = 150; perf.Children.Add(Ui.Text("Дисковый ввод-вывод", 17, true)); perf.Children.Add(disks);
        MainWindow.AddColumn(storage, "Файловая система", "FileSystem", 200); MainWindow.AddColumn(storage, "Точка монтирования", "MountPoint", 200); MainWindow.AddColumn(storage, "Всего", "Total", 140); MainWindow.AddColumn(storage, "Доступно", "Available", 140); storage.Height = 150; perf.Children.Add(Ui.Text("Место на дисках", 17, true)); perf.Children.Add(storage);
        performance.Content = new ScrollViewer { Content = perf, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Content = root;
        timer.Interval = TimeSpan.FromSeconds(App.Store.Settings.PollIntervalSeconds); timer.Tick += async (_, _) => await Poll();
    }
    void AddMetricColumn(string title, string metric, string text, double width, string aggregateBinding)
    {
        var header = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, DataContext = viewModel }; var total = Ui.Text("", 20); total.HorizontalAlignment = HorizontalAlignment.Right; total.SetBinding(TextBlock.TextProperty, new Binding(aggregateBinding)); header.Children.Add(total); var caption = Ui.Text(title, 12); caption.HorizontalAlignment = HorizontalAlignment.Right; caption.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); header.Children.Add(caption);
        var border = new FrameworkElementFactory(typeof(Border)); border.SetBinding(Border.BackgroundProperty, new Binding(metric) { Converter = new HeatConverter() }); border.SetValue(Border.PaddingProperty, new Thickness(10, 5, 10, 5)); var label = new FrameworkElementFactory(typeof(TextBlock)); label.SetBinding(TextBlock.TextProperty, new Binding(text)); label.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Right); border.AppendChild(label); processes.Columns.Add(new DataGridTemplateColumn { Header = header, CellTemplate = new DataTemplate { VisualTree = border }, SortMemberPath = metric, Width = width, HeaderStyle = new Style(typeof(DataGridColumnHeader), (Style)FindResource(typeof(DataGridColumnHeader))) { Setters = { new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Right) } } });
    }
    public void SetActive(bool value)
    {
        active = value; if (value && !disposed) { timer.Interval = TimeSpan.FromSeconds(Math.Clamp(App.Store.Settings.PollIntervalSeconds, 1, 60)); timer.Start(); _ = Poll(); } else timer.Stop();
    }
    async Task Poll()
    {
        if (disposed || !active || busy) return; busy = true;
        try
        {
            var sample = await monitor.SampleAsync(lifetime.Token); if (disposed) return;
            viewModel.ApplySnapshot(sample, monitor.History);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { viewModel.ReportFailure(ErrorMessages.Describe(ex)); }
        finally { busy = false; }
    }
    void ApplyProcesses()
    {
        if (applyingProcesses) return; applyingProcesses = true;
        try { var view = CollectionViewSource.GetDefaultView(processes.ItemsSource); if (view is not null) { view.SortDescriptions.Clear(); foreach (var sort in processSorts) view.SortDescriptions.Add(sort); } processes.SelectedItem = viewModel.Processes.FirstOrDefault(x => x.Metric.Identity == viewModel.SelectedIdentity); }
        finally { applyingProcesses = false; }
    }
    async Task Signal(int signal)
    {
        if (processes.SelectedItem is not ProcessItemViewModel row) return;
        if (!Ui.Confirm($"{(signal == 9 ? "ПРИНУДИТЕЛЬНО завершить (SIGKILL)" : "Завершить (SIGTERM)")} процесс «{row.Name}», PID {row.Pid}, пользователь {row.User}?\n\nСервер: {session.Profile.Name}\nПеред сигналом повторно проверяются start time и UID. Между проверкой и сигналом остаётся небольшая гонка PID. Автоматического sudo нет.")) return;
        await monitor.SignalAsync(row.Metric.Identity, signal, lifetime.Token); await Poll();
    }
    void ShowCommand()
    {
        if (processes.SelectedItem is not ProcessItemViewModel row) return; var w = new Window { Title = $"PID {row.Pid}: командная строка", Width = 760, Height = 300, Owner = Window.GetWindow(this), WindowStartupLocation = WindowStartupLocation.CenterOwner }; w.Content = new TextBox { IsReadOnly = true, Text = row.Metric.CommandLine ?? "Недоступно: нет прав или процесс завершился.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(18), VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; w.Show();
    }
    async Task OpenTop()
    {
        var caps = await monitor.GetCapabilitiesAsync(lifetime.Token); var command = caps.HasHtop ? "htop" : caps.HasTop ? "top" : null;
        if (command is null) { Ui.Error("На сервере нет доступных htop и top. Графический монитор продолжает работать без них."); return; }
        if (!caps.HasHtop && !Ui.Confirm("htop отсутствует. Открыть установленный top в отдельном терминале?")) return;
        var w = new Window { Title = session.Profile.Name + " — " + command, Width = 1050, Height = 650, Owner = Window.GetWindow(this), WindowStartupLocation = WindowStartupLocation.CenterOwner }; var terminal = new TerminalView(session); w.Content = terminal; var registration = session.Lifetime.Register(() => Dispatcher.BeginInvoke(() => { if (w.IsLoaded) w.Close(); })); w.Closed += (_, _) => { registration.Dispose(); terminal.Dispose(); }; w.Show(); await terminal.StartAsync(); await terminal.SendTextAsync(command + "\r");
    }
    public void Dispose() { disposed = true; timer.Stop(); lifetime.Cancel(); lifetime.Dispose(); }
    sealed class HeatConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => !SystemParameters.HighContrast && value is double n && Application.Current.TryFindResource("Accent") is SolidColorBrush accent ? new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(12 + n * 1.8, 12, 150), accent.Color.R, accent.Color.G, accent.Color.B)) : Brushes.Transparent;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}

public sealed class MetricGraph : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(nameof(Values), typeof(double?[]), typeof(MetricGraph), new FrameworkPropertyMetadata(Array.Empty<double?>(), FrameworkPropertyMetadataOptions.AffectsRender));
    public double?[] Values { get => (double?[])GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); var w = ActualWidth; var h = ActualHeight; var edge = (Brush)FindResource("Edge"); var accent = (Brush)FindResource("Accent"); dc.DrawRectangle((Brush)FindResource("Paper"), new Pen(edge, 1), new Rect(0, 0, w, h));
        for (var i = 1; i < 5; i++) dc.DrawLine(new Pen(edge, .5), new Point(0, h * i / 5), new Point(w, h * i / 5));
        for (var i = 1; i < 6; i++) dc.DrawLine(new Pen(edge, .5), new Point(w * i / 6, 0), new Point(w * i / 6, h));
        var values = Values; Point? previous = null; for (var i = 0; i < values.Length; i++) { if (values[i] is not { } value) { previous = null; continue; } var point = new Point(w * (1 - (values.Length - 1 - i) / (double)Math.Max(119, values.Length - 1)), h * (1 - Math.Clamp(value, 0, 100) / 100)); if (previous is { } p) dc.DrawLine(new Pen(accent, 1.8), p, point); previous = point; }
    }
}

