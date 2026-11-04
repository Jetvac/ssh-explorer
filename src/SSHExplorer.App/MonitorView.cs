using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SSHExplorer.App.ViewModels;
using SSHExplorer.Core;
using SSHExplorer.Monitoring;

namespace SSHExplorer.App;

public enum MonitorPage { Processes, Performance }

public sealed class MonitorView : UserControl, IDisposable
{
    public static readonly DependencyProperty PaletteRevisionProperty = DependencyProperty.Register(nameof(PaletteRevision), typeof(int), typeof(MonitorView), new PropertyMetadata(0));
    public int PaletteRevision { get => (int)GetValue(PaletteRevisionProperty); private set => SetValue(PaletteRevisionProperty, value); }
    readonly ServerSession? session;
    readonly LinuxMonitor? monitor;
    readonly MonitorViewModel viewModel;
    readonly CancellationTokenSource lifetime;
    CancellationTokenSource? pollCancellation;
    readonly DispatcherTimer timer = new();
    readonly DispatcherTimer preferenceSaveTimer = new() { Interval=TimeSpan.FromMilliseconds(350) };
    readonly MonitorTablePreferences preferences;
    readonly Action<MonitorPreferences>? publishPreferences;
    readonly List<Action> removeColumnObservers=[];
    readonly Grid header = new(), pages = new();
    readonly TextBlock title = Ui.Text("Процессы", 18, true), status = Ui.Text("", 12), empty = Ui.Text("");
    readonly TextBox search = new() { MinWidth = 150, ToolTip = "Имя, пользователь или PID" };
    readonly Grid searchHost = new();
    readonly DataGrid processes = new(), networks = new(), disks = new(), storage = new();
    readonly ScrollViewer performance = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
    readonly UniformGrid resources = new() { Columns = 2 };
    readonly MetricGraph cpuGraph = new() { Height = 160, Metric = GraphMetric.Cpu }, memoryGraph = new() { Height = 160, Metric = GraphMetric.Memory };
    readonly Button terminate, overflow;
    readonly Dictionary<string, DataGridColumn> columns = [];
    readonly HashSet<Window> ownedWindows = [];
    bool applyingSelection, applyingPreferences, preferencesChanged, disposed, active, pausedByUser, compactCommands, searchSecondRow, nameReflowQueued;
    double responsiveWidth = double.NaN;
    ProcessSortComparer? processComparer;
    string? appliedSortColumn, appliedSortCulture;
    ListSortDirection appliedSortDirection;
    long pollEpoch, pageEpoch;
    double processOffset, performanceOffset;
    MonitorPage page;
    public MonitorPage Page => page;
    internal DataGrid ProcessTable => processes;
    internal Grid Header => header;
    internal MonitorViewModel Model => viewModel;

