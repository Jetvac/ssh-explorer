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
    bool closing, refreshingHome, profileEditorOpen;
    readonly CancellationTokenSource windowLifetime = new();
    readonly ConnectionAttemptRegistry connectionAttempts;
    readonly HashSet<Guid> requireCredentials = [];
    Button? openServerButton;
    TransfersWindow? transferWindow;
    public MainWindow()
    {
        connectionAttempts = new(windowLifetime.Token);
        Style = (Style)FindResource(typeof(Window));
        // The glass strip belongs to DWM. An opaque WPF background over it
        // hides the real caption buttons even when native hit testing is enabled.
        Background = Brushes.Transparent;
        DataContext = viewModel;
        Title = "SSH Explorer " + BuildInfo.DisplayVersion; Width = 1320; Height = 850; MinWidth = 720; MinHeight = 500; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 44, GlassFrameThickness = new Thickness(0, 44, 0, 0), ResizeBorderThickness = SystemParameters.WindowResizeBorderThickness, CornerRadius = new CornerRadius(8), UseAeroCaptionButtons = true });
        var root = new Grid();
        openServerButton = Ui.IconButton(Ui.AddGlyph, AddMenu, "Открыть сервер");
        var actions = Ui.Row(openServerButton, Ui.IconButton("\uE8AB", ShowTransfers, "Очередь передач"), Ui.IconButton(Ui.SettingsGlyph, Dialogs.Settings, "Параметры"));
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
        servers.MouseDoubleClick += async (_, e) => { if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(servers, source) is DataGridRow { Item: ServerProfileViewModel }) await Ui.Guard(ConnectSelected); };
        var menu = new ContextMenu { DataContext = viewModel }; foreach (var item in new[] { Ui.AsyncMenu("Подключиться", ConnectSelected, "Enter", Ui.TerminalGlyph), Ui.AsyncMenu("Изменить профиль", EditSelected, glyph: Ui.RenameGlyph), Ui.AsyncMenu("Удалить профиль", RemoveSelected, glyph: Ui.DeleteGlyph) }) { item.SetBinding(IsEnabledProperty, new Binding(nameof(HomeViewModel.HasSelectedProfile))); menu.Items.Add(item); } menu.Items.Add(new Separator()); menu.Items.Add(Ui.AsyncMenu("Добавить сервер…", AddProfile, glyph: Ui.AddGlyph)); Ui.AttachContextMenu(servers, menu);
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
        if (profileEditorOpen || closing) return;
        profileEditorOpen = true;
        try
        {
            var saved = await Dialogs.ProfileSubmissionAsync(owner: this, ct: windowLifetime.Token);
            if (saved is null) return;
            RefreshHome(); viewModel.SelectedProfile = viewModel.VisibleProfiles.FirstOrDefault(p => p.Profile.Id == saved.Profile.Id);
            if (viewModel.SelectedProfile is not null) servers.ScrollIntoView(viewModel.SelectedProfile);
            if (saved.CleanupWarning is { } warning) Ui.Error(warning);
            if (saved.ConnectNow) await OpenServerCore(saved.Profile, null, saved.Credentials, null);
        }
        finally { profileEditorOpen = false; }
    }
    async Task EditSelected()
    {
        if (profileEditorOpen || closing || viewModel.SelectedProfile is not { } row) return;
        profileEditorOpen = true;
        try { if (await Dialogs.ProfileAsync(row.Profile) is not null) { RefreshHome(); if (App.Store.LastProfileCleanupWarning is { } warning) Ui.Error(warning); } }
        finally { profileEditorOpen = false; }
    }
    async Task RemoveSelected()
    {
        if (viewModel.SelectedProfile is not { } row || !Ui.Confirm($"Удалить локальный профиль «{row.Name}»?\n\nФайлы сервера и открытые сессии сохранятся.")) return;
        await App.Store.DeleteProfileAsync(row.Profile.Id); RefreshHome(); if (App.Store.LastProfileCleanupWarning is { } warning) Ui.Error(warning);
    }
    Task ConnectSelected() => viewModel.SelectedProfile is { } row ? OpenServer(row.Profile) : Task.CompletedTask;
    void AddMenu()
    {
        var menu = new ContextMenu();
        foreach (var profile in App.Store.Profiles.OrderBy(p => p.Name))
        {
            var item = new MenuItem { Header = profile.Name + "  ·  " + profile.Endpoint };
            item.Items.Add(Ui.AsyncMenu("Подключиться", () => OpenServer(profile), glyph: Ui.TerminalGlyph));
            item.Items.Add(Ui.AsyncMenu("Новая независимая сессия", () => OpenServerCore(profile, null, null, null, independent: true), glyph: Ui.NewTabGlyph));
            item.Items.Add(Ui.AsyncMenu("Ввести другой пароль…", () => OpenServerCore(profile, null, null, null, forceCredentials: true), glyph: Ui.SettingsGlyph));
            menu.Items.Add(item);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        menu.Items.Add(Ui.AsyncMenu("Добавить сервер…", AddProfile, glyph: Ui.AddGlyph));
        Ui.OpenFlyout(openServerButton!, menu);
    }
    // This overload is retained for explicit integration fixtures. The ordinary Add/Connect commands
    // below never inject a trust decision and always use the actual dialog coordinator.
    public Task OpenServer(ServerProfile profile, string? directory = null, SessionCredentials? suppliedCredentials = null, Func<HostKeyPrompt, Task<TrustDecision>>? trustOverride = null)
        => OpenServerCore(profile, directory, suppliedCredentials, trustOverride);
    async Task OpenServerCore(ServerProfile profile, string? directory, SessionCredentials? initialCredentials,
        Func<HostKeyPrompt, Task<TrustDecision>>? trustOverride, bool independent = false, bool forceCredentials = false,
        TabItem? replacement = null, SessionView? previous = null)
    {
        if (closing) return;
        using var attempt = connectionAttempts.TryBegin(profile.Id, independent);
        if (attempt is null) return;
        ServerSession? session = null; SessionView? view = null; ConnectionInteraction? interaction = null;
        string? saved = null; bool secretUnavailable = false;
        await attempt.RecordAsync("connect.command", "started");
        try
        {
            if (previous is not null && App.Transfers.HasActive(previous.Session.Id) && !Ui.Confirm("В этой сессии выполняются передачи. Прервать их и переподключить вкладку?")) return;
            var credentials = initialCredentials;
            if (credentials is null)
            {
                await attempt.RecordAsync("secret.read", "started");
                try { saved = profile.SaveSecret ? await App.Store.ReadSecretAsync(profile.Id, attempt.Token) : null; await attempt.RecordAsync("secret.read", "succeeded"); }
                catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException or FormatException)
                { await attempt.RecordAsync("secret.read", "failed", ex); forceCredentials = true; secretUnavailable = true; }
                if (saved is not null && !forceCredentials && !requireCredentials.Contains(profile.Id)) credentials = new SessionCredentials(saved);
                else
                {
                    await attempt.RecordAsync("credentials.dialog", "started");
                    credentials = Dialogs.Credentials(profile, null, this, attempt.Token, secretUnavailable ? "Защищённый секрет недоступен. Введите его заново; профиль сохранён." : null);
                    await attempt.RecordAsync("credentials.dialog", credentials is null ? "cancelled" : "succeeded");
                }
            }
            attempt.Token.ThrowIfCancellationRequested();
            if (credentials is null) { await attempt.RecordAsync("connect.command", "cancelled"); return; }
            await attempt.RecordAsync("credentials.accepted", "succeeded");
            session = new ServerSession(profile, App.Store);
            interaction = new ConnectionInteraction(this, session, attempt);
            credentials = credentials with { Prompt = null, CancellablePrompt = interaction.PromptAsync };
            EventHandler<ConnectionStageEvent> diagnostics = (_, stage) =>
            {
                if (!attempt.IsFinished) attempt.Observe(stage); else _ = LocalDiagnostics.RecordStageAsync(stage);
                if (stage.Stage == ConnectionStage.Authentication && stage.Outcome == ConnectionStageOutcome.Failed && !Dispatcher.HasShutdownStarted)
                    Dispatcher.BeginInvoke(() => requireCredentials.Add(profile.Id));
            };
            session.StageChanged += diagnostics;
            object? context = previous?.CaptureContext();
            if (previous is not null) { App.Transfers.CancelSession(previous.Session.Id); await previous.DisposeAsync(); }
            await attempt.RecordAsync("view.construct", "started");
            view = new SessionView(session, this, directory); view.AttachConnection(interaction, diagnostics, attempt);
            await attempt.RecordAsync("view.construct", "succeeded");
            if (replacement is null) AddSessionTab(profile, session, view);
            else { replacement.Content = view; sessions[replacement] = view; tabs.SelectedItem = replacement; }
            session.StateChanged += (_, _) => { if (!closing && !Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(RefreshHome); };
            attempt.BeginNetworkTimeout(profile.TimeoutSeconds);
            bool ready = await view.ConnectAsync(credentials, interaction, attempt, trustOverride);
            if (ready)
            {
                attempt.Token.ThrowIfCancellationRequested();
                requireCredentials.Remove(profile.Id); view.RestoreContext(context);
                if (profile.SaveSecret && (initialCredentials is not null || saved is null || saved != credentials.Secret))
                {
                    await attempt.RecordAsync("secret.commit", "started");
                    try { bool committed = await App.Store.SaveAuthenticatedSecretAsync(profile, credentials.Secret, attempt.Token); await attempt.RecordAsync("secret.commit", committed ? "succeeded" : "stale"); }
                    catch (Exception ex) { await attempt.RecordAsync("secret.commit", "failed", ex); view.SetNotice("Подключение установлено, но пароль не сохранён. Проверьте доступ к папке настроек."); }
                }
                await attempt.RecordAsync("connect.command", "succeeded");
            }
            else await attempt.RecordAsync("connect.command", attempt.Token.IsCancellationRequested ? "cancelled" : "failed");
        }
        catch (Exception ex)
        {
            await attempt.RecordAsync("connect.command", ex is OperationCanceledException ? "cancelled" : "failed", ex);
            if (session is not null) await session.DisposeAsync();
            interaction?.Dispose();
            if (!closing) { if (view is not null) view.SetConnectionFailure(ex, attempt.TimedOut); else Ui.Error(ErrorMessages.Describe(ex)); }
        }
        finally
        {
            interaction?.InitialFinished(); await attempt.FlushAsync(); RefreshHome();
        }
    }
    void AddSessionTab(ServerProfile profile, ServerSession session, SessionView view)
    {
        var tab = new TabItem { Content = view, ToolTip = profile.Username + "@" + profile.Endpoint };
        var number = sessionNumbers.GetValueOrDefault(profile.Id) + 1; sessionNumbers[profile.Id] = number; var title = profile.Name + (number > 1 ? $" ({number})" : "");
        var close = Ui.Button("", async () => await CloseTab(tab), "Закрыть сессию"); close.Content = Ui.Icon(Ui.CloseGlyph, 11, false); close.SetResourceReference(StyleProperty, "TabCloseButtonStyle"); WindowChrome.SetIsHitTestVisibleInChrome(close, true);
        var label = Ui.Text(title); label.TextTrimming = TextTrimming.CharacterEllipsis; label.MaxWidth = 155; var header = Ui.Row(Ui.Icon("\uE968", 16), label, close); header.ToolTip = tab.ToolTip; tab.ClearValue(ToolTipProperty); tab.Header = header; WindowChrome.SetIsHitTestVisibleInChrome(header, true);
        Point? start = null;
        header.PreviewMouseLeftButtonDown += (_, e) => start = e.GetPosition(header);
        header.PreviewMouseMove += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed && start is Point a && (e.GetPosition(header) - a).Length > 12 && !close.IsMouseOver) { start = null; try { DragDrop.DoDragDrop(header, TabDragData(tab), DragDropEffects.Move); } finally { CancelDragFeedback(); } } };
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
        var pair = sessions.FirstOrDefault(x => ReferenceEquals(x.Value, previous));
        if (pair.Key is null) return;
        await OpenServerCore(previous.Session.Profile, path, null, null, replacement: pair.Key, previous: previous);
    }
    async Task CloseTab(TabItem tab)
    {
        if (!sessions.TryGetValue(tab, out var view)) return;
        if (App.Transfers.HasActive(view.Session.Id) && !Ui.Confirm("В этой сессии выполняется передача. Прервать передачу и закрыть сессию?")) return;
        CancelDragFeedback(); App.Transfers.CancelSession(view.Session.Id); if (tabHoverTimers.Remove(tab, out var timer)) timer.Stop(); await view.DisposeAsync(); sessions.Remove(tab); tabs.Items.Remove(tab); RefreshHome();
    }
    async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closing) return; e.Cancel = true;
        if (sessions.Values.Any(v => App.Transfers.HasActive(v.Session.Id)) && !Ui.Confirm("Есть активные передачи. Прервать их и выйти?")) return;
        closing = true; connectionAttempts.CancelAll(); windowLifetime.Cancel();
        await LocalDiagnostics.RecordAsync("app.close.requested");
        CancelDragFeedback(); tabHoverTimers.Clear(); tabDragTokens.Clear();
        foreach (var view in sessions.Values.ToArray())
        {
            App.Transfers.CancelSession(view.Session.Id);
            try { await view.DisposeAsync(); } catch (Exception ex) { await LocalDiagnostics.RecordAsync("app.close.session.failed", ex); }
        }
        try { await App.Store.SaveAsync(); }
        catch (Exception ex) { await LocalDiagnostics.RecordAsync("app.close.settings.failed", ex); Ui.Error("Не удалось сохранить последние настройки окна. " + ErrorMessages.Describe(ex)); }
        finally { await LocalDiagnostics.RecordAsync("app.close.completed"); transferWindow?.Close(); Close(); }
    }
    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) CancelDragFeedback();
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.Tab) { e.Handled = true; var delta = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1; tabs.SelectedIndex = (tabs.SelectedIndex + delta + tabs.Items.Count) % tabs.Items.Count; }
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.F4 && tabs.SelectedItem is TabItem tab && tab != home) { e.Handled = true; _ = Ui.Guard(() => CloseTab(tab)); }
        if (!e.Handled && tabs.SelectedItem is TabItem current && sessions.TryGetValue(current, out var session)) session.RouteFileKey(e);
    }
    internal void CancelDragFeedback()
    {
        foreach (var timer in tabHoverTimers.Values) timer.Stop();
        foreach (var view in sessions.Values) view.CancelDragFeedback();
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
    readonly StackPanel side = new(); readonly ContentControl screen = new() { Focusable = false, IsTabStop = false };
    readonly ContentControl addressHost = new() { Focusable = false, IsTabStop = false }, commandHost = new() { Focusable = false, IsTabStop = false }, fileStatusHost = new() { Focusable = false, IsTabStop = false };
    readonly Dictionary<string, Button> navigationButtons = [];
    readonly string? startPath;
    readonly SessionViewModel viewModel = new();
    FileBrowserView? files; TerminalView? terminal; MonitorView? monitor; readonly Button reconnect;
    ConnectionInteraction? interaction; EventHandler<ConnectionStageEvent>? diagnostics; ConnectionAttempt? connecting;
    bool disposed, failed, initialFolderFailed;
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
    internal void AttachConnection(ConnectionInteraction value, EventHandler<ConnectionStageEvent> handler, ConnectionAttempt attempt) { interaction = value; diagnostics = handler; connecting = attempt; }
    void StateChanged(object? sender, EventArgs e)
    {
        if (disposed || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (disposed || failed) return;
            viewModel.Update(Session); files?.SetAvailability(Session.SftpAvailable && Session.State == SessionState.Connected);
        });
    }
    internal async Task<bool> ConnectAsync(SessionCredentials credentials, ConnectionInteraction prompts, ConnectionAttempt attempt, Func<HostKeyPrompt, Task<TrustDecision>>? trustOverride = null)
    {
        string stage = "connection.transport";
        try
        {
            await Session.ConnectAsync(credentials, (key, ct) => trustOverride is null ? prompts.TrustAsync(key, ct) : trustOverride(key).WaitAsync(ct), attempt.Token, attempt.Id);
            attempt.Token.ThrowIfCancellationRequested();
            navigationButtons["terminal"].IsEnabled = Session.ShellAvailable;
            navigationButtons["monitor"].IsEnabled = Session.ShellAvailable;
            navigationButtons["files"].IsEnabled = Session.SftpAvailable;
            if (Session.SftpAvailable)
            {
                stage = "view.files"; await attempt.RecordAsync(stage, "started");
                attempt.Token.ThrowIfCancellationRequested();
                files = new FileBrowserView(Session, main); files.FileNavigationRequested += () => Show("files");
                files.NavigationCommitted += () => { if (initialFolderFailed && !disposed) { initialFolderFailed = false; viewModel.Update(Session); } };
                addressHost.Content = files.AddressBar; commandHost.Content = files.CommandBar; fileStatusHost.Content = files.StatusBar; side.Children.Add(files.Navigation);
                await attempt.RecordAsync(stage, "succeeded"); Show("files");
                stage = "navigation.initial"; await attempt.RecordAsync(stage, "started");
                await files.InitializeAsync(startPath ?? Session.Profile.InitialPath, attempt.Token);
                await attempt.RecordAsync(stage, "succeeded");
            }
            else
            {
                screen.Content = new TextBlock { Text = "Файловая подсистема недоступна. Можно открыть терминал или диспетчер задач; переподключение доступно после закрытия этой сессии.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24) };
                SetNotice("Вход выполнен; SFTP недоступен. " + Session.SftpError);
            }
            viewModel.Update(Session);
            if (!Session.SftpAvailable) SetNotice("Вход выполнен; SFTP недоступен. " + Session.SftpError);
            return true;
        }
        catch (Exception ex)
        {
            await attempt.RecordAsync(stage, ex is OperationCanceledException ? "cancelled" : "failed", ex);
            if (stage == "navigation.initial" && ex is not OperationCanceledException && !disposed)
            {
                // The authenticated SFTP connection is still usable. Keep explicit navigation recovery.
                initialFolderFailed = true;
                SetNotice("Вход выполнен, начальная папка недоступна. Выберите домашнюю папку или корень. " + ErrorMessages.Describe(ex));
                return false;
            }
            prompts.Dispose();
            files?.Dispose(); files = null; terminal?.Dispose(); terminal = null; monitor?.Dispose(); monitor = null;
            await Session.DisposeAsync();
            if (!disposed) SetConnectionFailure(ex, attempt.TimedOut);
            return false;
        }
    }
    internal void SetNotice(string text) { if (!disposed) viewModel.StatusText = text; }
    internal void SetConnectionFailure(Exception error, bool timeout)
    {
        if (disposed) return;
        failed = true; viewModel.Update(Session);
        viewModel.StatusText = timeout ? "Время подключения истекло. Повторите попытку." : error is OperationCanceledException ? "Подключение отменено. Можно повторить попытку." : ErrorMessages.Describe(error);
        addressHost.Content = commandHost.Content = fileStatusHost.Content = null;
        screen.Content = new TextBlock { Text = viewModel.StatusText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24), MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    }
    void Show(string key)
    {
        if (disposed) return;
        if (section == key && screen.Content is not null &&
            (key == "files" && ReferenceEquals(screen.Content, files) || key == "monitor" && ReferenceEquals(screen.Content, monitor) || key == "terminal" && ReferenceEquals(screen.Content, terminal))) return;
        section = key; monitor?.SetActive(false);
        addressHost.Visibility = commandHost.Visibility = fileStatusHost.Visibility = key == "files" ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (name, button) in navigationButtons) { button.SetResourceReference(BackgroundProperty, name == key ? "Selected" : "NavigationSurface"); button.SetResourceReference(BorderBrushProperty, "Accent"); button.BorderThickness = name == key ? new Thickness(2, 0, 0, 0) : new Thickness(0); }
        if (key == "terminal") { _ = OpenTerminalAsync(); return; }
        if (key == "monitor")
        {
            if (!Session.ShellAvailable) return;
            try { monitor ??= new MonitorView(Session); screen.Content = monitor; monitor.SetActive(active); }
            catch (Exception ex) { _ = LocalDiagnostics.RecordAsync("monitor.construct.failed", ex); SetNotice(ErrorMessages.Describe(ex)); screen.Content = new TextBlock { Text = "Диспетчер задач не удалось открыть. Нажмите раздел для повторной попытки.", Margin = new Thickness(24), TextWrapping = TextWrapping.Wrap }; }
        }
        else screen.Content = files;
        UiMotion.SurfaceChanged(screen);
    }
    async Task OpenTerminalAsync()
    {
        if (!Session.ShellAvailable || disposed) return;
        var operation = Guid.NewGuid();
        try
        {
            await LocalDiagnostics.RecordOperationAsync(operation, "terminal.initialize", "started");
            if (disposed || section != "terminal") throw new OperationCanceledException();
            terminal ??= new TerminalView(Session); screen.Content = terminal;
            // WebView2 is an HWND host: do not animate/reposition it or delay its input.
            UiMotion.Stop(screen); screen.Opacity = 1; screen.RenderTransform = Transform.Identity;
            await terminal.StartAsync();
            await LocalDiagnostics.RecordOperationAsync(operation, "terminal.initialize", "succeeded");
        }
        catch (Exception ex)
        {
            await LocalDiagnostics.RecordOperationAsync(operation, "terminal.initialize", ex is OperationCanceledException ? "cancelled" : "failed", ex);
            if (ex is not OperationCanceledException) { terminal?.Dispose(); terminal = null; }
            if (!disposed && section == "terminal")
            {
                SetNotice(ErrorMessages.Describe(ex));
                screen.Content = new TextBlock { Text = "Терминал не удалось открыть. Нажмите раздел для повторной попытки.", Margin = new Thickness(24), TextWrapping = TextWrapping.Wrap };
            }
        }
    }
    public void SetActive(bool value) { active = value; monitor?.SetActive(value && section == "monitor"); }
    public void RouteFileKey(KeyEventArgs e) { if (section == "files") files?.RouteGlobalKey(e); }
    public bool CanReceiveFileDrop => Session.SftpAvailable && Session.State == SessionState.Connected && !string.IsNullOrEmpty(files?.CurrentPath);
    internal DragDropEffects TabDropEffect(IDataObject data, DragDropKeyStates keys) => CanReceiveFileDrop ? files!.DragEffect(data, keys) : DragDropEffects.None;
    internal Task ReceiveTabDropAsync(IDataObject data, DragDropKeyStates keys) { if (!CanReceiveFileDrop) throw new InvalidOperationException("Файловая сессия не готова к приёму файлов."); Show("files"); return files!.ReceiveDropAsync(data, keys); }
    public object? CaptureContext() => files?.CaptureContext();
    public void RestoreContext(object? context) => files?.RestoreContext(context);
    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true; connecting?.Cancel(); interaction?.Dispose(); UiMotion.Stop(screen);
        Session.StateChanged -= StateChanged;
        monitor?.Dispose(); files?.Dispose(); terminal?.Dispose();
        try { await Session.DisposeAsync(); }
        finally { if (diagnostics is not null) Session.StageChanged -= diagnostics; }
    }
    internal void CancelDragFeedback() => files?.CancelDragFeedback();
}

