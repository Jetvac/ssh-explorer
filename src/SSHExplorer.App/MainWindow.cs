using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Data;
using System.Windows.Shell;
using System.Windows.Threading;
using System.IO;
using System.Text;
using SSHExplorer.Core;
using SSHExplorer.App.ViewModels;

namespace SSHExplorer.App;

public sealed partial class MainWindow : Window
{
    readonly TabControl tabs = new() { Padding = new Thickness(0, 0, 278, 0), Tag = "WindowTabs" };
    readonly TabItem home = new();
    readonly DataGrid servers = new();
    readonly TextBox search = new() { MinWidth = 300, ToolTip = "Поиск сохранённых серверов" };
    readonly Dictionary<TabItem, SessionView> sessions = [];
    readonly Dictionary<Guid, int> sessionNumbers = [];
    readonly TextBlock homeStatus = Ui.Text("");
    readonly HomeViewModel viewModel = new();
    readonly Dictionary<string, WeakReference<TabItem>> tabDragTokens = [];
    readonly Dictionary<TabItem, DispatcherTimer> tabHoverTimers = [];
    bool closing, refreshingHome;
    TransfersWindow? transferWindow;
    public MainWindow()
    {
        Style = (Style)FindResource(typeof(Window));
        // The glass strip belongs to DWM. An opaque WPF background over it
        // hides the real caption buttons even when native hit testing is enabled.
        Background = Brushes.Transparent;
        DataContext = viewModel;
        Title = "SSH Explorer"; Width = 1320; Height = 850; MinWidth = 720; MinHeight = 500; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 44, GlassFrameThickness = new Thickness(0, 44, 0, 0), ResizeBorderThickness = SystemParameters.WindowResizeBorderThickness, CornerRadius = new CornerRadius(8), UseAeroCaptionButtons = true });
        var root = new Grid();
        var actions = Ui.Row(Ui.IconButton(Ui.AddGlyph, AddMenu, "Открыть сервер"), Ui.IconButton("\uE8AB", ShowTransfers, "Очередь передач"), Ui.IconButton(Ui.SettingsGlyph, Dialogs.Settings, "Параметры"));
        actions.VerticalAlignment = VerticalAlignment.Top; actions.HorizontalAlignment = HorizontalAlignment.Right; actions.Margin = new Thickness(0, 4, 146, 0); WindowChrome.SetIsHitTestVisibleInChrome(actions, true); root.Children.Add(tabs); root.Children.Add(actions); Content = root;
        home.Header = Ui.Row(Ui.Icon("\uE80F", 16), Ui.Text("Главная")); home.Content = BuildHome(); tabs.Items.Add(home); tabs.SelectedItem = home;
        tabs.SelectionChanged += (_, e) => { if (!ReferenceEquals(e.OriginalSource, tabs)) return; foreach (var (tab, view) in sessions) view.SetActive(tabs.SelectedItem == tab); if (tabs.SelectedItem is TabItem item) (item.Header as FrameworkElement)?.BringIntoView(); RefreshHome(); };
        tabs.PreviewDragOver += (_, e) => { if (e.Data.GetDataPresent("SSHExplorer.Tab")) { e.Effects = DragDropEffects.Move; e.Handled = true; } };
        tabs.AllowDrop = true;
        PreviewKeyDown += OnKey;
        Closing += OnClosing;
        RefreshHome();
    }
    UIElement BuildHome()
    {
        var p = new DockPanel(); p.SetResourceReference(Panel.BackgroundProperty, "Paper");
        var navigation = new Grid { Margin = new Thickness(12, 8, 16, 8), MinHeight = 38 }; navigation.ColumnDefinitions.Add(new ColumnDefinition()); navigation.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(290) });
        var location = new Border { CornerRadius = new CornerRadius(5), Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 12, 0), Child = Ui.Row(Ui.Icon(Ui.HomeGlyph, 17), Ui.Text("Главная")) }; location.SetResourceReference(Border.BackgroundProperty, "Surface"); navigation.Children.Add(location);
        search.MinWidth = 0; search.SetBinding(TextBox.TextProperty, new Binding(nameof(HomeViewModel.SearchQuery)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        var searchHost = new Grid(); searchHost.Children.Add(search); var searchHint = Ui.Text("Поиск серверов", 14); searchHint.Margin = new Thickness(10, 0, 6, 0); searchHint.IsHitTestVisible = false; searchHint.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); searchHost.Children.Add(searchHint); search.TextChanged += (_, _) => searchHint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Grid.SetColumn(searchHost, 1); navigation.Children.Add(searchHost); DockPanel.SetDock(navigation, Dock.Top); p.Children.Add(navigation);
        var commands = Ui.Row(Ui.AsyncCommandButton(Ui.AddGlyph, "Добавить сервер", AddProfile), Ui.Line());
        foreach (var button in new[] { Ui.AsyncCommandButton("\uE768", "Подключиться", ConnectSelected), Ui.AsyncCommandButton(Ui.RenameGlyph, "Изменить", EditSelected), Ui.AsyncCommandButton(Ui.DeleteGlyph, "Удалить профиль", RemoveSelected) }) { button.SetBinding(IsEnabledProperty, new Binding(nameof(HomeViewModel.HasSelectedProfile))); commands.Children.Add(button); }
        commands.Margin = new Thickness(10, 4, 10, 5); var commandBorder = new Border { Child = commands, BorderThickness = new Thickness(0, 0, 0, 1) }; commandBorder.SetResourceReference(Border.BorderBrushProperty, "Edge"); DockPanel.SetDock(commandBorder, Dock.Top); p.Children.Add(commandBorder);
        homeStatus.Margin = new Thickness(16, 6, 16, 6); homeStatus.FontSize = 12; homeStatus.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); homeStatus.SetBinding(TextBlock.TextProperty, new Binding(nameof(HomeViewModel.StatusText))); DockPanel.SetDock(homeStatus, Dock.Bottom); p.Children.Add(homeStatus);
        servers.Margin = new Thickness(12, 4, 12, 0); servers.SelectionMode = DataGridSelectionMode.Single;
        servers.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(HomeViewModel.VisibleProfiles)));
        servers.SetBinding(DataGrid.SelectedItemProperty, new Binding(nameof(HomeViewModel.SelectedProfile)) { Mode = BindingMode.TwoWay });
        AddColumn(servers, "Название", "Name", 260); AddColumn(servers, "Адрес", "Endpoint", 280); AddColumn(servers, "Пользователь", "Username", 180); AddColumn(servers, "Состояние", "Status", 240);
        servers.MouseDoubleClick += async (_, _) => await Ui.Guard(ConnectSelected);
        var menu = new ContextMenu { DataContext = viewModel }; foreach (var item in new[] { Ui.AsyncMenu("Подключиться", ConnectSelected, "Enter"), Ui.AsyncMenu("Изменить профиль", EditSelected), Ui.AsyncMenu("Удалить профиль", RemoveSelected) }) { item.SetBinding(IsEnabledProperty, new Binding(nameof(HomeViewModel.HasSelectedProfile))); menu.Items.Add(item); } menu.Items.Add(new Separator()); menu.Items.Add(Ui.AsyncMenu("Добавить сервер…", AddProfile)); servers.ContextMenu = menu;
        servers.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await Ui.Guard(ConnectSelected); } };
        var body = new Grid(); body.Children.Add(servers); var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 390, Margin = new Thickness(24) }; var emptyTitle = Ui.Text("", 18, true); emptyTitle.HorizontalAlignment = HorizontalAlignment.Center; emptyTitle.SetBinding(TextBlock.TextProperty, new Binding(nameof(HomeViewModel.EmptyStateTitle))); empty.Children.Add(emptyTitle); var emptyText = Ui.Text(""); emptyText.TextWrapping = TextWrapping.Wrap; emptyText.TextAlignment = TextAlignment.Center; emptyText.Margin = new Thickness(0, 10, 0, 16); emptyText.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); emptyText.SetBinding(TextBlock.TextProperty, new Binding(nameof(HomeViewModel.EmptyStateMessage))); empty.Children.Add(emptyText); empty.SetBinding(VisibilityProperty, new Binding(nameof(HomeViewModel.ShowEmptyState)) { Converter = new BooleanToVisibilityConverter() }); body.Children.Add(empty); p.Children.Add(body); return p;
    }
    public static void AddColumn(DataGrid grid, string title, string binding, double width, string? format = null)
    {
        grid.Columns.Add(new DataGridTextColumn { Header = title, Binding = new Binding(binding) { StringFormat = format, TargetNullValue = "Недоступно" }, Width = width, SortMemberPath = binding });
    }
    void RefreshHome()
    {
        if (refreshingHome) return;
        refreshingHome = true;
        try { viewModel.Refresh(App.Store.Profiles, sessions.Values.Select(v => (v.Session.Profile.Id, v.Session.State))); }
        finally { refreshingHome = false; }
    }
    async Task AddProfile()
    {
        if (await Dialogs.ProfileAsync() is not { } saved) return; RefreshHome(); viewModel.SelectedProfile = viewModel.VisibleProfiles.FirstOrDefault(p => p.Profile.Id == saved.Id); if (viewModel.SelectedProfile is not null) servers.ScrollIntoView(viewModel.SelectedProfile);
    }
    async Task EditSelected()
    {
        if (viewModel.SelectedProfile is not { } row) return; if (await Dialogs.ProfileAsync(row.Profile) is not null) RefreshHome();
    }
    async Task RemoveSelected()
    {
        if (viewModel.SelectedProfile is not { } row || !Ui.Confirm($"Удалить локальный профиль «{row.Name}»?\n\nФайлы сервера и открытые сессии сохранятся.")) return;
        await App.Store.DeleteProfileAsync(row.Profile.Id); RefreshHome(); if (App.Store.LastProfileCleanupWarning is { } warning) Ui.Error(warning);
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
        var close = Ui.Button("", async () => await CloseTab(tab), "Закрыть сессию"); close.Content = Ui.Icon(Ui.CloseGlyph, 11, false); close.SetResourceReference(StyleProperty, "TabCloseButtonStyle"); WindowChrome.SetIsHitTestVisibleInChrome(close, true);
        var label = Ui.Text(title); label.TextTrimming = TextTrimming.CharacterEllipsis; label.MaxWidth = 155; var header = Ui.Row(Ui.Icon("\uE968", 16), label, close); header.ToolTip = tab.ToolTip; tab.ClearValue(ToolTipProperty); tab.Header = header; WindowChrome.SetIsHitTestVisibleInChrome(header, true);
        Point? start = null;
        header.PreviewMouseLeftButtonDown += (_, e) => start = e.GetPosition(header);
        header.PreviewMouseMove += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed && start is Point a && (e.GetPosition(header) - a).Length > 12 && !close.IsMouseOver) { start = null; DragDrop.DoDragDrop(header, TabDragData(tab), DragDropEffects.Move); } };
        header.AllowDrop = true;
        var hover = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) }; tabHoverTimers[tab] = hover; hover.Tick += (_, _) => { hover.Stop(); if (sessions.ContainsKey(tab)) tabs.SelectedItem = tab; };
        header.Drop += async (_, e) =>
        {
            hover.Stop();
            if (DraggedTab(e.Data) is TabItem source && source != home && source != tab) { var dest = tabs.Items.IndexOf(tab); tabs.Items.Remove(source); tabs.Items.Insert(dest, source); tabs.SelectedItem = source; e.Handled = true; }
            else if (IsFileDrag(e.Data) && sessions.GetValueOrDefault(tab) is { CanReceiveFileDrop: true } target) { tabs.SelectedItem = tab; e.Handled = true; await Ui.Guard(() => target.ReceiveTabDropAsync(e.Data, e.KeyStates)); }
        };
        header.DragEnter += (_, e) => { if (IsFileDrag(e.Data) && sessions.GetValueOrDefault(tab)?.CanReceiveFileDrop == true) hover.Start(); };
        header.DragLeave += (_, _) => hover.Stop();
        header.DragOver += (_, e) => { if (IsFileDrag(e.Data)) { e.Effects = sessions.GetValueOrDefault(tab)?.TabDropEffect(e.Data, e.KeyStates) ?? DragDropEffects.None; e.Handled = true; } };
        sessions[tab] = view; tabs.Items.Add(tab); tabs.SelectedItem = tab;
        session.StateChanged += (_, _) => Dispatcher.BeginInvoke(RefreshHome);
        await view.ConnectAsync(credentials, trustOverride); RefreshHome();
    }
    static bool IsFileDrag(IDataObject data) => data.GetDataPresent("SSHExplorer.RemoteFiles", false) || data.GetDataPresent(DataFormats.FileDrop);
    DataObject TabDragData(TabItem tab)
    {
        if (tabDragTokens.Count >= 64) tabDragTokens.Remove(tabDragTokens.Keys.First()); var token = Guid.NewGuid().ToString("N"); tabDragTokens[token] = new WeakReference<TabItem>(tab);
        var data = new DataObject(); data.SetData("SSHExplorer.Tab", new MemoryStream(Encoding.UTF8.GetBytes(token), false), false); return data;
    }
    TabItem? DraggedTab(IDataObject data)
    {
        if (!data.GetDataPresent("SSHExplorer.Tab", false) || data.GetData("SSHExplorer.Tab", false) is not MemoryStream { Length: 32 } stream) return null;
        return tabDragTokens.TryGetValue(Encoding.UTF8.GetString(stream.ToArray()), out var reference) && reference.TryGetTarget(out var tab) ? tab : null;
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
        App.Transfers.CancelSession(view.Session.Id); if (tabHoverTimers.Remove(tab, out var timer)) timer.Stop(); await view.DisposeAsync(); sessions.Remove(tab); tabs.Items.Remove(tab); RefreshHome();
    }
    async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closing) return; e.Cancel = true;
        if (sessions.Values.Any(v => App.Transfers.HasActive(v.Session.Id)) && !Ui.Confirm("Есть активные передачи. Прервать их и выйти?")) return;
        closing = true;
        foreach (var timer in tabHoverTimers.Values) timer.Stop(); tabHoverTimers.Clear(); tabDragTokens.Clear(); foreach (var view in sessions.Values.ToArray()) { App.Transfers.CancelSession(view.Session.Id); await view.DisposeAsync(); }
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
    readonly ContentControl addressHost = new(), commandHost = new(), fileStatusHost = new();
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
        host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); host.RowDefinitions.Add(new RowDefinition()); host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        host.Children.Add(addressHost); Grid.SetRow(commandHost, 1); host.Children.Add(commandHost);
        state.Margin = new Thickness(12, 5, 16, 5); state.FontSize = 12; state.MaxWidth = 430; state.TextTrimming = TextTrimming.CharacterEllipsis; state.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); state.SetBinding(TextBlock.TextProperty, new Binding(nameof(SessionViewModel.StatusText))); state.SetBinding(ToolTipProperty, new Binding(nameof(SessionViewModel.StatusText)));
        var statusRow = new DockPanel { LastChildFill = true, MinHeight = 30 }; DockPanel.SetDock(state, Dock.Right); statusRow.Children.Add(state); statusRow.Children.Add(fileStatusHost); var statusBorder = new Border { Child = statusRow, BorderThickness = new Thickness(0, 1, 0, 0) }; statusBorder.SetResourceReference(Border.BorderBrushProperty, "Edge"); Grid.SetRow(statusBorder, 3); host.Children.Add(statusBorder);
        var body = new Grid(); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Clamp(App.Store.Settings.TreeWidth, 190, 420)), MinWidth = 190, MaxWidth = 420 }); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) }); body.ColumnDefinitions.Add(new ColumnDefinition { MinWidth = 340 });
        side.Margin = new Thickness(5, 8, 5, 8); side.SetResourceReference(Panel.BackgroundProperty, "NavigationSurface");
        side.Children.Add(Nav("\uE756", "Терминал", "terminal")); side.Children.Add(Nav("\uE9D9", "Диспетчер задач", "monitor"));
        var line = new Border { Height = 1, Margin = new Thickness(8, 10, 8, 10) }; line.SetResourceReference(Border.BackgroundProperty, "Edge"); side.Children.Add(line);
        side.Children.Add(Nav("\uE8B7", "Файлы", "files"));
        reconnect = Ui.AsyncButton("Переподключиться", () => main.Reconnect(this, files?.CurrentPath)); reconnect.Visibility = Visibility.Collapsed; reconnect.Margin = new Thickness(7, 8, 7, 4); side.Children.Add(reconnect);
        reconnect.SetBinding(VisibilityProperty, new Binding(nameof(SessionViewModel.CanReconnect)) { Converter = new BooleanToVisibilityConverter() });
        var sidebar = new Border { Child = new ScrollViewer { Content = side, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } }; sidebar.SetResourceReference(Border.BackgroundProperty, "NavigationSurface"); body.Children.Add(sidebar);
        var preferredTreeWidth = App.Store.Settings.TreeWidth;
        var splitter = new GridSplitter { Width = 4, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch }; splitter.SetResourceReference(BackgroundProperty, "NavigationSurface"); Grid.SetColumn(splitter, 1); body.Children.Add(splitter); splitter.DragCompleted += (_, _) => { preferredTreeWidth = App.Store.Settings.TreeWidth = body.ColumnDefinitions[0].ActualWidth; _ = App.Store.SaveAsync(); };
        body.SizeChanged += (_, _) => { var width = Math.Clamp(preferredTreeWidth, 190, Math.Max(190, Math.Min(420, body.ActualWidth - 344))); if (Math.Abs(body.ColumnDefinitions[0].ActualWidth - width) > 1) body.ColumnDefinitions[0].Width = new GridLength(width); };
        Grid.SetColumn(screen, 2); body.Children.Add(screen); Grid.SetRow(body, 2); host.Children.Add(body); Content = host;
        Session.StateChanged += StateChanged;
    }
    Button Nav(string glyph, string text, string key)
    {
        var b = Ui.Button("", () => Show(key), text); b.Content = Ui.Row(Ui.Icon(glyph), Ui.Text(text)); b.SetResourceReference(StyleProperty, "NavigationButtonStyle"); navigationButtons[key] = b; return b;
    }
    void StateChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        viewModel.Update(Session);
        files?.SetAvailability(Session.SftpAvailable && Session.State == SessionState.Connected);
    });
    public async Task ConnectAsync(SessionCredentials credentials, Func<HostKeyPrompt, Task<TrustDecision>>? trustOverride = null)
    {
        try
        {
            await Session.ConnectAsync(credentials, trustOverride ?? (prompt => Dispatcher.InvokeAsync(() => Dialogs.Trust(prompt)).Task));
            files = new FileBrowserView(Session, main); files.FileNavigationRequested += () => Show("files"); terminal = new TerminalView(Session); monitor = new MonitorView(Session);
            addressHost.Content = files.AddressBar; commandHost.Content = files.CommandBar; fileStatusHost.Content = files.StatusBar; side.Children.Add(files.Navigation); Show("files");
            if (Session.SftpAvailable) await files.NavigateAsync(startPath ?? Session.Profile.InitialPath);
        }
        catch (Exception ex) { viewModel.StatusText = ErrorMessages.Describe(ex); screen.Content = new TextBlock { Text = viewModel.StatusText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(30), MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top }; }
    }
    void Show(string key)
    {
        section = key; monitor?.SetActive(false);
        addressHost.Visibility = commandHost.Visibility = fileStatusHost.Visibility = key == "files" ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (name, button) in navigationButtons) { button.SetResourceReference(BackgroundProperty, name == key ? "Selected" : "NavigationSurface"); button.SetResourceReference(BorderBrushProperty, "Accent"); button.BorderThickness = name == key ? new Thickness(2, 0, 0, 0) : new Thickness(0); }
        if (key == "terminal") { screen.Content = terminal; if (terminal is not null) _ = Ui.Guard(() => terminal.StartAsync()); }
        else if (key == "monitor") { screen.Content = monitor; monitor?.SetActive(active); }
        else screen.Content = files;
    }
    public void SetActive(bool value) { active = value; monitor?.SetActive(value && section == "monitor"); }
    public void RouteFileKey(KeyEventArgs e) { if (section == "files") files?.RouteGlobalKey(e); }
    public bool CanReceiveFileDrop => Session.SftpAvailable && Session.State == SessionState.Connected && !string.IsNullOrEmpty(files?.CurrentPath);
    internal DragDropEffects TabDropEffect(IDataObject data, DragDropKeyStates keys) => CanReceiveFileDrop ? files!.DragEffect(data, keys) : DragDropEffects.None;
    internal Task ReceiveTabDropAsync(IDataObject data, DragDropKeyStates keys) { if (!CanReceiveFileDrop) throw new InvalidOperationException("Файловая сессия не готова к приёму файлов."); Show("files"); return files!.ReceiveDropAsync(data, keys); }
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
