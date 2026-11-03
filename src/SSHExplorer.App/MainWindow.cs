using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Data;
using SSHExplorer.Core;
using SSHExplorer.App.ViewModels;

namespace SSHExplorer.App;

public sealed partial class MainWindow : Window
{
    readonly TabControl tabs = new() { Padding = new Thickness(0, 0, 160, 0) };
    readonly TabItem home = new();
    readonly DataGrid servers = new();
    readonly TextBox search = new() { MinWidth = 300, ToolTip = "Поиск сохранённых серверов" };
    readonly Dictionary<TabItem, SessionView> sessions = [];
    readonly Dictionary<Guid, int> sessionNumbers = [];
    readonly TextBlock homeStatus = Ui.Text("");
    readonly HomeViewModel viewModel = new();
    bool closing;
    TransfersWindow? transferWindow;
    public MainWindow()
    {
        Style = (Style)FindResource(typeof(Window));
        DataContext = viewModel;
        Title = "SSH Explorer"; Width = 1320; Height = 850; MinWidth = 860; MinHeight = 560; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new Grid();
        var actions = Ui.Row(Ui.Button("＋", AddMenu, "Открыть сервер"), Ui.Button("⇄", ShowTransfers, "Очередь передач"), Ui.Button("⚙", Dialogs.Settings, "Параметры"));
        actions.VerticalAlignment = VerticalAlignment.Top; actions.HorizontalAlignment = HorizontalAlignment.Right; actions.Margin = new Thickness(0, 5, 8, 0); actions.SetResourceReference(BackgroundProperty, "Surface"); root.Children.Add(tabs); root.Children.Add(actions); Content = root;
        home.Header = Ui.Row(Ui.Icon("\uE80F", 16), Ui.Text("Главная")); home.Content = BuildHome(); tabs.Items.Add(home); tabs.SelectedItem = home;
        tabs.SelectionChanged += (_, _) => { foreach (var (tab, view) in sessions) view.SetActive(tabs.SelectedItem == tab); if (tabs.SelectedItem is TabItem item) (item.Header as FrameworkElement)?.BringIntoView(); RefreshHome(); };
        tabs.PreviewDragOver += (_, e) => { if (e.Data.GetDataPresent("SSHExplorer.Tab")) { e.Effects = DragDropEffects.Move; e.Handled = true; } };
        tabs.AllowDrop = true;
        PreviewKeyDown += OnKey;
        Closing += OnClosing;
        RefreshHome();
    }
    UIElement BuildHome()
    {
        var p = new DockPanel(); p.SetResourceReference(Panel.BackgroundProperty, "Paper");
        var title = new Grid { Margin = new Thickness(30, 24, 30, 17) }; title.ColumnDefinitions.Add(new ColumnDefinition()); title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.Children.Add(Ui.Row(Ui.Icon("\uE80F", 24), Ui.Text("Главная", 24, true))); Grid.SetColumn(search, 1); title.Children.Add(search); search.SetBinding(TextBox.TextProperty, new Binding(nameof(HomeViewModel.SearchQuery)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }); DockPanel.SetDock(title, Dock.Top); p.Children.Add(title);
        var commands = Ui.Row(Ui.AsyncButton("＋  Добавить сервер", AddProfile), Ui.AsyncButton("Подключиться", ConnectSelected), Ui.AsyncButton("Изменить", EditSelected), Ui.AsyncButton("Удалить профиль", RemoveSelected));
        commands.Margin = new Thickness(26, 0, 26, 18); DockPanel.SetDock(commands, Dock.Top); p.Children.Add(commands);
        homeStatus.Margin = new Thickness(30, 12, 30, 12); homeStatus.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); homeStatus.SetBinding(TextBlock.TextProperty, new Binding(nameof(HomeViewModel.StatusText))); DockPanel.SetDock(homeStatus, Dock.Bottom); p.Children.Add(homeStatus);
        servers.Margin = new Thickness(28, 0, 28, 0); servers.SelectionMode = DataGridSelectionMode.Single;
        servers.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(HomeViewModel.VisibleProfiles)));
        servers.SetBinding(DataGrid.SelectedItemProperty, new Binding(nameof(HomeViewModel.SelectedProfile)) { Mode = BindingMode.TwoWay });
        AddColumn(servers, "Название", "Name", 260); AddColumn(servers, "Адрес", "Endpoint", 280); AddColumn(servers, "Пользователь", "Username", 180); AddColumn(servers, "Состояние", "Status", 240);
        servers.MouseDoubleClick += async (_, _) => await Ui.Guard(ConnectSelected);
        var menu = new ContextMenu(); menu.Items.Add(Ui.AsyncMenu("Подключиться", ConnectSelected, "Enter")); menu.Items.Add(Ui.AsyncMenu("Изменить профиль", EditSelected)); menu.Items.Add(Ui.AsyncMenu("Удалить профиль", RemoveSelected)); servers.ContextMenu = menu;
        servers.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await Ui.Guard(ConnectSelected); } };
        p.Children.Add(servers); return p;
    }
    public static void AddColumn(DataGrid grid, string title, string binding, double width, string? format = null)
    {
        grid.Columns.Add(new DataGridTextColumn { Header = title, Binding = new Binding(binding) { StringFormat = format, TargetNullValue = "Недоступно" }, Width = width, SortMemberPath = binding });
    }
    void RefreshHome()
    {
        viewModel.Refresh(App.Store.Profiles, sessions.Values.Select(v => (v.Session.Profile.Id, v.Session.State)));
    }
    async Task AddProfile()
    {
        var p = Dialogs.Profile(); if (p is null) return; App.Store.Profiles.Add(p); await App.Store.SaveAsync(); RefreshHome();
    }
    async Task EditSelected()
    {
        if (viewModel.SelectedProfile is not { } row) return; var p = Dialogs.Profile(row.Profile); if (p is null) return;
        var index = App.Store.Profiles.FindIndex(x => x.Id == p.Id); App.Store.Profiles[index] = p; if (!p.SaveSecret) App.Store.DeleteSecret(p.Id); await App.Store.SaveAsync(); RefreshHome();
    }
    async Task RemoveSelected()
    {
        if (viewModel.SelectedProfile is not { } row || !Ui.Confirm($"Удалить локальный профиль «{row.Name}»?\n\nФайлы сервера и открытые сессии сохранятся.")) return;
        await App.Store.DeleteProfileAsync(row.Profile.Id); RefreshHome();
    }
    Task ConnectSelected() => viewModel.SelectedProfile is { } row ? OpenServer(row.Profile) : Task.CompletedTask;
    void AddMenu()
    {
        var menu = new ContextMenu(); foreach (var p in App.Store.Profiles.OrderBy(p => p.Name)) menu.Items.Add(Ui.AsyncMenu(p.Name + "  ·  " + p.Endpoint, () => OpenServer(p)));
        if (menu.Items.Count > 0) menu.Items.Add(new Separator()); menu.Items.Add(Ui.AsyncMenu("Добавить сервер…", AddProfile)); menu.IsOpen = true;
    }
    public async Task OpenServer(ServerProfile profile, string? directory = null, SessionCredentials? suppliedCredentials = null, Func<HostKeyPrompt, Task<TrustDecision>>? trustOverride = null)
    {
        var saved = suppliedCredentials is null ? await App.Store.ReadSecretAsync(profile.Id) : null; var credentials = suppliedCredentials ?? Dialogs.Credentials(profile, saved); if (credentials is null) return;
        if (suppliedCredentials is null) credentials = credentials with { Prompt = prompt => Dispatcher.InvokeAsync(() => Dialogs.Interactive(prompt)).Task };
        if (profile.SaveSecret) await App.Store.SaveSecretAsync(profile.Id, credentials.Secret);
        var session = new ServerSession(profile, App.Store); var view = new SessionView(session, this, directory); var tab = new TabItem { Content = view, ToolTip = profile.Username + "@" + profile.Endpoint };
        var number = sessionNumbers.GetValueOrDefault(profile.Id) + 1; sessionNumbers[profile.Id] = number; var title = profile.Name + (number > 1 ? $" ({number})" : "");
        var close = Ui.Button("×", async () => await CloseTab(tab), "Закрыть сессию"); close.Padding = new Thickness(5, 0, 5, 0); close.MinHeight = 24; close.BorderThickness = new Thickness(0); close.Margin = new Thickness(14, 0, 0, 0);
        var header = Ui.Row(Ui.Icon("\uE968", 16), Ui.Text(title), close); header.ToolTip = tab.ToolTip; tab.ClearValue(ToolTipProperty); tab.Header = header;
        Point? start = null;
        header.PreviewMouseLeftButtonDown += (_, e) => start = e.GetPosition(header);
        header.PreviewMouseMove += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed && start is Point a && (e.GetPosition(header) - a).Length > 12 && !close.IsMouseOver) { start = null; DragDrop.DoDragDrop(header, new DataObject("SSHExplorer.Tab", tab), DragDropEffects.Move); } };
        header.AllowDrop = true;
        header.Drop += (_, e) => { if (e.Data.GetData("SSHExplorer.Tab") is TabItem source && source != home && source != tab) { var dest = tabs.Items.IndexOf(tab); tabs.Items.Remove(source); tabs.Items.Insert(dest, source); tabs.SelectedItem = source; e.Handled = true; } };
        header.DragEnter += (_, e) => { if (!e.Data.GetDataPresent("SSHExplorer.Tab")) tabs.SelectedItem = tab; };
        sessions[tab] = view; tabs.Items.Add(tab); tabs.SelectedItem = tab;
        session.StateChanged += (_, _) => Dispatcher.BeginInvoke(RefreshHome);
        await view.ConnectAsync(credentials, trustOverride); RefreshHome();
    }
    public async Task Reconnect(SessionView previous, string? path)
    {
        var pair = sessions.FirstOrDefault(x => ReferenceEquals(x.Value, previous)); if (pair.Key is null) return; var profile = previous.Session.Profile;
        if (App.Transfers.HasActive(previous.Session.Id) && !Ui.Confirm("В этой сессии выполняются передачи. Прервать их и переподключить вкладку?")) return;
        var credentials = Dialogs.Credentials(profile, await App.Store.ReadSecretAsync(profile.Id)); if (credentials is null) return;
        credentials = credentials with { Prompt = prompt => Dispatcher.InvokeAsync(() => Dialogs.Interactive(prompt)).Task };
        if (profile.SaveSecret) await App.Store.SaveSecretAsync(profile.Id, credentials.Secret);
        App.Transfers.CancelSession(previous.Session.Id); var context = previous.CaptureContext(); await previous.DisposeAsync();
        var session = new ServerSession(profile, App.Store); var next = new SessionView(session, this, path); pair.Key.Content = next; sessions[pair.Key] = next; next.SetActive(tabs.SelectedItem == pair.Key); session.StateChanged += (_, _) => Dispatcher.BeginInvoke(RefreshHome);
        await next.ConnectAsync(credentials); next.RestoreContext(context); RefreshHome();
    }
    async Task CloseTab(TabItem tab)
    {
        if (!sessions.TryGetValue(tab, out var view)) return;
        if (App.Transfers.HasActive(view.Session.Id) && !Ui.Confirm("В этой сессии выполняется передача. Прервать передачу и закрыть сессию?")) return;
        App.Transfers.CancelSession(view.Session.Id); await view.DisposeAsync(); sessions.Remove(tab); tabs.Items.Remove(tab); RefreshHome();
    }
    async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closing) return; e.Cancel = true;
        if (sessions.Values.Any(v => App.Transfers.HasActive(v.Session.Id)) && !Ui.Confirm("Есть активные передачи. Прервать их и выйти?")) return;
        closing = true;
        foreach (var view in sessions.Values.ToArray()) { App.Transfers.CancelSession(view.Session.Id); await view.DisposeAsync(); }
        await App.Store.SaveAsync(); transferWindow?.Close(); Close();
    }
    void OnKey(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.Tab) { e.Handled = true; var delta = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1; tabs.SelectedIndex = (tabs.SelectedIndex + delta + tabs.Items.Count) % tabs.Items.Count; }
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.F4 && tabs.SelectedItem is TabItem tab && tab != home) { e.Handled = true; _ = Ui.Guard(() => CloseTab(tab)); }
        if (!e.Handled && tabs.SelectedItem is TabItem current && sessions.TryGetValue(current, out var session)) session.RouteFileKey(e);
    }
    public void ShowTransfers()
    {
        if (transferWindow is null || !transferWindow.IsLoaded) { transferWindow = new TransfersWindow { Owner = this }; transferWindow.Show(); } else transferWindow.Activate();
    }
}