    public MonitorView(ServerSession session) : this(new MonitorViewModel(), session, App.Store.Settings.Monitor, value=>App.Store.Settings.Monitor=value) { }
    // CI rendering uses the same composition with public data, no SSH or normal settings.
    internal MonitorView(MonitorViewModel model) : this(model, (ServerSession?)null) { }
    internal MonitorView(MonitorViewModel model, LinuxMonitor fixtureMonitor) : this(model, (ServerSession?)null) { monitor = fixtureMonitor; }
    internal MonitorView(MonitorViewModel model, MonitorPreferences saved, Action<MonitorPreferences> persist) : this(model,(ServerSession?)null,saved,persist) { }
    MonitorView(MonitorViewModel model, ServerSession? owner, MonitorPreferences? saved=null, Action<MonitorPreferences>? persist=null)
    {
        preferences=new(saved);publishPreferences=persist;
        session = owner; viewModel = model; DataContext = model;
        lifetime = owner is null ? new CancellationTokenSource() : CancellationTokenSource.CreateLinkedTokenSource(owner.Lifetime);
        if (owner is not null) monitor = new LinuxMonitor(async (command, ct) => { var result = await owner.ExecuteAsync(command, ct); return new ExecResult(result.Output, result.Error, result.ExitStatus); });
        SetResourceReference(BackgroundProperty, "Paper");
        terminate = Ui.AsyncCommandButton(Ui.CloseGlyph, "Завершить задачу", () => Signal(15), "Завершить выбранный процесс (SIGTERM)");
        terminate.SetBinding(IsEnabledProperty, new Binding(nameof(MonitorViewModel.CanSignalSelected)));
        terminate.SetBinding(ToolTipProperty, new Binding(nameof(MonitorViewModel.SignalDisabledReason))); terminate.SetBinding(AutomationProperties.HelpTextProperty, new Binding(nameof(MonitorViewModel.SignalDisabledReason))); ToolTipService.SetShowOnDisabled(terminate, true);
        if (owner is null) { terminate.IsEnabled = false; terminate.ToolTip = "Представление данных без SSH-сессии"; }
        overflow = Ui.FlyoutButton(Ui.MoreGlyph, "", BuildOverflow, "Дополнительные действия");
        BuildHeader();
        BuildProcessTable();
        RestorePreferences();
        BuildPerformance();
        var root = new Grid(); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var commandBar = new Border { Child = header }; commandBar.SetResourceReference(StyleProperty, "CommandBarStyle"); root.Children.Add(commandBar);
        var processSurface = new Grid(); processSurface.Children.Add(processes); empty.HorizontalAlignment = HorizontalAlignment.Center; empty.VerticalAlignment = VerticalAlignment.Center; empty.Margin = new Thickness(24); empty.TextWrapping = TextWrapping.Wrap; empty.IsHitTestVisible = false; empty.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); processSurface.Children.Add(empty);
        pages.Children.Add(processSurface); pages.Children.Add(performance); Grid.SetRow(pages, 1); root.Children.Add(pages);
        var footer = new Grid(); footer.ColumnDefinitions.Add(new ColumnDefinition()); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        status.SetBinding(TextBlock.TextProperty, new Binding(nameof(MonitorViewModel.StatusText))); status.SetBinding(ToolTipProperty, new Binding(nameof(MonitorViewModel.StatusDetails))); status.TextTrimming = TextTrimming.CharacterEllipsis; status.Margin = new Thickness(4, 0, 12, 0); status.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite); footer.Children.Add(status);
        var details = Ui.CommandButton(Ui.PropertiesGlyph, "Сведения", ShowDataDetails, "Сведения о данных, ограничениях и последней ошибке"); details.Height = double.NaN; details.MinHeight = 24; details.Padding = new Thickness(8, 2, 8, 2); details.Margin = new Thickness(0); Grid.SetColumn(details, 1); footer.Children.Add(details);
        var statusBar = new Border { Child = footer }; statusBar.SetResourceReference(StyleProperty, "StatusBarStyle"); Grid.SetRow(statusBar, 2); root.Children.Add(statusBar);
        Content = root;
        viewModel.PropertyChanged += ModelChanged; viewModel.ProcessesUpdated += ApplyProcesses; viewModel.Processes.CollectionChanged += ProcessesChanged;
        Theme.Changed += PaletteChanged;
        // One-shot scheduling: the next request starts one interval after the previous START, or
        // right after completion when collection is slower than the interval. Requests never overlap.
        timer.Tick += async (_, _) => { timer.Stop(); await Poll(); };
        preferenceSaveTimer.Tick += async (_,_) => { preferenceSaveTimer.Stop();await SavePreferencesAsync(); };
        SizeChanged += (_, _) => UpdateResponsive();
        PreviewKeyDown += OnKey;
        ApplyPage(); ApplyProcesses(); UpdateEmpty(); UpdateResponsive();
    }

    void BuildHeader()
    {
        header.Margin = new Thickness(16, 8, 16, 8);
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.SetResourceReference(StyleProperty, "MonitorPageTitleStyle"); title.Margin = new Thickness(4, 0, 20, 0); header.Children.Add(title);
        searchHost.Margin = new Thickness(0, 0, 12, 0); search.Padding = new Thickness(32, 6, 10, 6); search.VerticalAlignment = VerticalAlignment.Center;
        search.SetBinding(TextBox.TextProperty, new Binding(nameof(MonitorViewModel.SearchQuery)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        AutomationProperties.SetName(search, "Поиск процессов по имени, пользователю или PID");
        searchHost.Children.Add(search); var icon = Ui.Icon(Ui.SearchGlyph, 14, false); icon.HorizontalAlignment = HorizontalAlignment.Left; icon.IsHitTestVisible = false; icon.Margin = new Thickness(10, 0, 0, 0); icon.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); searchHost.Children.Add(icon);
        var hint = Ui.Text("Имя, пользователь или PID"); hint.Margin = new Thickness(32, 0, 10, 0); hint.IsHitTestVisible = false; hint.TextTrimming = TextTrimming.CharacterEllipsis; hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); searchHost.Children.Add(hint);
        search.TextChanged += (_, _) => hint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(searchHost, 1); header.Children.Add(searchHost);
        var actions = Ui.Row(terminate, overflow); Grid.SetColumn(actions, 2); header.Children.Add(actions);
    }

    void BuildProcessTable()
    {
        processes.SetResourceReference(StyleProperty, "ProcessTableStyle"); processes.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MonitorViewModel.Processes))); processes.SelectionMode = DataGridSelectionMode.Single;
        var nameRow = new FrameworkElementFactory(typeof(DockPanel)); nameRow.SetValue(DockPanel.LastChildFillProperty, true);
        var icon = new FrameworkElementFactory(typeof(ProcessIcon)); icon.SetValue(MarginProperty, new Thickness(0, 0, 8, 0)); icon.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center); icon.SetValue(DockPanel.DockProperty, Dock.Left); nameRow.AppendChild(icon);
        var label = new FrameworkElementFactory(typeof(TextBlock)); label.SetBinding(TextBlock.TextProperty, new Binding(nameof(ProcessItemViewModel.Name))); label.SetBinding(ToolTipProperty, new Binding(nameof(ProcessItemViewModel.Name))); label.SetBinding(AutomationProperties.NameProperty, new Binding(nameof(ProcessItemViewModel.Name))); label.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); label.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center); nameRow.AppendChild(label);
        AddColumn("Name", new DataGridTemplateColumn { Header = "Имя", CellTemplate = new DataTemplate { VisualTree = nameRow }, SortMemberPath = nameof(ProcessItemViewModel.Name), MinWidth = 180, Width = new DataGridLength(1, DataGridLengthUnitType.Star) }, true);
        AddTextColumn("Pid", "PID", nameof(ProcessItemViewModel.Pid), 78, true);
        AddTextColumn("User", "Пользователь", nameof(ProcessItemViewModel.User), 120, false);
        AddTextColumn("State", "Состояние", nameof(ProcessItemViewModel.State), 120, true);
        AddMetricColumn("Cpu", "ЦП", nameof(ProcessItemViewModel.Cpu), nameof(ProcessItemViewModel.CpuText), nameof(ProcessItemViewModel.CpuReason), 96, nameof(MonitorViewModel.CpuHeader), nameof(MonitorViewModel.CpuReason));
        AddMetricColumn("Memory", "Память", nameof(ProcessItemViewModel.MemoryFraction), nameof(ProcessItemViewModel.MemoryText), nameof(ProcessItemViewModel.MemoryReason), 126, nameof(MonitorViewModel.MemoryHeader), nameof(MonitorViewModel.MemoryReason));
        columns["Memory"].SortMemberPath = nameof(ProcessItemViewModel.MemoryBytes);
        AddTextColumn("Read", "Чтение", nameof(ProcessItemViewModel.ReadText), 118, false, nameof(ProcessItemViewModel.ReadBytesPerSecond), nameof(ProcessItemViewModel.ReadReason));
        AddTextColumn("Write", "Запись", nameof(ProcessItemViewModel.WriteText), 118, false, nameof(ProcessItemViewModel.WriteBytesPerSecond), nameof(ProcessItemViewModel.WriteReason));
        processes.SelectionChanged += (_, _) => { if (!applyingSelection) viewModel.SelectedIdentity = (processes.SelectedItem as ProcessItemViewModel)?.Metric.Identity; };
        processes.Sorting += OnSorting;
        processes.ColumnDisplayIndexChanged += (_,_) => { if(!applyingPreferences){preferences.SetOrder(columns.OrderBy(p=>p.Value.DisplayIndex).Select(p=>p.Key));CommitPreferences();} };
        foreach(var (key,column) in columns)
        {
            var descriptor=DependencyPropertyDescriptor.FromProperty(DataGridColumn.WidthProperty,typeof(DataGridColumn));
            EventHandler changed=(_,_)=>
            {
                if(applyingPreferences||disposed)return;
                var width=column.Width;var mode=width.IsStar?MonitorColumnWidthMode.Star:width.IsAuto?MonitorColumnWidthMode.Auto:MonitorColumnWidthMode.Pixel;
                double value=width.IsAuto?1:width.Value;
                if(preferences[key].WidthMode==mode&&preferences[key].Width==value)return;
                preferences.SetWidth(key,value,mode);CommitPreferences();
            };
            descriptor.AddValueChanged(column,changed);removeColumnObservers.Add(()=>descriptor.RemoveValueChanged(column,changed));
        }
        processes.SizeChanged += (_, args) => { if (args.WidthChanged) ReflowNameColumnAfterLayout(); };
        processes.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, args) => { if (args.ViewportWidthChange != 0) ReflowNameColumnAfterLayout(); }));
        Ui.AttachContextMenu(processes, BuildProcessMenu);
    }

    void AddColumn(string key, DataGridColumn column, bool visible)
    {
        if (column.Header is string)
        {
            var caption = new FrameworkElementFactory(typeof(TextBlock)); caption.SetBinding(TextBlock.TextProperty, new Binding()); caption.SetBinding(ToolTipProperty, new Binding()); caption.SetBinding(AutomationProperties.NameProperty, new Binding()); caption.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); caption.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap); caption.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Stretch); caption.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            column.HeaderTemplate = new DataTemplate { VisualTree = caption };
        }
        column.HeaderStyle = (Style)FindResource("ProcessColumnHeaderStyle"); columns[key] = column; processes.Columns.Add(column); column.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }
    void AddTextColumn(string key, string caption, string text, double width, bool visible, string? numericSort = null, string? reason = null)
    {
        var column = new DataGridTextColumn { Header = caption, Width = width, MinWidth = 56, SortMemberPath = numericSort ?? text, Binding = new Binding(text) };
        var style = new Style(typeof(TextBlock)); style.Setters.Add(new Setter(VerticalAlignmentProperty, VerticalAlignment.Center)); style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        if (key is "Pid" or "Read" or "Write") style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));
        if (reason is not null) { style.Setters.Add(new Setter(ToolTipProperty, new Binding(reason))); style.Setters.Add(new Setter(AutomationProperties.HelpTextProperty, new Binding(reason))); }
        column.ElementStyle = style; AddColumn(key, column, visible);
    }
    void AddMetricColumn(string key, string caption, string metric, string text, string reason, double width, string aggregate, string aggregateReason)
    {
        var heading = new StackPanel { DataContext = viewModel, HorizontalAlignment = HorizontalAlignment.Stretch };
        var total = Ui.Text("", 20); total.TextAlignment = TextAlignment.Right; total.SetBinding(TextBlock.TextProperty, new Binding(aggregate)); total.SetBinding(ToolTipProperty, new Binding(aggregateReason)); total.SetBinding(AutomationProperties.HelpTextProperty, new Binding(aggregateReason)); heading.Children.Add(total);
        var name = Ui.Text(caption, 12); name.TextAlignment = TextAlignment.Right; name.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); heading.Children.Add(name);
        var cell = new Style(typeof(DataGridCell), (Style)FindResource("ProcessMetricCellStyle")); cell.Setters.Add(new Setter(AutomationProperties.HelpTextProperty, new Binding(reason)));
        var border = new FrameworkElementFactory(typeof(Border), "MetricSurface"); border.SetValue(NameProperty, "MetricSurface"); border.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Stretch); border.SetValue(VerticalAlignmentProperty, VerticalAlignment.Stretch); border.SetValue(Border.PaddingProperty, new Thickness(10, 4, 10, 4));
        var heat = new MultiBinding { Converter = new MetricHeatConverter() }; heat.Bindings.Add(new Binding(metric)); heat.Bindings.Add(new Binding(nameof(DataGridRow.IsSelected)) { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(DataGridRow), 1) }); heat.Bindings.Add(new Binding(nameof(DataGridRow.IsMouseOver)) { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(DataGridRow), 1) }); heat.Bindings.Add(new Binding(nameof(PaletteRevision)) { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(MonitorView), 1) }); heat.Bindings.Add(new Binding(nameof(ProcessItemViewModel.IsStale))); border.SetBinding(Border.BackgroundProperty, heat); border.SetBinding(ToolTipProperty, new Binding(reason));
        var value = new FrameworkElementFactory(typeof(TextBlock), "MetricValue"); value.SetValue(NameProperty, "MetricValue"); value.SetResourceReference(StyleProperty, "ProcessValueStyle"); value.SetBinding(TextBlock.TextProperty, new Binding(text)); value.SetBinding(AutomationProperties.HelpTextProperty, new Binding(reason)); value.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Right); value.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center); border.AppendChild(value);
        AddColumn(key, new DataGridTemplateColumn { Header = heading, CellTemplate = new DataTemplate { VisualTree = border }, CellStyle = cell, SortMemberPath = metric, Width = width, MinWidth = 76 }, true);
    }

    void BuildPerformance()
    {
        cpuGraph.SetBinding(MetricGraph.HistoryProperty, new Binding(nameof(MonitorViewModel.History))); memoryGraph.SetBinding(MetricGraph.HistoryProperty, new Binding(nameof(MonitorViewModel.History)));
        resources.Children.Add(ResourcePanel("ЦП", nameof(MonitorViewModel.CpuText), nameof(MonitorViewModel.CpuReason), cpuGraph));
        resources.Children.Add(ResourcePanel("Память", nameof(MonitorViewModel.MemoryText), nameof(MonitorViewModel.MemoryReason), memoryGraph));
        var body = new StackPanel { Margin = new Thickness(20, 16, 20, 16) }; body.Children.Add(resources);
        AddPerformanceTable(body, "Сеть", networks, nameof(MonitorViewModel.Networks), [("Интерфейс", "Name", 150), ("Получение", "Receive", 170), ("Отправка", "Send", 170)]);
        AddPerformanceTable(body, "Дисковый ввод-вывод", disks, nameof(MonitorViewModel.Disks), [("Устройство", "Name", 150), ("Чтение", "Read", 170), ("Запись", "Write", 170)]);
        AddPerformanceTable(body, "Хранилища", storage, nameof(MonitorViewModel.Storage), [("Файловая система", "FileSystem", 170), ("Точка монтирования", "MountPoint", 180), ("Всего", "Total", 130), ("Доступно", "Available", 130)]);
        performance.Content = body;
    }
    FrameworkElement ResourcePanel(string caption, string valueProperty, string reasonProperty, MetricGraph graph)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 16, 16) };
        var row = new DockPanel(); var value = Ui.Text("", 24, true); value.HorizontalAlignment = HorizontalAlignment.Right; value.SetBinding(TextBlock.TextProperty, new Binding(valueProperty)); value.SetBinding(ToolTipProperty, new Binding(reasonProperty)); value.SetBinding(AutomationProperties.HelpTextProperty, new Binding(reasonProperty)); DockPanel.SetDock(value, Dock.Right); row.Children.Add(value); row.Children.Add(Ui.Text(caption, 18, true)); panel.Children.Add(row);
        var unit = Ui.Text("% использования · наведите указатель, чтобы увидеть значение", 12); unit.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); unit.Margin = new Thickness(0, 10, 0, 4); unit.TextTrimming = TextTrimming.CharacterEllipsis; panel.Children.Add(unit); panel.Children.Add(graph);
        var axis = new DockPanel { Margin = new Thickness(0, 4, 40, 0) }; var span = Ui.Text(graph.WindowLabel, 12); span.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); axis.Children.Add(span); var interval = Ui.Text(graph.IntervalLabel, 12); interval.TextAlignment = TextAlignment.Right; interval.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); axis.Children.Add(interval); graph.IntervalChanged += () => { interval.Text = graph.IntervalLabel; span.Text = graph.WindowLabel; }; panel.Children.Add(axis);
        return panel;
    }
    void AddPerformanceTable(Panel parent, string caption, DataGrid table, string source, (string Caption, string Property, double Width)[] fields)
    {
        var name = Ui.Text(caption, 18, true); name.Margin = new Thickness(0, 12, 0, 6); parent.Children.Add(name);
        table.SetResourceReference(StyleProperty, "ProcessTableStyle"); table.ColumnHeaderHeight = 36; table.MaxHeight = 180; table.MinHeight = 76;
        table.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(source));
        foreach (var field in fields) MainWindow.AddColumn(table, field.Caption, field.Property, field.Width);
        parent.Children.Add(table);
    }

    public void SetPage(MonitorPage value)
    {
        if (disposed || page == value) return;
        if (page == MonitorPage.Processes) processOffset = FindScroll(processes)?.VerticalOffset ?? processOffset; else performanceOffset = performance.VerticalOffset;
        page = value; ApplyPage(); if (monitor is not null) monitor.Scope = page == MonitorPage.Processes ? MonitorScope.Processes : MonitorScope.System;
        var version = ++pageEpoch;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { if (disposed || version != pageEpoch) return; if (page == MonitorPage.Processes) FindScroll(processes)?.ScrollToVerticalOffset(processOffset); else performance.ScrollToVerticalOffset(performanceOffset); });
    }
    void ApplyPage()
    {
        pages.Children[0].Visibility = page == MonitorPage.Processes ? Visibility.Visible : Visibility.Collapsed;
        performance.Visibility = page == MonitorPage.Performance ? Visibility.Visible : Visibility.Collapsed;
        title.Text = page == MonitorPage.Processes ? "Процессы" : "Производительность"; searchHost.Visibility = page == MonitorPage.Processes ? Visibility.Visible : Visibility.Collapsed;
        UpdateResponsive(); UpdateGraphTime();
    }
    void UpdateResponsive()
    {
        if (disposed) return;
        var width = ActualWidth > 0 ? ActualWidth : 1000;
        var columnsChanged = false;
        compactCommands = width < 780;
        terminate.Visibility = page == MonitorPage.Processes && !compactCommands ? Visibility.Visible : Visibility.Collapsed;
        var second = width < 500 && page == MonitorPage.Processes;
        if (second != searchSecondRow)
        {
            searchSecondRow = second; Grid.SetRow(searchHost, second ? 1 : 0); Grid.SetColumn(searchHost, second ? 0 : 1); Grid.SetColumnSpan(searchHost, second ? 3 : 1); searchHost.Margin = second ? new Thickness(4, 8, 4, 0) : new Thickness(0, 0, 12, 0);
        }
        resources.Columns = width < 680 ? 1 : 2;
        foreach (var key in new[] { "Pid", "User", "State", "Read", "Write" })
        {
            var visibility = preferences.EffectiveVisible(key,width) ? Visibility.Visible : Visibility.Collapsed;
            if (columns[key].Visibility != visibility) { columns[key].Visibility = visibility; columnsChanged = true; }
        }
        if (columnsChanged || responsiveWidth != width) { responsiveWidth = width; ReflowNameColumnAfterLayout(); }
    }
    void ReflowNameColumnAfterLayout()
    {
        if (disposed || nameReflowQueued || !columns.TryGetValue("Name", out var name) || !name.Width.IsStar) return;
        nameReflowQueued = true;
        // Column visibility and the cells viewport settle in this dispatcher turn.
        // Recreate only the star width cache, without a timer, row reset or refresh.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            nameReflowQueued = false;
            if (disposed || processes.ActualWidth <= 0 || !name.Width.IsStar) return;
            var weight = name.Width.Value;
            applyingPreferences=true;
            try { name.Width = DataGridLength.Auto;name.Width = new DataGridLength(weight, DataGridLengthUnitType.Star); }
            finally { applyingPreferences=false; }
            processes.InvalidateMeasure();
        });
    }

    void RestorePreferences()
    {
        applyingPreferences=true;
        try
        {
            foreach(var key in MonitorTablePreferences.Keys)
            {
                var setting=preferences[key];var column=columns[key];
                column.Width=setting.WidthMode switch{MonitorColumnWidthMode.Star=>new DataGridLength(setting.Width,DataGridLengthUnitType.Star),MonitorColumnWidthMode.Auto=>DataGridLength.Auto,_=>new DataGridLength(setting.Width)};
            }
            foreach(var setting in preferences.Snapshot().Columns)columns[setting.Key].DisplayIndex=setting.Order;
            ApplySortIndicators();
        }
        finally { applyingPreferences=false; }
    }
    void CommitPreferences()
    {
        if(disposed||applyingPreferences)return;
        preferencesChanged=true;
        publishPreferences?.Invoke(preferences.Snapshot());
        if(session is not null){preferenceSaveTimer.Stop();preferenceSaveTimer.Start();}
    }
    async Task SavePreferencesAsync()
    {
        if(session is null)return;
        try { await App.Store.SaveAsync(); }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { await LocalDiagnostics.RecordAsync("monitor.preferences.save.failed",error); }
    }
    void OnSorting(object sender,DataGridSortingEventArgs args)
    {
        args.Handled=true;
        string? key=columns.FirstOrDefault(p=>ReferenceEquals(p.Value,args.Column)).Key;
        if(key is null)return;
        var direction=preferences.SortColumn==key&&preferences.SortDirection==ListSortDirection.Ascending?ListSortDirection.Descending:ListSortDirection.Ascending;
        SetSort(key,direction);
    }
    internal void SetSort(string? key,ListSortDirection direction)
    { if(disposed)return;preferences.SetSort(key,direction);ApplySortIndicators();ApplyProcesses();CommitPreferences(); }
    void ApplySortIndicators()
    { foreach(var (key,column) in columns)column.SortDirection=key==preferences.SortColumn?preferences.SortDirection:null; }
    MenuItem BuildSortMenu()
    {
        var menu=new MenuItem{Header="Сортировать",Icon=Ui.Icon("\uE8CB",16,false)};
        foreach(var key in MonitorTablePreferences.Keys)
        {
            var item=new MenuItem{Header=MonitorTablePreferences.Caption(key),IsCheckable=true,IsChecked=preferences.SortColumn==key};
            item.Click+=(_,_)=>SetSort(key,preferences.SortDirection);menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        foreach(var direction in new[]{ListSortDirection.Ascending,ListSortDirection.Descending})
        {
            var item=new MenuItem{Header=direction==ListSortDirection.Ascending?"По возрастанию":"По убыванию",IsCheckable=true,IsChecked=preferences.SortColumn is not null&&preferences.SortDirection==direction};
            item.Click+=(_,_)=>SetSort(preferences.SortColumn??"Name",direction);menu.Items.Add(item);
        }
        menu.Items.Add(Ui.Menu("Исходный порядок",()=>SetSort(null,ListSortDirection.Ascending)));
        return menu;
    }

    ContextMenu BuildOverflow()
    {
        var menu = new ContextMenu { DataContext = viewModel };
        if (page == MonitorPage.Processes)
        {
            if (compactCommands) menu.Items.Add(SignalItem("Завершить задачу (SIGTERM)…", 15));
            menu.Items.Add(SignalItem("Принудительно завершить (SIGKILL)…", 9)); menu.Items.Add(new Separator());
            var choose = new MenuItem { Header = "Выбрать столбцы", Icon = Ui.Icon(IconCatalog.ViewDetails, 16, false) };
            foreach (var (key, column) in columns)
            {
                bool autoHidden=preferences[key].Visible&&column.Visibility!=Visibility.Visible;
                var item = new MenuItem { Header = MonitorTablePreferences.Caption(key), IsCheckable = true, IsChecked = preferences[key].Visible, IsEnabled = key is not "Name" and not "Cpu" and not "Memory", InputGestureText=autoHidden?"Скрыт в узком окне":"" };
                item.ToolTip=autoHidden?"Столбец выбран; временно скрыт при узком окне.":null;
                item.Click += (_, _) => { preferences.SetVisible(key,item.IsChecked);CommitPreferences();UpdateResponsive(); }; choose.Items.Add(item);
            }
            menu.Items.Add(choose);menu.Items.Add(BuildSortMenu()); var details = Ui.Menu("Сведения о выбранном процессе", ShowProcessDetails, "Alt+Enter", Ui.PropertiesGlyph); details.IsEnabled = viewModel.SelectedIdentity is not null; menu.Items.Add(details); menu.Items.Add(new Separator());
        }
        var refresh = Ui.AsyncMenu("Обновить сейчас", Poll, "F5", Ui.RefreshGlyph); refresh.IsEnabled = monitor is not null && viewModel.IsActive && !viewModel.IsBusy && !viewModel.IsCommandRunning; menu.Items.Add(refresh);
        var pause = Ui.Menu(pausedByUser ? "Продолжить обновление" : "Приостановить обновление", () => { pausedByUser = !pausedByUser; ApplyPolling(); }, glyph: pausedByUser ? "\uE768" : "\uE769"); pause.IsEnabled = monitor is not null; menu.Items.Add(pause);
        var top = Ui.AsyncMenu("Открыть htop / top в терминале", OpenTop, glyph: Ui.TerminalGlyph); top.IsEnabled = session?.ShellAvailable == true; menu.Items.Add(top);
        menu.Items.Add(Ui.Menu("Сведения о данных", ShowDataDetails, "F1", Ui.PropertiesGlyph)); return menu;
    }
    ContextMenu BuildProcessMenu()
    {
        var menu = new ContextMenu { DataContext = viewModel }; menu.Items.Add(SignalItem("Завершить процесс (SIGTERM)…", 15)); menu.Items.Add(SignalItem("Принудительно завершить (SIGKILL)…", 9)); menu.Items.Add(new Separator());
        var info = Ui.Menu("Сведения о процессе", ShowProcessDetails, "Alt+Enter", Ui.PropertiesGlyph); info.IsEnabled = viewModel.SelectedIdentity is not null; menu.Items.Add(info); return menu;
    }
    MenuItem SignalItem(string caption, int signal)
    {
        var item = Ui.AsyncMenu(caption, () => Signal(signal), glyph: Ui.CloseGlyph); item.SetBinding(IsEnabledProperty, new Binding(nameof(MonitorViewModel.CanSignalSelected))); item.SetBinding(ToolTipProperty, new Binding(nameof(MonitorViewModel.SignalDisabledReason))); item.SetBinding(AutomationProperties.HelpTextProperty, new Binding(nameof(MonitorViewModel.SignalDisabledReason))); ToolTipService.SetShowOnDisabled(item, true); if (session is null) item.IsEnabled = false; return item;
    }

    public void SetActive(bool value) { if (disposed) return; active = value; ApplyPolling(); }
    void ApplyPolling()
    {
        var effective = active && !pausedByUser;
        if (viewModel.IsActive == effective && (timer.IsEnabled || pollCancellation is not null || !effective || monitor is null)) return;
        ++pollEpoch; viewModel.IsActive = effective; timer.Stop(); UpdateGraphTime();
        if (!effective) { pollCancellation?.Cancel(); monitor?.MarkGap(DateTimeOffset.UtcNow, pausedByUser ? "Обновление приостановлено пользователем" : "Раздел мониторинга скрыт"); return; }
        if (monitor is null) return;
        var interval = session is null ? monitor.ExpectedInterval : TimeSpan.FromSeconds(Math.Clamp(App.Store.Settings.PollIntervalSeconds, 1, 60)); monitor.ExpectedInterval = interval;
        monitor.Scope = page == MonitorPage.Processes ? MonitorScope.Processes : MonitorScope.System;
        monitor.HistoryWindow = cpuGraph.Window + TimeSpan.FromMinutes(1);
        cpuGraph.GapThreshold = memoryGraph.GapThreshold = monitor.ContinuityLimit; _ = Poll();
    }
    async Task Poll()
    {
        if (disposed || monitor is null || !viewModel.IsActive || pollCancellation is not null || viewModel.IsBusy || viewModel.IsCommandRunning) return;
        var epoch = pollEpoch; var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); pollCancellation = request; viewModel.IsBusy = true; var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var sample = await monitor.SampleAsync(request.Token);
            if (disposed || epoch != pollEpoch || !viewModel.IsActive) return;
            viewModel.ApplySnapshot(sample, monitor.History); UpdateGraphTime();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex) { if (!disposed && epoch == pollEpoch && viewModel.IsActive) { viewModel.ReportFailure(ErrorMessages.Describe(ex), monitor.History); UpdateGraphTime(); } }
        finally
        {
            if (ReferenceEquals(pollCancellation, request)) pollCancellation = null;
            // Hide/Dispose only cancels. The request owns disposal after its awaited
            // callback has unwound, so rapid resume never reuses a disposed CTS.
            request.Dispose(); viewModel.IsBusy = false;
            if (!disposed && viewModel.IsActive && epoch == pollEpoch && monitor is not null)
            {
                var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(startedAt);
                var rest = TimeSpan.FromMilliseconds(Math.Min(250, monitor.ExpectedInterval.TotalMilliseconds / 4));
                var delay = monitor.ExpectedInterval - elapsed; if (delay < rest) delay = rest;
                cpuGraph.GapThreshold = memoryGraph.GapThreshold = monitor.ContinuityLimit;
                timer.Interval = delay; timer.Start();
            }
            if (!disposed && viewModel.IsActive && epoch != pollEpoch)
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { if (!disposed && viewModel.IsActive) _ = Poll(); });
        }
    }
    void ApplyProcesses()
    {
        if (disposed || applyingSelection) return; applyingSelection = true;
        try
        {
            var view = CollectionViewSource.GetDefaultView(processes.ItemsSource);
            double offset=FindScroll(processes)?.VerticalOffset??processOffset;
            if(view is ListCollectionView sortable)
            {
                if(preferences.SortColumn is { } key)
                {
                    if(sortable.IsLiveSorting is true)sortable.IsLiveSorting=false;
                    string culture=CultureInfo.CurrentCulture.Name;
                    if(processComparer is null||appliedSortColumn!=key||appliedSortDirection!=preferences.SortDirection||appliedSortCulture!=culture||!ReferenceEquals(sortable.CustomSort,processComparer))
                    {
                        processComparer=new ProcessSortComparer(key,preferences.SortDirection);appliedSortColumn=key;appliedSortDirection=preferences.SortDirection;appliedSortCulture=culture;
                        sortable.CustomSort=processComparer;
                    }
                    else sortable.Refresh(); // A completed snapshot may change numeric ordering.
                }
                else if(sortable.CustomSort is ProcessSortComparer){sortable.CustomSort=null;processComparer=null;}
            }
            SelectCurrentProcess();
            FindScroll(processes)?.ScrollToVerticalOffset(offset);
        }
        finally { applyingSelection = false; }
        UpdateEmpty();
    }
    void SyncProcessSelection()
    {
        if(disposed||applyingSelection)return;
        applyingSelection=true;
        try { SelectCurrentProcess(); }
        finally { applyingSelection=false; }
    }
    void SelectCurrentProcess()
    {
        var selected=viewModel.Processes.FirstOrDefault(x=>x.Metric.Identity==viewModel.SelectedIdentity);
        if(!ReferenceEquals(processes.SelectedItem,selected))processes.SelectedItem=selected;
    }
    void ModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MonitorViewModel.SelectedIdentity)) SyncProcessSelection();
        if (args.PropertyName is nameof(MonitorViewModel.Snapshot) or nameof(MonitorViewModel.HasError) or nameof(MonitorViewModel.SearchQuery) or nameof(MonitorViewModel.IsActive)) UpdateEmpty();
    }
    void ProcessesChanged(object? sender, NotifyCollectionChangedEventArgs args) => UpdateEmpty();
    void UpdateEmpty()
    {
        empty.Visibility = viewModel.Processes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        empty.Text = viewModel.Snapshot is null ? viewModel.HasError ? "Не удалось получить процессы" : !viewModel.IsActive ? "Обновление приостановлено" : "Собираем данные…" : search.Text.Length > 0 ? "Нет совпадений" : "Нет доступных процессов";
    }
    void UpdateGraphTime() { cpuGraph.Now = memoryGraph.Now = session is null && monitor is null ? DateTimeOffset.UtcNow : SteadyClock.Now; }

    async Task Signal(int signal)
    {
        if (disposed || monitor is null || session is null) return;
        try
        {
            await viewModel.SignalSelectedAsync(signal,(target,ct)=>Task.FromResult(Ui.Confirm($"{(signal == 9 ? "Принудительно завершить (SIGKILL)" : "Завершить (SIGTERM)")} процесс «{target.Name}», PID {target.Pid}, пользователь {target.User}?\n\nСервер: {session.Profile.Name}\nИдентичность и права будут повторно проверены сервером. Автоматического sudo нет.")),
                async(identity,number,ct)=> { pollCancellation?.Cancel();await monitor.SignalAsync(identity,number,ct); },lifetime.Token);
        }
        finally { if(!disposed&&viewModel.IsActive)await Poll(); }
    }
    void ShowDataDetails() => ShowReadOnly("Сведения о данных", viewModel.StatusDetails + "\n\n" + viewModel.DataDetails + "\n\nИстория: " + cpuGraph.IntervalLabel + ". Пустые интервалы обозначают отсутствие измерений.");
    void ShowProcessDetails()
    {
        if (processes.SelectedItem is not ProcessItemViewModel row) return;
        ShowReadOnly($"PID {row.Pid} — сведения", $"{row.Name}\nПользователь: {row.User}\nСостояние: {row.State}\n\nЦП: {row.CpuText}\n{row.CpuReason}\n\nПамять: {row.MemoryText}\n{row.MemoryReason}\n\nЧтение: {row.ReadText}\n{row.ReadReason}\n\nЗапись: {row.WriteText}\n{row.WriteReason}\n\nКомандная строка:\n{row.Metric.CommandLine ?? "Нет доступа или процесс завершился"}");
    }
    void ShowReadOnly(string caption, string text)
    {
        var window = new Window { Title = caption, Width = 730, Height = 500, Owner = Window.GetWindow(this), WindowStartupLocation = WindowStartupLocation.CenterOwner };
        window.Content = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Track(window); window.Show();
    }
    async Task OpenTop()
    {
        if (monitor is null || session?.ShellAvailable != true) return;
        var caps = await monitor.GetCapabilitiesAsync(lifetime.Token); var command = caps.HasHtop ? "htop" : caps.HasTop ? "top" : null;
        if (command is null) { Ui.Error("На сервере нет доступных htop и top."); return; }
        if (!caps.HasHtop && !Ui.Confirm("htop отсутствует. Открыть установленный top в отдельном терминале?")) return;
        var window = new Window { Title = session.Profile.Name + " — " + command, Width = 1050, Height = 650, Owner = Window.GetWindow(this), WindowStartupLocation = WindowStartupLocation.CenterOwner }; var terminal = new TerminalView(session); window.Content = terminal; window.Closed += (_, _) => terminal.Dispose(); Track(window); window.Show(); await terminal.StartAsync(); await terminal.SendTextAsync(command + "\r");
    }
    void Track(Window window) { ownedWindows.Add(window); window.Closed += (_, _) => ownedWindows.Remove(window); }
    void OnKey(object sender, KeyEventArgs args)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && args.Key == Key.F && page == MonitorPage.Processes) { args.Handled = true; search.Focus(); search.SelectAll(); }
        else if (args.Key == Key.F1) { args.Handled = true; ShowDataDetails(); }
        else if (args.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Alt) { args.Handled = true; ShowProcessDetails(); }
        else if (args.Key == Key.F5 && monitor is not null) { args.Handled = true; _ = Ui.Guard(Poll); }
    }
    void PaletteChanged() { PaletteRevision = unchecked(PaletteRevision + 1); cpuGraph.InvalidateVisual(); memoryGraph.InvalidateVisual(); processes.InvalidateVisual(); }
    static ScrollViewer? FindScroll(DependencyObject parent)
    {
        if (parent is ScrollViewer viewer) return viewer;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) if (FindScroll(VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
        return null;
    }
    public void Dispose()
    {
        if(disposed)return;
        preferenceSaveTimer.Stop();
        disposed = true; ++pollEpoch; ++pageEpoch; timer.Stop(); pollCancellation?.Cancel(); lifetime.Cancel(); viewModel.IsActive = false;
        foreach(var remove in removeColumnObservers)remove();removeColumnObservers.Clear();
        if(session is not null&&preferencesChanged)_=SavePreferencesAsync();
        viewModel.PropertyChanged -= ModelChanged; viewModel.ProcessesUpdated -= ApplyProcesses; viewModel.Processes.CollectionChanged -= ProcessesChanged; Theme.Changed -= PaletteChanged;
        foreach (var window in ownedWindows.ToArray()) if (window.IsLoaded) window.Close(); ownedWindows.Clear(); lifetime.Dispose();
    }

    sealed class MetricHeatConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (SystemParameters.HighContrast || values[0] is not double number || !double.IsFinite(number) || values[1] is true || values[2] is true || values[4] is true || Application.Current.TryFindResource("Accent") is not SolidColorBrush accent) return Brushes.Transparent;
            var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(12 + Math.Clamp(number, 0, 100) * 1.5, 12, 162), accent.Color.R, accent.Color.G, accent.Color.B)); brush.Freeze(); return brush;
        }
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
