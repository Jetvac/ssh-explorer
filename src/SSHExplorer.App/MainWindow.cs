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
    readonly TabControl tabs = new() { Padding = new Thickness(8, 0, 0, 0), Tag = "WindowTabs" };
    WindowCaption caption = null!;
    internal WindowCaption Caption => caption;
    readonly TabItem home = new();
    readonly DataGrid servers = new();
    readonly TextBox search = new() { MinWidth = 300, ToolTip = "Поиск сохранённых серверов" };
    readonly Dictionary<TabItem, SessionView> sessions = [];
    readonly Dictionary<Guid, int> sessionNumbers = [];
    readonly TextBlock homeStatus = Ui.Text("");
    readonly HomeViewModel viewModel = new();
    readonly Dictionary<string, WeakReference<TabItem>> tabDragTokens = [];
    readonly Dictionary<TabItem, DispatcherTimer> tabHoverTimers = [];
    readonly HashSet<TabItem> closingTabs = [];
    bool closing, refreshingHome, profileEditorOpen, tabRevealScheduled, preparingToClose;
    readonly CancellationTokenSource windowLifetime = new();
    readonly ConnectionAttemptRegistry connectionAttempts;
    readonly HashSet<Guid> requireCredentials = [];
    Button? openServerButton;
    Button? allTabsButton;
    TransfersWindow? transferWindow;
    public MainWindow()
    {
        connectionAttempts = new(windowLifetime.Token);
        Style = (Style)FindResource(typeof(Window));
        // The caption row (tabs, actions, caption buttons) is one WPF surface; see WindowCaption.
        SetResourceReference(BackgroundProperty, "CaptionSurface");
        DataContext = viewModel;
        tabs.SetResourceReference(StyleProperty, "SessionTabControlStyle");
        home.SetResourceReference(StyleProperty, "SessionTabItemStyle");
        Title = "SSH Explorer " + BuildInfo.DisplayVersion; Width = 1320; Height = 850; MinWidth = 720; MinHeight = 500; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        AppIcon.Apply(this);
        WindowCaption.ApplyChrome(this);
        var root = new Grid();
        openServerButton = Ui.IconButton(Ui.AddGlyph, AddMenu, "Открыть сервер");
        allTabsButton = Ui.FlyoutButton(IconCatalog.TabList, "", CreateTabListMenu, "Все вкладки (Ctrl+Shift+A)"); allTabsButton.Name = "AllTabsButton";
        var actions = Ui.Row(allTabsButton, Ui.IconButton(IconCatalog.Documents, ShowDocuments, "Редактируемые документы"), Ui.IconButton(IconCatalog.Transfers, ShowTransfers, "Очередь передач"), Ui.IconButton(IconCatalog.Settings, Dialogs.Settings, "Параметры"));
        actions.Name = "CaptionActions"; actions.VerticalAlignment = VerticalAlignment.Top; actions.Margin = new Thickness(8, 4, 8, 0); WindowChrome.SetIsHitTestVisibleInChrome(actions, true);
        caption = new WindowCaption(this);
        var trailing = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top }; trailing.Children.Add(actions); trailing.Children.Add(caption);
        SessionTabInfo.SetTrailingContent(tabs, trailing);
        root.Children.Add(tabs); Content = root;
        home.Header = Ui.Row(Ui.Icon("\uE80F", 16), Ui.Text("Главная")); SessionTabInfo.SetTitle(home, "Главная"); home.Content = BuildHome(); tabs.Items.Add(home); tabs.SelectedItem = home;
        tabs.SelectionChanged += (_, e) => { if (!ReferenceEquals(e.OriginalSource, tabs)) return; foreach (var (tab, view) in sessions) view.SetActive(tabs.SelectedItem == tab); RevealSelectedTab(); RefreshHome(); };
        tabs.SizeChanged+=(_,_)=>RevealSelectedTab();
        tabs.Loaded += (_, _) =>
        {
            tabs.ApplyTemplate(); if (tabs.Template.FindName("PART_NewTab", tabs) is Button add) { openServerButton = add; add.Click += (_, _) => AddMenu(); }
            // Adding or closing tabs changes the extent after selection already scheduled a reveal.
            if (tabs.Template.FindName("HeaderScroller", tabs) is ScrollViewer headerScroller) headerScroller.ScrollChanged += (_, e) => { if (Math.Abs(e.ExtentWidthChange) > .5 || Math.Abs(e.ViewportWidthChange) > .5) RevealSelectedTab(); };
        };
        tabs.PreviewDragOver += (_, e) => { if (DraggedTab(e.Data) is not null) { e.Effects = DragDropEffects.Move; e.Handled = true; } };
        tabs.AllowDrop = true;
        PreviewKeyDown += OnKey;
        Closing += OnClosing;
        RefreshHome();
        Loaded += (_, _) => { if (System.IO.Directory.Exists(System.IO.Path.Combine(App.Store.DirectoryPath, "RemoteEdits")) && EditWorkspace.Documents.Any(d => d.Record.State != RemoteEditState.Completed)) ShowDocuments(); };
        Activated += (_, _) => { if (editWorkspace is not null) _ = editWorkspace.ReconcileAsync(); };
    }
    UIElement BuildHome()
    {
        var p = new DockPanel(); p.SetResourceReference(Panel.BackgroundProperty, "Paper");
        var navigation = new Grid { Margin = new Thickness(12, 8, 16, 8), MinHeight = 38 }; navigation.ColumnDefinitions.Add(new ColumnDefinition()); navigation.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(290) });
        var location = new Border { CornerRadius = new CornerRadius(5), Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 12, 0), Child = Ui.Row(Ui.Icon(Ui.HomeGlyph, 17), Ui.Text("Главная")) }; location.SetResourceReference(Border.BackgroundProperty, "Surface"); navigation.Children.Add(location);
        search.MinWidth = 0; search.SetBinding(TextBox.TextProperty, new Binding(nameof(HomeViewModel.SearchQuery)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        var searchHost = new Grid(); searchHost.Children.Add(search); var searchHint = Ui.Text("Поиск серверов", 14); searchHint.Margin = new Thickness(10, 0, 6, 0); searchHint.IsHitTestVisible = false; searchHint.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); searchHost.Children.Add(searchHint); search.TextChanged += (_, _) => searchHint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Grid.SetColumn(searchHost, 1); navigation.Children.Add(searchHost); DockPanel.SetDock(navigation, Dock.Top); p.Children.Add(navigation);
        var commands = new ResponsiveCommandBar { Name = "HomeCommandBar" };
        commands.Add("add", Ui.AsyncCommandButton(Ui.AddGlyph, "Добавить сервер", AddProfile), () => Ui.AsyncMenu("Добавить сервер", AddProfile, glyph: Ui.AddGlyph), 100);
        foreach (var (key,glyph,label,action,priority) in new (string,string,string,Func<Task>,int)[] { ("connect","\uE768","Подключиться",ConnectSelected,80), ("edit",Ui.RenameGlyph,"Изменить",EditSelected,50), ("remove",Ui.DeleteGlyph,"Удалить профиль",RemoveSelected,10) })
        {
            var button = Ui.AsyncCommandButton(glyph,label,action); button.SetBinding(IsEnabledProperty,new Binding(nameof(HomeViewModel.HasSelectedProfile)));
            commands.Add(key,button,()=>Ui.AsyncMenu(label,action,glyph:glyph),priority);
        }
        commands.Margin = new Thickness(10, 4, 10, 5); var commandBorder = new Border { Child = commands, BorderThickness = new Thickness(0, 0, 0, 1) }; commandBorder.SetResourceReference(StyleProperty, "CommandBarStyle"); DockPanel.SetDock(commandBorder, Dock.Top); p.Children.Add(commandBorder);
        homeStatus.Margin = new Thickness(16, 6, 16, 6); homeStatus.FontSize = 12; homeStatus.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); homeStatus.SetBinding(TextBlock.TextProperty, new Binding(nameof(HomeViewModel.StatusText))); var homeFooter = new Border { Child = homeStatus }; homeFooter.SetResourceReference(StyleProperty, "StatusBarStyle"); homeStatus.Margin = new Thickness(0); DockPanel.SetDock(homeFooter, Dock.Bottom); p.Children.Add(homeFooter);
        servers.SetResourceReference(StyleProperty, "HomeTableStyle");
        servers.Margin = new Thickness(12, 4, 12, 0); servers.SelectionMode = DataGridSelectionMode.Single;
        servers.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(HomeViewModel.VisibleProfiles)));
        servers.SetBinding(DataGrid.SelectedItemProperty, new Binding(nameof(HomeViewModel.SelectedProfile)) { Mode = BindingMode.TwoWay });
        AddColumn(servers, "Название", "Name", 260); AddColumn(servers, "Адрес", "Endpoint", 280); AddColumn(servers, "Пользователь", "Username", 180); AddColumn(servers, "Состояние", "Status", 240); servers.Columns[^1].MinWidth = 240; servers.Columns[^1].Width = new DataGridLength(1, DataGridLengthUnitType.Star);
        TableSorting.Attach(servers, "home.servers", [new("Name", r => ((ServerProfileViewModel)r).Name, true), new("Endpoint", r => ((ServerProfileViewModel)r).Endpoint, true), new("Username", r => ((ServerProfileViewModel)r).Username, true), new("Status", r => ((ServerProfileViewModel)r).StatusRank)], "Name");
        servers.MouseDoubleClick += async (_, e) => { if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(servers, source) is DataGridRow { Item: ServerProfileViewModel }) await Ui.Guard(ConnectSelected); };
        var menu = new ContextMenu { DataContext = viewModel }; foreach (var item in new[] { Ui.AsyncMenu("Подключиться", ConnectSelected, "Enter", Ui.TerminalGlyph), Ui.AsyncMenu("Изменить профиль", EditSelected, glyph: Ui.RenameGlyph), Ui.AsyncMenu("Удалить профиль", RemoveSelected, glyph: Ui.DeleteGlyph) }) { item.SetBinding(IsEnabledProperty, new Binding(nameof(HomeViewModel.HasSelectedProfile))); menu.Items.Add(item); } menu.Items.Add(new Separator()); menu.Items.Add(Ui.AsyncMenu("Добавить сервер…", AddProfile, glyph: Ui.AddGlyph)); Ui.AttachContextMenu(servers, menu);
        servers.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await Ui.Guard(ConnectSelected); } };
        var body = new Grid(); body.Children.Add(servers); var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 390, Margin = new Thickness(24) }; var emptyTitle = Ui.Text("", 18, true); emptyTitle.HorizontalAlignment = HorizontalAlignment.Center; emptyTitle.SetBinding(TextBlock.TextProperty, new Binding(nameof(HomeViewModel.EmptyStateTitle))); empty.Children.Add(emptyTitle); var emptyText = Ui.Text(""); emptyText.TextWrapping = TextWrapping.Wrap; emptyText.TextAlignment = TextAlignment.Center; emptyText.Margin = new Thickness(0, 10, 0, 16); emptyText.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); emptyText.SetBinding(TextBlock.TextProperty, new Binding(nameof(HomeViewModel.EmptyStateMessage))); empty.Children.Add(emptyText); empty.SetBinding(VisibilityProperty, new Binding(nameof(HomeViewModel.ShowEmptyState)) { Converter = new BooleanToVisibilityConverter() }); body.Children.Add(empty); p.Children.Add(body); return p;
    }
    public static void AddColumn(DataGrid grid, string title, string binding, double width, string? format = null)
    {
        var textStyle = new Style(typeof(TextBlock));
        textStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        textStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
        textStyle.Setters.Add(new Setter(ToolTipProperty, new Binding(binding) { StringFormat = format }));
        grid.Columns.Add(new DataGridTextColumn { Header = title, Binding = new Binding(binding) { StringFormat = format, TargetNullValue = "—" }, Width = width, SortMemberPath = binding, ElementStyle = textStyle });
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
    void AddMenu() => Ui.OpenFlyout(openServerButton!, BuildAddMenu());
    ContextMenu BuildAddMenu()
    {
        var menu = new ContextMenu();
        foreach (var profile in App.Store.Profiles.OrderBy(p => p.Name))
        {
            var item = new MenuItem { Header = profile.Name + "  ·  " + profile.Endpoint, Icon = Ui.Icon(IconCatalog.Server, 16, false) };
            item.Items.Add(Ui.AsyncMenu("Подключиться", () => OpenServer(profile), glyph: Ui.TerminalGlyph));
            item.Items.Add(Ui.AsyncMenu("Новая независимая сессия", () => OpenServerCore(profile, null, null, null, independent: true), glyph: Ui.NewTabGlyph));
            item.Items.Add(Ui.AsyncMenu("Ввести другой пароль…", () => OpenServerCore(profile, null, null, null, forceCredentials: true), glyph: Ui.SettingsGlyph));
            menu.Items.Add(item);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        menu.Items.Add(Ui.AsyncMenu("Добавить сервер…", AddProfile, glyph: Ui.AddGlyph));
        return menu;
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
        var tab = new TabItem { Content = view };
        tab.SetResourceReference(StyleProperty, "SessionTabItemStyle");
        var number = sessionNumbers.GetValueOrDefault(profile.Id) + 1; sessionNumbers[profile.Id] = number; var title = profile.Name + (number > 1 ? $" ({number})" : "");
        SessionTabInfo.SetTitle(tab,title); SessionTabInfo.SetDescription(tab,profile.Username+"@"+profile.Endpoint);
        System.Windows.Automation.AutomationProperties.SetName(tab,title); System.Windows.Automation.AutomationProperties.SetHelpText(tab,SessionTabInfo.GetDescription(tab));
        var close = Ui.Button("", async () => await CloseTab(tab), "Закрыть «"+title+"» (Ctrl+F4)"); close.Name="SessionTabCloseButton"; close.Content = Ui.Icon(Ui.CloseGlyph, 11, false); close.SetResourceReference(StyleProperty, "TabCloseButtonStyle"); WindowChrome.SetIsHitTestVisibleInChrome(close, true);
        var label = Ui.Text(title); label.TextTrimming = TextTrimming.CharacterEllipsis; label.MinWidth=0;
        var header = new Grid { ToolTip=title+"\n"+SessionTabInfo.GetDescription(tab) }; header.ColumnDefinitions.Add(new(){Width=GridLength.Auto}); header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        header.Children.Add(Ui.Icon(IconCatalog.Server,16)); Grid.SetColumn(label,1); header.Children.Add(label); Grid.SetColumn(close,2); header.Children.Add(close); tab.Header=header; WindowChrome.SetIsHitTestVisibleInChrome(header,true);
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
        header.DragEnter += (_, e) => { if (DraggedTab(e.Data) is null && IsFileDrag(e.Data) && sessions.GetValueOrDefault(tab)?.CanReceiveFileDrop == true) hover.Start(); };
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
        return tabDragTokens.TryGetValue(Encoding.UTF8.GetString(stream.ToArray()), out var reference) && reference.TryGetTarget(out var tab) && tabs.Items.Contains(tab) ? tab : null;
    }
    public async Task Reconnect(SessionView previous, string? path)
    {
        var pair = sessions.FirstOrDefault(x => ReferenceEquals(x.Value, previous));
        if (pair.Key is null) return;
        await OpenServerCore(previous.Session.Profile, path, null, null, replacement: pair.Key, previous: previous);
    }
    internal ContextMenu CreateTabListMenu()
    {
        var menu=new ContextMenu();
        foreach(var tab in tabs.Items.OfType<TabItem>())
        {
            var title=SessionTabInfo.GetTitle(tab); var description=SessionTabInfo.GetDescription(tab);
            var item=Ui.Menu(title,()=>{tabs.SelectedItem=tab;tab.Focus();},glyph:tab==home?Ui.HomeGlyph:Ui.NewTabGlyph);
            item.IsCheckable=true;item.IsChecked=ReferenceEquals(tabs.SelectedItem,tab);item.ToolTip=title+(description.Length>0?"\n"+description:"");
            var text=Ui.Text(title);text.MaxWidth=400;text.TextWrapping=TextWrapping.Wrap;
            if(description.Length>0){var stack=new StackPanel();stack.Children.Add(text);var endpoint=Ui.Text(description,12);endpoint.MaxWidth=400;endpoint.TextWrapping=TextWrapping.Wrap;endpoint.SetResourceReference(TextBlock.ForegroundProperty,"Muted");stack.Children.Add(endpoint);item.Header=stack;}else item.Header=text;
            System.Windows.Automation.AutomationProperties.SetName(item,title);System.Windows.Automation.AutomationProperties.SetHelpText(item,description);menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var close=Ui.AsyncMenu("Закрыть текущую вкладку",()=>tabs.SelectedItem is TabItem tab?CloseTab(tab):Task.CompletedTask,"Ctrl+F4",Ui.CloseGlyph);close.IsEnabled=tabs.SelectedItem!=home;menu.Items.Add(close);
        return menu;
    }
    void RevealSelectedTab()
    {
        if(tabRevealScheduled||closing)return;tabRevealScheduled=true;
        // Add/resize can select a header before its first measure. Use the next
        // layout turn, not a timer or a second selection, to reveal its close target.
        // Reveal the whole TabItem (its chrome and padding, not only the header content) after a
        // settled layout pass, so the scroller already knows the extent of a just-added tab.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded,()=>{tabRevealScheduled=false;if(closing||tabs.SelectedItem is not TabItem tab)return;tabs.UpdateLayout();tab.BringIntoView();});
    }
    internal Task CloseSessionTabAsync(TabItem tab)=>CloseTab(tab);
    async Task CloseTab(TabItem tab)
    {
        if (!sessions.TryGetValue(tab, out var view) || !closingTabs.Add(tab)) return;
        try
        {
            if (App.Transfers.HasActive(view.Session.Id) && !Ui.Confirm("В этой сессии выполняется передача. Прервать передачу и закрыть сессию?")) return;
            if (!await PrepareDocumentsForCloseAsync(view.Session.Id)) return;
            CancelDragFeedback(); App.Transfers.CancelSession(view.Session.Id); if (tabHoverTimers.Remove(tab, out var timer)) timer.Stop(); await view.DisposeAsync();
            // Choose the right neighbour, then the left. Closing a background tab never changes the active one.
            var index=tabs.Items.IndexOf(tab); var next=ReferenceEquals(tabs.SelectedItem,tab)?tabs.Items.OfType<TabItem>().ElementAtOrDefault(index+1)??tabs.Items.OfType<TabItem>().ElementAtOrDefault(Math.Max(0,index-1)):null;
            sessions.Remove(tab); if(next is not null){tabs.SelectedItem=next;next.Focus();} tabs.Items.Remove(tab); RefreshHome();
        }
        finally { closingTabs.Remove(tab); }
    }
    async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closing) return; e.Cancel = true;
        if (preparingToClose) return; preparingToClose = true;
        try
        {
        if (sessions.Values.Any(v => App.Transfers.HasActive(v.Session.Id)) && !Ui.Confirm("Есть активные передачи. Прервать их и выйти?")) return;
        foreach (var view in sessions.Values.ToArray()) if (!await PrepareDocumentsForCloseAsync(view.Session.Id)) return;
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
        finally { if (editWorkspace is not null) await editWorkspace.DisposeAsync(); await LocalDiagnostics.RecordAsync("app.close.completed"); documentsWindow?.Close(); transferWindow?.Close(); Close(); }
        }
        catch (Exception ex) { Ui.Error("Не удалось завершить отключение: " + ErrorMessages.Describe(ex)); }
        finally { preparingToClose = false; }
    }
    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) CancelDragFeedback();
        if(TryHandleTabShortcut(e.Key,Keyboard.Modifiers,e.OriginalSource as DependencyObject,Keyboard.FocusedElement as DependencyObject)){e.Handled=true;return;}
        if (!e.Handled && tabs.SelectedItem is TabItem current && sessions.TryGetValue(current, out var session)) session.RouteFileKey(e);
    }
    internal bool TryHandleTabShortcut(Key key,ModifierKeys modifiers,DependencyObject? source,DependencyObject? focused)
    {
        // Editors and the terminal own their keystrokes. OriginalSource also
        // covers routed input when WPF has no current keyboard focus element.
        if(IsInputEditing(source)||IsInputEditing(focused))return false;
        if (modifiers == (ModifierKeys.Control|ModifierKeys.Shift) && key==Key.A) { Ui.OpenFlyout(allTabsButton!,CreateTabListMenu());return true; }
        if ((modifiers & ModifierKeys.Control) != 0 && key == Key.Tab) { var delta = (modifiers & ModifierKeys.Shift) != 0 ? -1 : 1; tabs.SelectedIndex = (tabs.SelectedIndex + delta + tabs.Items.Count) % tabs.Items.Count;return true; }
        if ((modifiers & ModifierKeys.Control) != 0 && key == Key.F4 && tabs.SelectedItem is TabItem tab && tab != home) { _ = Ui.Guard(() => CloseTab(tab));return true; }
        return false;
    }
    static bool IsInputEditing(DependencyObject? element)
    {
        for(var current=element;current is not null;current=current is Visual?VisualTreeHelper.GetParent(current):LogicalTreeHelper.GetParent(current))
            if(current is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox or TerminalView)return true;
        return false;
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
    readonly StackPanel side = new(), monitorNavigation = new();
    readonly ContentControl fileNavigationHost = new() { Focusable = false, IsTabStop = false, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    MonitorPage monitorPage = MonitorPage.Processes;
    readonly ContentControl screen = new() { Focusable = false, IsTabStop = false, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    readonly ContentControl addressHost = new() { Focusable = false, IsTabStop = false, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch }, commandHost = new() { Focusable = false, IsTabStop = false, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch }, fileStatusHost = new() { Focusable = false, IsTabStop = false, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    readonly Border connectionStatus = new();
    readonly bool presentationOnly;
    readonly Dictionary<string, Button> navigationButtons = [];
    readonly string? startPath;
    readonly SessionViewModel viewModel = new();
    FileBrowserView? files; TerminalView? terminal; MonitorView? monitor; ServerPanelView? serverPanel; readonly Button reconnect;
    ConnectionInteraction? interaction; EventHandler<ConnectionStageEvent>? diagnostics; ConnectionAttempt? connecting;
    bool disposed, failed, initialFolderFailed, hasNotice;
    bool active { get => viewModel.IsActive; set => viewModel.IsActive = value; }
    string section { get => viewModel.CurrentSection; set => viewModel.CurrentSection = value; }
    public SessionView(ServerSession session, MainWindow main, string? startPath)
    {
        Session = session; this.main = main; this.startPath = startPath;
        DataContext = viewModel;
        host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); host.RowDefinitions.Add(new RowDefinition()); host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        host.Children.Add(addressHost); Grid.SetRow(commandHost, 1); host.Children.Add(commandHost);
        state.Margin = new Thickness(12, 5, 16, 5); state.FontSize = 12; state.MaxWidth = 430; state.TextTrimming = TextTrimming.CharacterEllipsis; state.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); state.SetBinding(TextBlock.TextProperty, new Binding(nameof(SessionViewModel.StatusText))); state.SetBinding(ToolTipProperty, new Binding(nameof(SessionViewModel.StatusText)));
        var statusRow = new DockPanel { LastChildFill = true, MinHeight = 30 }; DockPanel.SetDock(state, Dock.Right); statusRow.Children.Add(state); statusRow.Children.Add(fileStatusHost); connectionStatus.Child = statusRow; connectionStatus.Padding = new Thickness(0); connectionStatus.SetResourceReference(StyleProperty, "StatusBarStyle"); Grid.SetRow(connectionStatus, 3); host.Children.Add(connectionStatus);
        var body = new Grid(); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Clamp(App.Store.Settings.TreeWidth, 190, 420)), MinWidth = 190, MaxWidth = 420 }); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) }); body.ColumnDefinitions.Add(new ColumnDefinition { MinWidth = 340 });
        side.Margin = new Thickness(5, 8, 5, 8); side.SetResourceReference(Panel.BackgroundProperty, "NavigationSurface");
        side.Children.Add(Nav(IconCatalog.Server, "Сервер", "server")); side.Children.Add(Nav(IconCatalog.Terminal, "Терминал", "terminal")); side.Children.Add(Nav(IconCatalog.Performance, "Диспетчер задач", "monitor"));
        monitorNavigation.Children.Add(Nav(IconCatalog.Processes, "Процессы", "processes"));
        monitorNavigation.Children.Add(Nav(IconCatalog.Performance, "Производительность", "performance"));
        monitorNavigation.Visibility = Visibility.Collapsed; side.Children.Add(monitorNavigation);
        var line = new Border { Height = 1, Margin = new Thickness(8, 10, 8, 10) }; line.SetResourceReference(Border.BackgroundProperty, "Edge"); side.Children.Add(line);
        side.Children.Add(Nav("\uE8B7", "Файлы", "files"));
        reconnect = Ui.AsyncButton("Переподключиться", () => main.Reconnect(this, files?.CurrentPath)); reconnect.Visibility = Visibility.Collapsed; reconnect.Margin = new Thickness(7, 8, 7, 4); side.Children.Add(reconnect);
        reconnect.SetBinding(VisibilityProperty, new Binding(nameof(SessionViewModel.CanReconnect)) { Converter = new BooleanToVisibilityConverter() });
        var sidebarLayout = new Grid(); sidebarLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); sidebarLayout.RowDefinitions.Add(new RowDefinition());
        sidebarLayout.Children.Add(side); Grid.SetRow(fileNavigationHost, 1); fileNavigationHost.Margin = new Thickness(5, 0, 5, 8); sidebarLayout.Children.Add(fileNavigationHost);
        var sidebar = new Border { Child = sidebarLayout, BorderThickness = new Thickness(0, 0, 1, 0) }; sidebar.SetResourceReference(Border.BackgroundProperty, "NavigationSurface"); sidebar.SetResourceReference(Border.BorderBrushProperty, "Edge"); body.Children.Add(sidebar);
        var preferredTreeWidth = App.Store.Settings.TreeWidth;
        var splitter = new GridSplitter { Width = 4, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch }; splitter.SetResourceReference(BackgroundProperty, "NavigationSurface"); Grid.SetColumn(splitter, 1); body.Children.Add(splitter); splitter.DragCompleted += (_, _) => { preferredTreeWidth = App.Store.Settings.TreeWidth = body.ColumnDefinitions[0].ActualWidth; _ = App.Store.SaveAsync(); };
        body.SizeChanged += (_, _) => { var width = Math.Clamp(preferredTreeWidth, 190, Math.Max(190, Math.Min(420, body.ActualWidth - 344))); if (Math.Abs(body.ColumnDefinitions[0].ActualWidth - width) > 1) body.ColumnDefinitions[0].Width = new GridLength(width); };
        Grid.SetColumn(screen, 2); body.Children.Add(screen); Grid.SetRow(body, 2); host.Children.Add(body); Content = host;
        Session.StateChanged += StateChanged;
    }
    Button Nav(string glyph, string text, string key)
    {
        var b = Ui.Button("", () => ShowSection(key), text); var label = Ui.Text(text); label.ClearValue(TextBlock.FontWeightProperty); label.TextTrimming = TextTrimming.CharacterEllipsis;
        var content = new DockPanel { LastChildFill = true }; var icon = Ui.Icon(glyph); DockPanel.SetDock(icon, Dock.Left); content.Children.Add(icon); content.Children.Add(label); b.Content = content;
        b.SetResourceReference(StyleProperty, key is "processes" or "performance" ? "SubNavigationButtonStyle" : "NavigationButtonStyle"); navigationButtons[key] = b; return b;
    }
    // Presentation-only fixture: it uses the production layout without opening SSH or polling.
    internal SessionView(ServerSession session, MainWindow main, FileBrowserView fileView, MonitorView monitorView) : this(session, main, null)
    {
        presentationOnly = true; files = fileView; monitor = monitorView; AttachFilePresentation(); Show("files");
    }
    void AttachFilePresentation()
    {
        if (files is null) return;
        files.FileNavigationRequested += () => Show("files");
        files.NavigationCommitted += () => { if (initialFolderFailed && !disposed) { initialFolderFailed = false; hasNotice = false; viewModel.Update(Session); UpdateConnectionStatusVisibility(); } };
        addressHost.Content = files.AddressBar; commandHost.Content = files.CommandBar; fileStatusHost.Content = files.StatusBar; fileNavigationHost.Content = files.Navigation;
    }
    /// <summary>Work that a reboot/power-off of this server would interrupt.</summary>
    (int Transfers, int Documents) PendingWork()
    {
        int transfers = App.Transfers.Jobs.Count(job => job.SessionId == Session.Id && job.CanCancel);
        int documents = main.EditWorkspace.Documents.Count(d => d.Record.Identity.ProfileId == Session.Profile.Id && d.Record.State is RemoteEditState.Pending or RemoteEditState.Uploading or RemoteEditState.Conflict or RemoteEditState.Error or RemoteEditState.Paused or RemoteEditState.Unknown or RemoteEditState.FormatConfirmation);
        return (transfers, documents);
    }
    internal ServerPanelView? ServerPanel => serverPanel;
    internal void ShowSection(string key)
    {
        if (key is "processes" or "performance") { monitorPage = key == "processes" ? MonitorPage.Processes : MonitorPage.Performance; key = "monitor"; }
        Show(key);
    }
    internal void AttachConnection(ConnectionInteraction value, EventHandler<ConnectionStageEvent> handler, ConnectionAttempt attempt) { interaction = value; diagnostics = handler; connecting = attempt; }
    void StateChanged(object? sender, EventArgs e)
    {
        if (disposed || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (disposed || failed) return;
            viewModel.Update(Session); files?.SetAvailability(Session.SftpAvailable && Session.State == SessionState.Connected); UpdateConnectionStatusVisibility();
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
            navigationButtons["monitor"].IsEnabled = navigationButtons["processes"].IsEnabled = navigationButtons["performance"].IsEnabled = Session.ShellAvailable;
            navigationButtons["files"].IsEnabled = Session.SftpAvailable;
            if (Session.SftpAvailable)
            {
                stage = "view.files"; await attempt.RecordAsync(stage, "started");
                attempt.Token.ThrowIfCancellationRequested();
                files = new FileBrowserView(Session, main); AttachFilePresentation();
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
            if (Session.ShellAvailable && SSHExplorer.Monitoring.PowerVerificationRegistry.IsPending(Session.Profile.Id, Session.Profile.Endpoint)) Show("server");
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
    internal void SetNotice(string text) { if (!disposed) { hasNotice = !string.IsNullOrWhiteSpace(text); viewModel.StatusText = text; UpdateConnectionStatusVisibility(); } }
    internal void SetConnectionFailure(Exception error, bool timeout)
    {
        if (disposed) return;
        failed = true; hasNotice = true; connectionStatus.Visibility = Visibility.Visible; viewModel.Update(Session);
        viewModel.StatusText = timeout ? "Время подключения истекло. Повторите попытку." : error is OperationCanceledException ? "Подключение отменено. Можно повторить попытку." : ErrorMessages.Describe(error);
        addressHost.Content = commandHost.Content = fileStatusHost.Content = null;
        screen.Content = new TextBlock { Text = viewModel.StatusText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24), MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    }
    void Show(string key)
    {
        if (disposed) return;
        if (section == key && key != "monitor" && screen.Content is not null &&
            (key == "files" && ReferenceEquals(screen.Content, files) || key == "monitor" && ReferenceEquals(screen.Content, monitor) || key == "terminal" && ReferenceEquals(screen.Content, terminal) || key == "server" && serverPanel is not null && ReferenceEquals(screen.Content, serverPanel))) return;
        var previousContent = screen.Content; var previousPage = monitor?.Page;
        section = key;
        UpdateConnectionStatusVisibility();
        if (key != "monitor") monitor?.SetActive(false);
        monitorNavigation.Visibility = key == "monitor" ? Visibility.Visible : Visibility.Collapsed;
        if (files is not null) Ui.SetIsFileSectionActive(files.Navigation, key == "files");
        addressHost.Visibility = commandHost.Visibility = fileStatusHost.Visibility = key == "files" ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (name, button) in navigationButtons)
        {
            Ui.SetIsNavigationActive(button, key == "monitor" ? name == (monitorPage == MonitorPage.Processes ? "processes" : "performance") : name == key);
            button.FontWeight = name == "monitor" && key == "monitor" ? FontWeights.SemiBold : FontWeights.Normal;
        }
        if (key == "terminal") { _ = OpenTerminalAsync(); return; }
        if (key == "server")
        {
            serverPanel ??= new ServerPanelView(Session, presentationOnly ? null : () => main.Reconnect(this, files?.CurrentPath), Session.ShellAvailable ? () => ShowSection("terminal") : null, PendingWork);
            screen.Content = serverPanel;
            if (!ReferenceEquals(previousContent, screen.Content)) UiMotion.SurfaceChanged(screen);
            return;
        }
        if (key == "monitor")
        {
            if (monitor is null && !Session.ShellAvailable) return;
            try { monitor ??= new MonitorView(Session); monitor.SetPage(monitorPage); screen.Content = monitor; monitor.SetActive(active); }
            catch (Exception ex) { _ = LocalDiagnostics.RecordAsync("monitor.construct.failed", ex); SetNotice(ErrorMessages.Describe(ex)); screen.Content = new TextBlock { Text = "Диспетчер задач не удалось открыть. Нажмите раздел для повторной попытки.", Margin = new Thickness(24), TextWrapping = TextWrapping.Wrap }; }
        }
        else screen.Content = files;
        if (!ReferenceEquals(previousContent, screen.Content) || key == "monitor" && previousPage != monitorPage) UiMotion.SurfaceChanged(screen);
    }
    void UpdateConnectionStatusVisibility() => connectionStatus.Visibility = hasNotice || section != "monitor" || !presentationOnly && Session.State != SessionState.Connected ? Visibility.Visible : Visibility.Collapsed;
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