public sealed class SessionView : UserControl, IAsyncDisposable
{
    public ServerSession Session { get; }
    readonly MainWindow main; readonly Grid host = new(); readonly TextBlock state = Ui.Text("Подключение…");
    readonly StackPanel side = new(); readonly ContentControl screen = new();
    readonly Dictionary<string, Button> navigationButtons = [];
    readonly string? startPath;
    readonly SessionViewModel viewModel = new();
    FileBrowserView? files; TerminalView? terminal; MonitorView? monitor; readonly Button reconnect;
    bool active { get => viewModel.IsActive; set => viewModel.IsActive = value; }
    string section { get => viewModel.CurrentSection; set => viewModel.CurrentSection = value; }
    public SessionView(ServerSession session, MainWindow main, string? startPath)
    {
        Session = session; this.main = main; this.startPath = startPath;
        DataContext = viewModel;
        host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); host.RowDefinitions.Add(new RowDefinition());
        state.Margin = new Thickness(16, 7, 16, 7); state.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); state.SetBinding(TextBlock.TextProperty, new Binding(nameof(SessionViewModel.StatusText))); host.Children.Add(state);
        var body = new Grid(); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Clamp(App.Store.Settings.TreeWidth, 190, 420)) }); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) }); body.ColumnDefinitions.Add(new ColumnDefinition());
        side.Margin = new Thickness(8, 12, 8, 8); side.SetResourceReference(Panel.BackgroundProperty, "Surface");
        side.Children.Add(Nav("\uE756", "Терминал", "terminal")); side.Children.Add(Nav("\uE9D9", "Диспетчер задач", "monitor"));
        var line = new Border { Height = 1, Margin = new Thickness(8, 10, 8, 10) }; line.SetResourceReference(Border.BackgroundProperty, "Edge"); side.Children.Add(line);
        side.Children.Add(Nav("\uE8B7", "Файлы", "files"));
        reconnect = Ui.AsyncButton("Переподключиться", () => main.Reconnect(this, files?.CurrentPath)); reconnect.Visibility = Visibility.Collapsed; reconnect.Margin = new Thickness(7, 8, 7, 4); side.Children.Add(reconnect);
        reconnect.SetBinding(VisibilityProperty, new Binding(nameof(SessionViewModel.CanReconnect)) { Converter = new BooleanToVisibilityConverter() });
        var sidebar = new Border { Child = new ScrollViewer { Content = side, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } }; sidebar.SetResourceReference(Border.BackgroundProperty, "Surface"); body.Children.Add(sidebar);
        var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch }; splitter.SetResourceReference(BackgroundProperty, "Surface"); Grid.SetColumn(splitter, 1); body.Children.Add(splitter); splitter.DragCompleted += (_, _) => { App.Store.Settings.TreeWidth = body.ColumnDefinitions[0].ActualWidth; _ = App.Store.SaveAsync(); };
        Grid.SetColumn(screen, 2); body.Children.Add(screen); Grid.SetRow(body, 1); host.Children.Add(body); Content = host;
        Session.StateChanged += StateChanged;
    }
    Button Nav(string glyph, string text, string key)
    {
        var b = Ui.Button("", () => Show(key)); b.Content = Ui.Row(Ui.Icon(glyph), Ui.Text(text)); b.HorizontalContentAlignment = HorizontalAlignment.Left; b.BorderThickness = new Thickness(0); b.Margin = new Thickness(0, 2, 0, 2); b.Height = 40; navigationButtons[key] = b; return b;
    }
    void StateChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        viewModel.Update(Session);
    });
    public async Task ConnectAsync(SessionCredentials credentials, Func<HostKeyPrompt, Task<TrustDecision>>? trustOverride = null)
    {
        try
        {
            await Session.ConnectAsync(credentials, trustOverride ?? (prompt => Dispatcher.InvokeAsync(() => Dialogs.Trust(prompt)).Task));
            files = new FileBrowserView(Session, main); files.FileNavigationRequested += () => Show("files"); terminal = new TerminalView(Session); monitor = new MonitorView(Session);
            side.Children.Add(files.Navigation); Show("files");
            if (Session.SftpAvailable) await files.NavigateAsync(startPath ?? Session.Profile.InitialPath);
        }
        catch (Exception ex) { viewModel.StatusText = ErrorMessages.Describe(ex); screen.Content = new TextBlock { Text = viewModel.StatusText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(30), MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top }; }
    }
    void Show(string key)
    {
        section = key; monitor?.SetActive(false);
        foreach (var (name, button) in navigationButtons) { button.SetResourceReference(BackgroundProperty, name == key ? "Selected" : "Surface"); button.SetResourceReference(BorderBrushProperty, "Accent"); button.BorderThickness = name == key ? new Thickness(3, 0, 0, 0) : new Thickness(0); }
        if (key == "terminal") { screen.Content = terminal; if (terminal is not null) _ = Ui.Guard(() => terminal.StartAsync()); }
        else if (key == "monitor") { screen.Content = monitor; monitor?.SetActive(active); }
        else screen.Content = files;
    }
    public void SetActive(bool value) { active = value; monitor?.SetActive(value && section == "monitor"); }
    public void RouteFileKey(KeyEventArgs e) { if (section == "files") files?.RouteGlobalKey(e); }
    public object? CaptureContext() => files?.CaptureContext();
    public void RestoreContext(object? context) => files?.RestoreContext(context);
    public async ValueTask DisposeAsync() { Session.StateChanged -= StateChanged; monitor?.Dispose(); files?.Dispose(); terminal?.Dispose(); await Session.DisposeAsync(); }
}