public sealed class TransfersWindow : Window
{
    readonly DataGrid grid = new();
    readonly TransferListViewModel transfers = new();
    readonly System.Windows.Threading.DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    public TransfersWindow()
    {
        Style = (Style)FindResource(typeof(Window));
        Title = "Передачи — SSH Explorer"; Width = 1050; Height = 440; MinWidth = 700; MinHeight = 300; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var p = new DockPanel { Margin = new Thickness(18) }; var buttons = Ui.Row(Ui.Button("Отменить", () => (grid.SelectedItem as TransferRow)?.Job.Cancel()), Ui.AsyncButton("Повторить", async () => { if (grid.SelectedItem is TransferRow row) await row.Job.RetryAsync(); })); DockPanel.SetDock(buttons, Dock.Bottom); p.Children.Add(buttons);
        MainWindow.AddColumn(grid, "Название", "Name", 220); MainWindow.AddColumn(grid, "Состояние", "State", 150); MainWindow.AddColumn(grid, "Передано", "Transferred", 120); MainWindow.AddColumn(grid, "Всего", "Total", 120); MainWindow.AddColumn(grid, "Прогресс", "Progress", 100); MainWindow.AddColumn(grid, "Скорость", "Speed", 120); MainWindow.AddColumn(grid, "Осталось", "Remaining", 120); MainWindow.AddColumn(grid, "Источник", "Source", 280); MainWindow.AddColumn(grid, "Назначение", "Destination", 280); MainWindow.AddColumn(grid, "Результат / ошибка", "Result", 380); p.Children.Add(grid); Content = p;
        grid.ItemsSource = transfers.Rows;
        timer.Tick += (_, _) => transfers.Refresh(App.Transfers.Jobs); transfers.Refresh(App.Transfers.Jobs); timer.Start(); Closed += (_, _) => timer.Stop();
    }
}