public sealed class TransfersWindow : Window
{
    readonly DataGrid grid = new();
    readonly System.Windows.Threading.DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    public TransfersWindow()
    {
        Style = (Style)FindResource(typeof(Window));
        Title = "Передачи — SSH Explorer"; Width = 1050; Height = 440; MinWidth = 700; MinHeight = 300; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var p = new DockPanel { Margin = new Thickness(18) }; var buttons = Ui.Row(Ui.Button("Отменить", () => (grid.SelectedItem as TransferRow)?.Job.Cancel()), Ui.AsyncButton("Повторить", async () => { if (grid.SelectedItem is TransferRow row) await row.Job.RetryAsync(); })); DockPanel.SetDock(buttons, Dock.Bottom); p.Children.Add(buttons);
        MainWindow.AddColumn(grid, "Название", "Name", 220); MainWindow.AddColumn(grid, "Состояние", "State", 150); MainWindow.AddColumn(grid, "Передано", "Transferred", 120); MainWindow.AddColumn(grid, "Всего", "Total", 120); MainWindow.AddColumn(grid, "Прогресс", "Progress", 100); MainWindow.AddColumn(grid, "Скорость", "Speed", 120); MainWindow.AddColumn(grid, "Осталось", "Remaining", 120); MainWindow.AddColumn(grid, "Источник", "Source", 280); MainWindow.AddColumn(grid, "Назначение", "Destination", 280); MainWindow.AddColumn(grid, "Результат / ошибка", "Result", 380); p.Children.Add(grid); Content = p;
        timer.Tick += (_, _) => { var id = (grid.SelectedItem as TransferRow)?.Job.Id; var rows = App.Transfers.Jobs.Select(j => new TransferRow(j)).ToList(); grid.ItemsSource = rows; grid.SelectedItem = rows.FirstOrDefault(r => r.Job.Id == id); }; timer.Start(); Closed += (_, _) => timer.Stop();
    }
    sealed class TransferRow(TransferJob job)
    {
        public TransferJob Job => job; public string Name => job.Name; public string State => job.State switch { TransferState.Queued => "В очереди", TransferState.Running => "Передача", TransferState.Completed => "Завершено", TransferState.Cancelled => "Отменено", _ => "Ошибка" }; public string Transferred => Ui.Size(job.BytesTransferred); public string Total => job.TotalBytes is { } n ? Ui.Size(n) : "Подсчёт…"; public string Progress => job.Progress is { } n ? $"{n:0.0}%" : "Неизвестно"; public string Speed => job.BytesPerSecond > 0 ? Ui.Size((long)job.BytesPerSecond) + "/с" : "—"; public string Remaining => job.Remaining is { } t ? t.ToString(@"hh\:mm\:ss") : "—"; public string Source => job.Source; public string Destination => job.Destination; public string Result => job.Error ?? job.Summary;
    }
}
