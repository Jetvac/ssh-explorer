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
    /// <summary>Remote desktop tabs; their native controls live on <see cref="desktopLayer"/> over the whole window.</summary>
    readonly Dictionary<TabItem, RdpView> desktops = [];
    readonly Canvas desktopLayer = new() { Name = "DesktopLayer" };
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
        root.Children.Add(tabs); root.Children.Add(desktopLayer); Content = root;
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
        EventHandler profilesChanged = (_, _) => Dispatcher.InvokeAsync(RefreshHome); App.ProfilesChanged += profilesChanged; Closed += (_, _) => App.ProfilesChanged -= profilesChanged;
        // Once a day: a newer release is announced on Home; nothing is downloaded.
        Loaded += async (_, _) => { try { if (await Updates.CheckAsync(false, windowLifetime.Token) is { } newer) viewModel.Announce($"Доступна версия {newer.Version} — «Параметры» → «О приложении»."); } catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or OperationCanceledException) { } };
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
        var import = Ui.FlyoutButton(ImportGlyph, "Импорт", ImportMenu, "Импорт серверов из других программ"); import.Name = "HomeImport";
        commands.Add("import", import, () => { var item = new MenuItem { Header = "Импорт", Icon = Ui.Icon(ImportGlyph, 16, false) }; foreach (var entry in ImportEntries()) item.Items.Add(entry); return item; }, 20);
        var export = Ui.AsyncCommandButton(ExportGlyph, "Экспорт", () => ExportProfilesAsync()); export.Name = "HomeExport"; export.ToolTip = "Сохранить серверы в файл для другого компьютера";
        commands.Add("export", export, () => Ui.AsyncMenu("Экспорт…", () => ExportProfilesAsync(), glyph: ExportGlyph), 15);
        alertsButton = Ui.FlyoutButton(IconCatalog.Performance, "Оповещения", AlertsMenu, "Заполненные диски, упавшие службы и ошибки скриптов на подключённых серверах"); alertsButton.Name = "HomeAlerts";
        commands.Add("alerts", alertsButton, () => Ui.Menu(AlertCenter.ActiveCount > 0 ? $"Оповещения ({AlertCenter.ActiveCount})…" : "Оповещения…", ShowAlerts), 25);
        AlertCenter.Changed += (_, _) => Dispatcher.InvokeAsync(UpdateAlertsButton); UpdateAlertsButton();
        // Load column: recomputed in the background after new samples (at most once a minute), then the list refreshes.
        Action<Guid> metricsUpdated = _ => HomeMetrics.Refresh(); HomeMetrics.Changed += RefreshHome; MetricsHub.Updated += metricsUpdated; HomeMetrics.Refresh(true);
        Closed += (_, _) => { HomeMetrics.Changed -= RefreshHome; MetricsHub.Updated -= metricsUpdated; };
        var group = Ui.AsyncCommandButton(IconCatalog.Terminal, "Выполнить на нескольких…", () => RunOnGroupAsync()); group.Name = "HomeGroupRun"; group.ToolTip = "Выполнить команды или скрипт сразу на нескольких серверах";
        commands.Add("group", group, () => Ui.AsyncMenu("Выполнить на нескольких…", () => RunOnGroupAsync(), glyph: IconCatalog.Terminal), 30);
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
        servers.Columns.Add(ProtocolColumn()); AddColumn(servers, "Название", "Name", 240); AddColumn(servers, "Адрес", "Endpoint", 280); AddColumn(servers, "Пользователь", "Username", 180); servers.Columns.Add(loadColumn = LoadColumn()); loadColumn.Visibility = HomeMetrics.Any ? Visibility.Visible : Visibility.Collapsed; AddColumn(servers, "Состояние", "Status", 240); servers.Columns[^1].MinWidth = 240; servers.Columns[^1].Width = new DataGridLength(1, DataGridLengthUnitType.Star);
        TableSorting.Attach(servers, "home.servers", [new("Name", r => ((ServerProfileViewModel)r).Name, true), new("Endpoint", r => ((ServerProfileViewModel)r).Endpoint, true), new("Username", r => ((ServerProfileViewModel)r).Username, true), new("Status", r => ((ServerProfileViewModel)r).StatusRank), new("Load", r => ((ServerProfileViewModel)r).LoadRank)], "Name",
            group: new("Group", r => ((ServerProfileViewModel)r).GroupKey, true));
        HomeGrouping.Attach(servers, () => viewModel.HasGroups);
        servers.MouseDoubleClick += async (_, e) => { if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(servers, source) is DataGridRow { Item: ServerProfileViewModel }) await Ui.Guard(ConnectSelected); };
        var menu = new ContextMenu { DataContext = viewModel }; foreach (var item in new[] { Ui.AsyncMenu("Подключиться", ConnectSelected, "Enter", Ui.TerminalGlyph), Ui.AsyncMenu("Открыть по RDP", () => viewModel.SelectedProfile is { } chosen ? OpenDesktopAsync(chosen.Profile, asDesktop: true) : Task.CompletedTask, glyph: IconCatalog.RemoteDesktop), Ui.Menu("История…", () => ShowHistory(viewModel.SelectedProfile?.Profile), glyph: IconCatalog.History), Ui.AsyncMenu("Изменить профиль", EditSelected, glyph: Ui.RenameGlyph), Ui.AsyncMenu("Удалить профиль", RemoveSelected, glyph: Ui.DeleteGlyph), Ui.AsyncMenu("Выполнить на группе…", () => RunOnGroupAsync(viewModel.SelectedProfile?.Profile), glyph: IconCatalog.Terminal), Ui.AsyncMenu("Параметры папки…", () => EditFolderAsync(viewModel.SelectedProfile?.Profile.Group ?? ""), glyph: Ui.SettingsGlyph), Ui.AsyncMenu("Экспортировать…", () => ExportProfilesAsync(viewModel.SelectedProfile is { } chosen ? new[] { chosen.Profile.Id } : Array.Empty<Guid>()), glyph: ExportGlyph) }) { item.SetBinding(IsEnabledProperty, new Binding(nameof(HomeViewModel.HasSelectedProfile))); menu.Items.Add(item); } menu.Items.Add(new Separator()); menu.Items.Add(Ui.AsyncMenu("Добавить сервер…", AddProfile, glyph: Ui.AddGlyph)); Ui.AttachContextMenu(servers, menu);
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
        if (loadColumn is not null) loadColumn.Visibility = HomeMetrics.Any ? Visibility.Visible : Visibility.Collapsed;
        try { viewModel.Refresh(App.Store.Profiles, sessions.Values.Select(v => (v.Session.Profile.Id, v.Session.State)).Concat(desktops.Values.Select(d => (d.Profile.Id, d.State)))); }
        finally { refreshingHome = false; }
    }
    const string ImportGlyph = "";
    ContextMenu ImportMenu()
    {
        var menu = new ContextMenu(); foreach (var entry in ImportEntries()) menu.Items.Add(entry); return menu;
    }
    IEnumerable<MenuItem> ImportEntries() =>
    [
        Ui.AsyncMenu("Из файла SSH Explorer (.sshx)…", () => ImportExchangeAsync(), glyph: ImportGlyph),
        Ui.AsyncMenu("Из файла OpenSSH (~/.ssh/config)…", ImportSshConfig, glyph: Ui.FileGlyph),
        Ui.AsyncMenu("Из PuTTY", ImportPutty, glyph: IconCatalog.Terminal),
        Ui.AsyncMenu("Из WinSCP…", ImportWinScp, glyph: IconCatalog.Transfers),
        Ui.AsyncMenu("Из mRemoteNG (confCons.xml)…", ImportMRemoteNgAsync, glyph: IconCatalog.Server),
        Ui.AsyncMenu("Из файлов удалённого рабочего стола (.rdp)…", ImportRdpFilesAsync, glyph: IconCatalog.RemoteDesktop),
        Ui.AsyncMenu("Из истории «Подключения к удалённому рабочему столу»", ImportRdpHistoryAsync, glyph: IconCatalog.RemoteDesktop),
    ];
    /// <summary>Reads saved sessions under a HKCU key (read-only); empty when the program never saved any.</summary>
    internal static IReadOnlyList<ExternalSession> RegistrySessions(string path)
    {
        using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path, writable: false);
        if (root is null) return [];
        var result = new List<ExternalSession>();
        foreach (var name in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(name, writable: false); if (key is null) continue;
            result.Add(new ExternalSession(name, key.GetValueNames().ToDictionary(v => v, v => key.GetValue(v), StringComparer.OrdinalIgnoreCase)));
        }
        return result;
    }
    async Task ImportPutty()
    {
        if (profileEditorOpen || closing) return;
        var sessions = RegistrySessions(@"Software\SimonTatham\PuTTY\Sessions");
        if (sessions.Count == 0) { Ui.Info(@"Сохранённых сессий PuTTY не найдено (HKCU\Software\SimonTatham\PuTTY\Sessions)."); return; }
        var (candidates, skipped) = ExternalSessionImport.FromPutty(sessions, Environment.UserName, App.Store.Profiles);
        await ImportProfilesAsync("PuTTY · сохранённые сессии", candidates, skipped);
    }
    async Task ImportWinScp()
    {
        if (profileEditorOpen || closing) return;
        var sessions = RegistrySessions(@"Software\Martin Prikryl\WinSCP 2\Sessions"); var source = "WinSCP · реестр";
        if (sessions.Count == 0)
        {
            var ini = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinSCP.ini");
            if (!System.IO.File.Exists(ini))
            {
                var pick = new Microsoft.Win32.OpenFileDialog { Title = "Файл настроек WinSCP", Filter = "WinSCP.ini|WinSCP.ini;*.ini|Все файлы|*.*" };
                if (pick.ShowDialog(this) != true) return;
                ini = pick.FileName;
            }
            sessions = ExternalSessionImport.ParseWinScpIni(await System.IO.File.ReadAllTextAsync(ini, windowLifetime.Token)); source = ini;
        }
        var (candidates, skipped) = ExternalSessionImport.FromWinScp(sessions, Environment.UserName, App.Store.Profiles);
        await ImportProfilesAsync(source, candidates, skipped);
    }
    internal Func<string?>? MRemoteNgPathOverride;
    /// <summary>Test hook: answers the import preview of any import that does not pass its own.</summary>
    internal Func<ImportProfilesWindow, bool?>? ImportPreviewOverride;
    async Task ImportSshConfig()
    {
        if (profileEditorOpen || closing) return;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); var sshDirectory = System.IO.Path.Combine(home, ".ssh");
        var path = System.IO.Path.Combine(sshDirectory, "config");
        if (!System.IO.File.Exists(path))
        {
            var pick = new Microsoft.Win32.OpenFileDialog { Title = "Файл конфигурации OpenSSH", InitialDirectory = System.IO.Directory.Exists(sshDirectory) ? sshDirectory : home, Filter = "Конфигурация OpenSSH|config;*.conf;*|Все файлы|*.*" };
            if (pick.ShowDialog(this) != true) return;
            path = pick.FileName; sshDirectory = System.IO.Path.GetDirectoryName(path)!;
        }
        var text = await System.IO.File.ReadAllTextAsync(path, windowLifetime.Token);
        var config = SshConfig.Parse(text, sshDirectory, System.IO.Path.GetFileName(path));
        var candidates = ProfileImport.FromSshConfig(config, Environment.UserName, home, App.Store.Profiles);
        await ImportProfilesAsync(path, candidates, config.Warnings);
    }
    internal async Task<int> ImportProfilesAsync(string source, IReadOnlyList<ImportCandidate> candidates, IReadOnlyList<string> warnings, Func<ImportProfilesWindow, bool?>? show = null,
        IReadOnlyDictionary<Guid, string>? secrets = null, IReadOnlyList<FolderDefaults>? folders = null, IReadOnlyDictionary<Guid, string>? folderSecrets = null)
    {
        profileEditorOpen = true;
        try
        {
            var window = new ImportProfilesWindow(source, candidates, warnings) { Owner = this };
            if (((show ?? ImportPreviewOverride)?.Invoke(window) ?? ModalRunner.Show(window, this, windowLifetime.Token)) != true) return 0;
            ServerProfile? last = null;
            foreach (var profile in window.Accepted) { var secret = secrets?.GetValueOrDefault(profile.Id); if (secret is null) profile.SaveSecret = false; await App.Store.SaveProfileAsync(profile, secret, windowLifetime.Token); last = profile; }
            if (folders is { Count: > 0 }) await SaveImportedFoldersAsync(folders, folderSecrets ?? new Dictionary<Guid, string>(), window.Accepted);
            RefreshHome();
            if (last is not null) { viewModel.SelectedProfile = viewModel.VisibleProfiles.FirstOrDefault(p => p.Profile.Id == last.Id); if (viewModel.SelectedProfile is not null) servers.ScrollIntoView(viewModel.SelectedProfile); }
            return window.Accepted.Count;
        }
        finally { profileEditorOpen = false; }
    }
    const string ExportGlyph = "";
    /// <summary>Test hooks: the file to write or read instead of a file dialog, and the export/import windows' answers.</summary>
    internal Func<string?>? ExportPathOverride, ImportPathOverride;
    internal Func<ExportWindow, bool?>? ExportWindowOverride;
    internal Func<ExchangeImportWindow, bool?>? ImportWindowOverride;
    internal async Task<int> ExportProfilesAsync(IReadOnlyCollection<Guid>? preselected = null)
    {
        if (profileEditorOpen || closing) return 0;
        if (App.Store.Profiles.Count == 0) { Ui.Info("Сохранённых серверов нет — экспортировать нечего."); return 0; }
        profileEditorOpen = true;
        try
        {
            var window = new ExportWindow(App.Store.Profiles, preselected ?? [], App.Store.Settings.FirewallRuleSets.Count) { Owner = this };
            if ((ExportWindowOverride?.Invoke(window) ?? ModalRunner.Show(window, this, windowLifetime.Token)) != true) return 0;
            string? path = ExportPathOverride?.Invoke();
            if (path is null)
            {
                var pick = new Microsoft.Win32.SaveFileDialog { Title = "Экспорт серверов", Filter = "Файл SSH Explorer (*.sshx)|*.sshx", DefaultExt = ProfileExchange.Extension, FileName = $"servers-{DateTime.Now:yyyy-MM-dd}{ProfileExchange.Extension}", AddExtension = true, OverwritePrompt = true };
                if (pick.ShowDialog(this) != true) return 0;
                path = pick.FileName;
            }
            var selected = window.Selected; var options = window.Chosen;
            var doc = await ProfileExchange.BuildAsync(selected, options, BuildInfo.DisplayVersion, id => App.Store.ReadSecretAsync(id, windowLifetime.Token), App.Store.Settings, App.Store.Trust.Records);
            await System.IO.File.WriteAllTextAsync(path, ProfileExchange.Serialize(doc), new UTF8Encoding(false), windowLifetime.Token);
            int secrets = doc.Servers.Count(x => x.Secret is not null);
            viewModel.Announce($"Экспортировано серверов: {doc.Servers.Count}" + (options.IncludeSecrets ? $", паролей: {secrets}" : ", без паролей") + " — " + System.IO.Path.GetFileName(path));
            return doc.Servers.Count;
        }
        finally { profileEditorOpen = false; }
    }
    internal async Task<int> ImportExchangeAsync()
    {
        if (profileEditorOpen || closing) return 0;
        string? path = ImportPathOverride?.Invoke();
        if (path is null)
        {
            var pick = new Microsoft.Win32.OpenFileDialog { Title = "Импорт из файла SSH Explorer", Filter = "Файл SSH Explorer (*.sshx)|*.sshx|Все файлы|*.*", CheckFileExists = true };
            if (pick.ShowDialog(this) != true) return 0;
            path = pick.FileName;
        }
        ExportDocument doc;
        try { doc = ProfileExchange.Parse(await System.IO.File.ReadAllTextAsync(path, windowLifetime.Token)); }
        catch (InvalidDataException ex) { Ui.Error(ex.Message); return 0; }
        profileEditorOpen = true;
        try
        {
            var window = new ExchangeImportWindow(path, doc, App.Store.Profiles) { Owner = this };
            if ((ImportWindowOverride?.Invoke(window) ?? ModalRunner.Show(window, this, windowLifetime.Token)) != true) return 0;
            int imported = 0; ServerProfile? last = null; var problems = new List<string>();
            foreach (var row in window.Rows.Where(r => r.Imported).ToArray())
            {
                var profile = ProfileExchange.Resolve(row.Candidate, row.Conflict, App.Store.Profiles);
                try
                {
                    try { await App.Store.SaveProfileAsync(profile, row.Candidate.Secret, windowLifetime.Token); }
                    catch (InvalidOperationException) when (row.Candidate.Secret is null && profile.SaveSecret) { profile.SaveSecret = false; await App.Store.SaveProfileAsync(profile, null, windowLifetime.Token); }
                }
                catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or IOException) { problems.Add($"«{profile.Name}»: {ex.Message}"); continue; }
                imported++; last = profile;
            }
            var summary = new List<string> { $"Импортировано серверов: {imported}." };
            if (window.IncludeHostKeys && doc.HostKeys is { Count: > 0 } keys)
            {
                var (added, conflicts) = await App.Store.Trust.ImportAsync(keys, windowLifetime.Token);
                summary.Add($"Доверенных ключей добавлено: {added}." + (conflicts.Count > 0 ? $" Не заменены ({conflicts.Count}): здесь уже доверен другой ключ для {string.Join(", ", conflicts.Select(c => c.Host + ":" + c.Port).Distinct())} — сверьте отпечаток при подключении." : ""));
            }
            bool layout = false;
            if (window.IncludeSidebar && doc.Sidebar is { } side)
            {
                var settings = App.Store.Settings; settings.SidebarOrder = [.. side.Order]; settings.SidebarHidden = [.. side.Hidden]; settings.DockerModule = side.Docker; settings.ScriptsModule = side.Scripts; layout = true;
                summary.Add("Порядок разделов панели применён.");
            }
            if (window.IncludeRuleSets && doc.RuleSets is { Count: > 0 } sets)
            {
                var mine = App.Store.Settings.FirewallRuleSets;
                foreach (var set in sets)
                {
                    var copy = set.Clone(); copy.Id = Guid.NewGuid(); var name = copy.Name;
                    for (int i = 2; mine.Any(m => string.Equals(m.Name, name, StringComparison.CurrentCultureIgnoreCase)); i++) name = $"{copy.Name} ({i})";
                    copy.Name = name; mine.Add(copy);
                }
                summary.Add($"Наборов правил файервола добавлено: {sets.Count}.");
            }
            if (doc.Folders is { Count: > 0 } folders) { await SaveImportedFoldersAsync(folders, new Dictionary<Guid, string>(), App.Store.Profiles.ToArray()); summary.Add($"Значения папок для наследования: {folders.Count} (пароли папок не переносятся)."); }
            if (layout || window.IncludeRuleSets) await App.Store.SaveAsync(windowLifetime.Token);
            if (layout) App.RaiseModulesChanged();
            RefreshHome();
            if (last is not null) { viewModel.SelectedProfile = viewModel.VisibleProfiles.FirstOrDefault(p => p.Profile.Id == last.Id); if (viewModel.SelectedProfile is not null) servers.ScrollIntoView(viewModel.SelectedProfile); }
            viewModel.Announce(string.Join(" ", summary));
            if (problems.Count > 0) Ui.Error("Не все серверы импортированы:\n" + string.Join("\n", problems));
            return imported;
        }
        finally { profileEditorOpen = false; }
    }
    internal Func<GroupRunWindow, bool?>? GroupRunWindowOverride;
    Button alertsButton = null!;
    void UpdateAlertsButton()
    {
        int n = AlertCenter.ActiveCount;
        if (alertsButton.Content is Panel p) foreach (var t in p.Children.OfType<TextBlock>()) if (t.Text.StartsWith("Оповещения", StringComparison.Ordinal)) t.Text = n > 0 ? $"Оповещения ({n})" : "Оповещения";
        TaskbarItemInfo ??= new TaskbarItemInfo(); TaskbarItemInfo.Description = n > 0 ? $"SSH Explorer — оповещений: {n}" : "SSH Explorer";
    }
    internal ContextMenu AlertsMenu()
    {
        var menu = new ContextMenu();
        if (AlertCenter.Items.Count == 0) menu.Items.Add(new MenuItem { Header = App.Store.Settings.AlertsEnabled ? "Оповещений нет. Проверяются подключённые серверы." : "Оповещения выключены в «Параметры» → «Мониторинг».", IsEnabled = false });
        foreach (var item in AlertCenter.Items.Take(30)) menu.Items.Add(new MenuItem { Header = item.Display, IsEnabled = !item.Resolved, Tag = item.Server });
        if (AlertCenter.Items.Count > 0) { menu.Items.Add(new Separator()); menu.Items.Add(Ui.Menu("Очистить решённые и события", AlertCenter.ClearAll)); }
        return menu;
    }
    /// <summary>Shows Home with the alerts list open (a click on the Windows notification).</summary>
    internal void ShowAlerts() { tabs.SelectedItem = home; Dispatcher.InvokeAsync(() => Ui.OpenFlyout(alertsButton, AlertsMenu()), System.Windows.Threading.DispatcherPriority.Input); }
    /// <summary>Other open server tabs whose files can receive a copy.</summary>
    internal IReadOnlyList<CopyTarget> CopyTargets(ServerSession except) => sessions.Where(p => !ReferenceEquals(p.Value.Session, except) && p.Value.Session.State == SessionState.Connected && p.Value.Session.SftpAvailable)
        .Select(p => new CopyTarget(SessionTabInfo.GetTitle(p.Key), p.Value.Session, p.Value.CurrentFolder ?? p.Value.Session.Files.HomeDirectory)).ToArray();
    /// <summary>Opens the group run with the chosen server's group ticked (or every server when it has none).</summary>
    internal Task RunOnGroupAsync(ServerProfile? around = null)
    {
        if (closing) return Task.CompletedTask;
        var ssh = App.Store.Profiles.Where(p => !p.IsRdp).ToArray();
        if (ssh.Length == 0) { Ui.Info("Сохранённых SSH-серверов нет."); return Task.CompletedTask; }
        var chosen = around ?? viewModel.SelectedProfile?.Profile;
        var ids = chosen is null ? ssh.Select(p => p.Id).ToArray()
            : chosen.Group.Length > 0 ? ssh.Where(p => p.Group == chosen.Group).Select(p => p.Id).ToArray() : chosen.IsRdp ? Array.Empty<Guid>() : new[] { chosen.Id };
        var window = new GroupRunWindow(ssh, ids, new GroupRunner(App.Store)) { Owner = this };
        if (GroupRunWindowOverride is not null) GroupRunWindowOverride(window); else ModalRunner.Show(window, this, windowLifetime.Token);
        _ = App.Store.SaveAsync();
        return Task.CompletedTask;
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
        // The metric history stays unless the user agrees to delete it too (it may still be wanted for a report).
        long history = MetricsHub.Store.SizeOf(row.Profile.Id);
        if (history > 0 && (DeleteHistoryOverride?.Invoke() ?? Ui.Confirm($"Удалить и записанную историю метрик «{row.Name}» ({Ui.Size(history)})?")))
            MetricsHub.Delete(row.Profile.Id);
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
        if (profile.IsRdp) { await OpenDesktopAsync(profile, forceCredentials: forceCredentials); return; }
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
                // An SSH agent signs for the user: no password or passphrase dialog.
                if (profile.Authentication == AuthenticationKind.Agent) credentials = new SessionCredentials("");
                else if (saved is not null && !forceCredentials && !requireCredentials.Contains(profile.Id)) credentials = new SessionCredentials(saved);
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
    /// <summary>Test hook: answers «удалить и историю?» after a profile is deleted.</summary>
    internal Func<bool>? DeleteHistoryOverride;
    DataGridTemplateColumn? loadColumn;
    /// <summary>Test hook: receives the history window instead of showing it.</summary>
    internal Action<HistoryWindow>? HistoryWindowOverride;
    internal void ShowHistory(ServerProfile? profile)
    {
        if (profile is null) return;
        if (profile.IsRdp) { Ui.Info("История метрик записывается для серверов SSH (Linux и Windows с OpenSSH)."); return; }
        var window = new HistoryWindow(profile, this);
        if (HistoryWindowOverride is { } fake) fake(window); else window.Show();
    }
    /// <summary>«Нагрузка»: CPU over 24 hours and the latest CPU, memory and fullest disk of recorded servers.</summary>
    static DataGridTemplateColumn LoadColumn()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        panel.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        panel.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(ServerProfileViewModel.LoadToolTip)));
        var line = new FrameworkElementFactory(typeof(Sparkline));
        line.SetBinding(Sparkline.ValuesProperty, new Binding(nameof(ServerProfileViewModel.LoadHistory)));
        panel.AppendChild(line);
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(ServerProfileViewModel.LoadText)));
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        text.SetValue(TextBlock.FontSizeProperty, 12d);
        text.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        panel.AppendChild(text);
        return new DataGridTemplateColumn { Header = "Нагрузка", Width = 220, SortMemberPath = "Load", CellTemplate = new DataTemplate { VisualTree = panel } };
    }
    static DataGridTemplateColumn ProtocolColumn()
    {
        var icon = new FrameworkElementFactory(typeof(ContentControl));
        icon.SetBinding(ContentControl.ContentProperty, new Binding(nameof(ServerProfileViewModel.ProtocolGlyph)) { Converter = new GlyphIconConverter() });
        icon.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(ServerProfileViewModel.ProtocolName)));
        icon.SetBinding(System.Windows.Automation.AutomationProperties.NameProperty, new Binding(nameof(ServerProfileViewModel.ProtocolName)));
        icon.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        icon.SetValue(Control.IsTabStopProperty, false);
        return new DataGridTemplateColumn { Header = "", Width = 46, CanUserResize = false, CanUserSort = false, CellTemplate = new DataTemplate { VisualTree = icon } };
    }
    sealed class GlyphIconConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => value is string glyph ? Ui.Icon(glyph, 16, false) : null;
        public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => Binding.DoNothing;
    }
    /// <summary>
    /// Opens a remote desktop tab. An SSH profile opened «по RDP» uses the same address and user with port 3389. The
    /// password comes from the profile, from its folder (inheritance) or is asked for; it goes only to the RDP control.
    /// </summary>
    internal async Task OpenDesktopAsync(ServerProfile stored, bool asDesktop = false, bool forceCredentials = false, RdpView? previous = null)
    {
        if (closing) return;
        var source = stored;
        if (!stored.IsRdp)
        {
            if (!asDesktop) return;
            source = new ServerProfile { Id = stored.Id, Name = stored.Name, Host = stored.Host, Port = 3389, Username = stored.Username, Group = stored.Group, Protocol = ConnectionProtocol.Rdp, Rdp = stored.Rdp.Clone(), Inherit = stored.Inherit, TimeoutSeconds = stored.TimeoutSeconds, ProxyJump = stored.ProxyJump, Authentication = AuthenticationKind.Password };
        }
        var resolved = ProfileInheritance.Resolve(source, App.Store.Settings.Folders);
        if (resolved.Missing.Count > 0) { Ui.Error($"Профиль «{stored.Name}» наследует из папки «{stored.Group}»: {string.Join(", ", resolved.Missing)}, но в папке они не заданы. Задайте их: правый щелчок по группе на «Главной» → «Параметры папки…»."); return; }
        var profile = resolved.Profile;
        string? saved = null; bool unavailable = false;
        try
        {
            if (resolved.PasswordFolder is { } folder) saved = await App.Store.ReadSecretAsync(folder, windowLifetime.Token);
            else if (stored.IsRdp && stored.SaveSecret) saved = await App.Store.ReadSecretAsync(stored.Id, windowLifetime.Token);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException or FormatException) { unavailable = true; _ = LocalDiagnostics.RecordAsync("rdp.secret.read.failed", ex); }
        string secret;
        bool typed = false;
        if (saved is not null && !forceCredentials && !requireCredentials.Contains(stored.Id)) secret = saved;
        else
        {
            var entered = Dialogs.Credentials(profile, null, this, windowLifetime.Token, unavailable ? "Сохранённый пароль недоступен. Введите его заново." : null);
            if (entered is null) return;
            secret = entered.Secret; typed = true;
        }
        TabItem? tab = null; var view = previous;
        if (view is null)
        {
            view = new RdpView(profile, desktopLayer) { ReconnectRequested = v => ReconnectDesktopAsync(v), FilesRequested = v => OpenFilesOverSshAsync(v.Profile) };
            view.StateChanged += (_, _) => { if (!closing && !Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(RefreshHome); };
            tab = AddDesktopTab(stored, view);
        }
        else tabs.SelectedItem = desktops.FirstOrDefault(p => ReferenceEquals(p.Value, view)).Key;
        RefreshHome();
        bool ok;
        try { ok = await view.ConnectAsync(secret, windowLifetime.Token); }
        catch (OperationCanceledException) { return; }
        if (!ok) { if (view.LastError is { } why && why.Contains("пароль", StringComparison.OrdinalIgnoreCase)) requireCredentials.Add(stored.Id); return; }
        requireCredentials.Remove(stored.Id);
        if (typed && stored.IsRdp && stored.SaveSecret && resolved.PasswordFolder is null && saved != secret)
        {
            try { await App.Store.SaveAuthenticatedSecretAsync(stored, secret, windowLifetime.Token); }
            catch (Exception ex) { _ = LocalDiagnostics.RecordAsync("rdp.secret.commit.failed", ex); }
        }
    }
    Task ReconnectDesktopAsync(RdpView view) => OpenDesktopAsync(App.Store.Profiles.FirstOrDefault(p => p.Id == view.Profile.Id) ?? view.Profile, asDesktop: true, previous: view);
    TabItem AddDesktopTab(ServerProfile profile, RdpView view)
    {
        var tab = new TabItem { Content = view };
        tab.SetResourceReference(StyleProperty, "SessionTabItemStyle");
        var number = sessionNumbers.GetValueOrDefault(profile.Id) + 1; sessionNumbers[profile.Id] = number; var title = profile.Name + (number > 1 ? $" ({number})" : "");
        SessionTabInfo.SetTitle(tab, title); SessionTabInfo.SetDescription(tab, "RDP · " + (view.Profile.Rdp.Domain.Length > 0 ? view.Profile.Rdp.Domain + "\\" : "") + view.Profile.Username + "@" + view.Profile.Endpoint);
        System.Windows.Automation.AutomationProperties.SetName(tab, title); System.Windows.Automation.AutomationProperties.SetHelpText(tab, SessionTabInfo.GetDescription(tab));
        var close = Ui.Button("", async () => await CloseTab(tab), "Закрыть «" + title + "» (Ctrl+F4)"); close.Name = "SessionTabCloseButton"; close.Content = Ui.Icon(Ui.CloseGlyph, 11, false); close.SetResourceReference(StyleProperty, "TabCloseButtonStyle"); WindowChrome.SetIsHitTestVisibleInChrome(close, true);
        var label = Ui.Text(title); label.TextTrimming = TextTrimming.CharacterEllipsis; label.MinWidth = 0;
        var header = new Grid { ToolTip = title + "\n" + SessionTabInfo.GetDescription(tab) }; header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(Ui.Icon(IconCatalog.RemoteDesktop, 16)); Grid.SetColumn(label, 1); header.Children.Add(label); Grid.SetColumn(close, 2); header.Children.Add(close); tab.Header = header; WindowChrome.SetIsHitTestVisibleInChrome(header, true);
        bool middlePressed = false;
        tab.PreviewMouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle) { middlePressed = true; e.Handled = true; } };
        tab.PreviewMouseUp += async (_, e) => { if (e.ChangedButton != MouseButton.Middle) return; e.Handled = true; if (!middlePressed) return; middlePressed = false; await Ui.Guard(() => CloseTab(tab)); };
        tab.MouseLeave += (_, _) => middlePressed = false;
        Point? start = null;
        header.PreviewMouseLeftButtonDown += (_, e) => start = e.GetPosition(header);
        header.PreviewMouseMove += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed && start is Point a && (e.GetPosition(header) - a).Length > 12 && !close.IsMouseOver) { start = null; DragDrop.DoDragDrop(header, TabDragData(tab), DragDropEffects.Move); } };
        header.AllowDrop = true;
        header.Drop += (_, e) => { if (DraggedTab(e.Data) is TabItem moved && moved != home && moved != tab) { var dest = tabs.Items.IndexOf(tab); tabs.Items.Remove(moved); tabs.Items.Insert(dest, moved); tabs.SelectedItem = moved; e.Handled = true; } };
        desktops[tab] = view; tabs.Items.Add(tab); tabs.SelectedItem = tab;
        return tab;
    }
    async Task CloseDesktopTabAsync(TabItem tab, RdpView view)
    {
        if (!closingTabs.Add(tab)) return;
        try
        {
            if (view.State == SessionState.Connected && !(DesktopCloseConfirmOverride ?? (t => Ui.Confirm(t)))($"Отключиться от «{view.Profile.Name}»? Программы на удалённом компьютере продолжат работать.")) return;
            var index = tabs.Items.IndexOf(tab); var next = ReferenceEquals(tabs.SelectedItem, tab) ? tabs.Items.OfType<TabItem>().ElementAtOrDefault(index + 1) ?? tabs.Items.OfType<TabItem>().ElementAtOrDefault(Math.Max(0, index - 1)) : null;
            await view.DisposeAsync();
            desktops.Remove(tab); if (next is not null) { tabs.SelectedItem = next; next.Focus(); } tabs.Items.Remove(tab); RefreshHome();
        }
        finally { closingTabs.Remove(tab); }
    }
    /// <summary>CI hook: open a desktop tab without connecting (fixture profiles).</summary>
    internal RdpView AddDesktopForTest(ServerProfile profile) { var view = new RdpView(profile, desktopLayer) { ReconnectRequested = v => ReconnectDesktopAsync(v) }; AddDesktopTab(profile, view); return view; }
    internal IReadOnlyCollection<RdpView> Desktops => desktops.Values;
    /// <summary>CI hook: the answer to «Отключиться от …?» when a connected desktop tab closes.</summary>
    internal Func<string, bool>? DesktopCloseConfirmOverride;
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
        // Middle click closes the tab, as in browsers; pressing and releasing must both happen on this tab.
        bool middlePressed = false;
        tab.PreviewMouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle) { middlePressed = true; e.Handled = true; } };
        tab.PreviewMouseUp += async (_, e) => { if (e.ChangedButton != MouseButton.Middle) return; e.Handled = true; if (!middlePressed) return; middlePressed = false; await Ui.Guard(() => CloseTab(tab)); };
        tab.MouseLeave += (_, _) => middlePressed = false;
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
        if (desktops.TryGetValue(tab, out var desktop)) { await CloseDesktopTabAsync(tab, desktop); return; }
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
        foreach (var desktop in desktops.Values.ToArray())
        {
            try { await desktop.DisposeAsync(); } catch (Exception ex) { await LocalDiagnostics.RecordAsync("app.close.desktop.failed", ex); }
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
    System.Windows.Threading.DispatcherTimer? healthTimer; bool healthBusy;
    /// <summary>Every few minutes while connected: nearly full disks and failed services become notifications.</summary>
    void StartHealthChecks()
    {
        if (healthTimer is not null) return;
        var minutes = Math.Clamp(App.Store.Settings.AlertIntervalMinutes, 1, 120);
        healthTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(minutes) };
        healthTimer.Tick += async (_, _) => await CheckHealthAsync();
        healthTimer.Start();
        _ = Dispatcher.InvokeAsync(async () => { await Task.Delay(TimeSpan.FromSeconds(15)); await CheckHealthAsync(); });
    }
    internal async Task CheckHealthAsync()
    {
        var settings = App.Store.Settings;
        if (healthBusy || disposed || !settings.AlertsEnabled || Session.State != SessionState.Connected || !Session.ShellAvailable) return;
        healthBusy = true;
        try { AlertCenter.Report(Session.Profile.Id, Session.Profile.Name, ServerHealth.Parse((Session.IsWindows ? await Session.ExecuteLongAsync(ServerHealth.WindowsCommand, TimeSpan.FromSeconds(60), Session.Lifetime) : await Session.ExecuteAsync(ServerHealth.Command, Session.Lifetime)).Output, Math.Clamp(settings.AlertDiskPercent, 50, 99))); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or Renci.SshNet.Common.SshException) { }
        finally { healthBusy = false; }
    }
    /// <summary>Opens the terminal and types <paramref name="text"/> without pressing Enter: the user reads and runs it.</summary>
    internal async Task TypeInTerminalAsync(string text)
    {
        ShowSection("terminal");
        for (int i = 0; i < 100 && terminals?.ActiveBody is not TerminalView; i++) await Task.Delay(100);
        if (terminals?.ActiveBody is not TerminalView terminal) return;
        await terminal.StartAsync(); await terminal.SendTextAsync(text); terminal.Focus();
    }
    /// <summary>The folder open in this tab's file manager, if any.</summary>
    internal string? CurrentFolder => files?.CurrentPath;
    readonly MainWindow main; readonly Grid host = new(); readonly TextBlock state = Ui.Text("Подключение…");
    readonly StackPanel side = new(), monitorNavigation = new();
    readonly ContentControl fileNavigationHost = new() { Focusable = false, IsTabStop = false, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    MonitorPage monitorPage = MonitorPage.Processes;
    readonly ContentControl screen = new() { Focusable = false, IsTabStop = false, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    readonly ContentControl addressHost = new() { Focusable = false, IsTabStop = false, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch }, commandHost = new() { Focusable = false, IsTabStop = false, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch }, fileStatusHost = new() { Focusable = false, IsTabStop = false, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    readonly Border connectionStatus = new();
    readonly bool presentationOnly;
    readonly Dictionary<string, Button> navigationButtons = [];
    readonly Dictionary<string, FrameworkElement[]> sideEntries = [];
    readonly Border filesLine = new() { Height = 1, Margin = new Thickness(8, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly Grid divider = new() { Name = "SidebarDivider", Margin = new Thickness(0, 4, 0, 4) };
    readonly Button customize;
    SidebarEditor? sidebarEditor;
    readonly string? startPath;
    readonly SessionViewModel viewModel = new();
    FileBrowserView? files; SplitWorkspace? terminals; MonitorView? monitor; HistoryView? historyView; SessionState? recordedState; ServerPanelView? serverPanel; DockerView? docker; ScriptsView? scriptsView; ServicesView? services; TunnelsView? tunnelsView; FirewallView? firewallView; WindowsFirewallView? windowsFirewallView; SecurityView? securityView; CronView? cronView; TunnelManager? tunnelManager; bool tunnelsAutoStarted; ScriptRunner? scriptRunner; EventHandler? modulesChanged; readonly Button reconnect;
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
        // Every top-level section is one entry; the task manager carries its pages with it. Order and visibility come
        // from Sidebar (shared by all tabs) and are edited in place with the gear button under the list.
        foreach (var (key, info) in SidebarCatalog.Entries) sideEntries[key] = [Nav(info.Glyph, info.Title, key)];
        monitorNavigation.Children.Add(Nav(IconCatalog.Processes, "Процессы", "processes"));
        monitorNavigation.Children.Add(Nav(IconCatalog.Performance, "Производительность", "performance"));
        monitorNavigation.Children.Add(Nav(IconCatalog.Services, "Службы", "services"));
        monitorNavigation.Visibility = Visibility.Collapsed; sideEntries["monitor"] = [sideEntries["monitor"][0], monitorNavigation];
        filesLine.SetResourceReference(Border.BackgroundProperty, "Edge");
        customize = Ui.IconButton(IconCatalog.Settings, ToggleSidebarEditor, "Настроить панель: порядок и видимость разделов"); customize.Name = "SidebarCustomize"; customize.Height = 24; customize.Width = 28;
        // The gear is the trailing end of the divider under the sections, so it reads as part of the panel, not a loose button.
        customize.Margin = new Thickness(0, 0, 2, 0); customize.Opacity = .7; customize.MouseEnter += (_, _) => customize.Opacity = 1; customize.MouseLeave += (_, _) => customize.Opacity = .7;
        divider.ColumnDefinitions.Add(new ColumnDefinition()); divider.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        divider.Children.Add(filesLine); Grid.SetColumn(customize, 1); divider.Children.Add(customize);
        System.Windows.Automation.AutomationProperties.SetName(customize, "Настроить панель разделов");
        reconnect = Ui.AsyncButton("Переподключиться", () => main.Reconnect(this, files?.CurrentPath)); reconnect.Visibility = Visibility.Collapsed; reconnect.Margin = new Thickness(7, 8, 7, 4);
        reconnect.SetBinding(VisibilityProperty, new Binding(nameof(SessionViewModel.CanReconnect)) { Converter = new BooleanToVisibilityConverter() });
        side.ContextMenuOpening += (_, e) => side.ContextMenu = SidebarMenu(e.OriginalSource as DependencyObject);
        ApplySidebar(); App.ModulesChanged += modulesChanged = (_, _) => Dispatcher.InvokeAsync(ApplySidebar);
        var sidebarLayout = new Grid(); sidebarLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); sidebarLayout.RowDefinitions.Add(new RowDefinition());
        sidebarLayout.Children.Add(side); Grid.SetRow(fileNavigationHost, 1); fileNavigationHost.Margin = new Thickness(5, 0, 5, 8); sidebarLayout.Children.Add(fileNavigationHost);
        var sidebar = new Border { Child = sidebarLayout, BorderThickness = new Thickness(0, 0, 1, 0) }; sidebar.SetResourceReference(Border.BackgroundProperty, "NavigationSurface"); sidebar.SetResourceReference(Border.BorderBrushProperty, "Edge"); body.Children.Add(sidebar);
        var preferredTreeWidth = App.Store.Settings.TreeWidth;
        var splitter = new GridSplitter { Width = 4, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch }; splitter.SetResourceReference(BackgroundProperty, "NavigationSurface"); Grid.SetColumn(splitter, 1); body.Children.Add(splitter); splitter.DragCompleted += (_, _) => { preferredTreeWidth = App.Store.Settings.TreeWidth = body.ColumnDefinitions[0].ActualWidth; _ = App.Store.SaveAsync(); };
        body.SizeChanged += (_, _) => { var width = Math.Clamp(preferredTreeWidth, 190, Math.Max(190, Math.Min(420, body.ActualWidth - 344))); if (Math.Abs(body.ColumnDefinitions[0].ActualWidth - width) > 1) body.ColumnDefinitions[0].Width = new GridLength(width); };
        Grid.SetColumn(screen, 2); body.Children.Add(screen); Grid.SetRow(body, 2); host.Children.Add(body); Content = host;
        Session.StateChanged += StateChanged;
    }
    /// <summary>Lays the panel out from the shared order; leaves a section that has just been hidden.</summary>
    void ApplySidebar()
    {
        if (disposed) return;
        var settings = App.Store.Settings; var items = Sidebar.Resolve(settings);
        side.Children.Clear();
        if (sidebarEditor is not null) { sidebarEditor.Refresh(items); side.Children.Add(sidebarEditor); }
        else
        {
            bool anyBefore = false;
            foreach (var item in items)
            {
                if (item.Key == Sidebar.Home && anyBefore) side.Children.Add(divider);
                foreach (var element in sideEntries[item.Key]) side.Children.Add(element);
                // Sections built on Linux tools (crontab, sshd, Docker, bash scripts) are not offered for a Windows server.
                bool visible = item.Visible && !(Session.IsWindows && LinuxOnly.Contains(item.Key));
                sideEntries[item.Key][0].Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                anyBefore |= visible;
            }
            // With «Файлы» first there is no divider above it: it closes the list instead.
            if (divider.Parent is null) side.Children.Add(divider);
            side.Children.Add(reconnect);
        }
        var current = section is "services" ? "monitor" : section;
        if (SidebarCatalog.Entries.ContainsKey(current) && !Sidebar.IsVisible(settings, current)) ShowSection(Sidebar.Home);
    }
    static readonly HashSet<string> LinuxOnly = new(StringComparer.Ordinal) { "cron", "security", "docker", "scripts" };
    bool windowsLayout;
    void ToggleSidebarEditor()
    {
        if (sidebarEditor is null) { bool keyboard = InputManager.Current.MostRecentInputDevice is KeyboardDevice; sidebarEditor = new SidebarEditor(() => { bool byKeyboard = InputManager.Current.MostRecentInputDevice is KeyboardDevice; sidebarEditor = null; ApplySidebar(); if (byKeyboard) customize.Focus(); }); ApplySidebar(); if (keyboard) sidebarEditor.FocusFirst(); }
        else sidebarEditor.Done();
    }
    internal bool IsEditingSidebar => sidebarEditor is not null;
    internal SidebarEditor? SidebarEditorView => sidebarEditor;
    ContextMenu SidebarMenu(DependencyObject? source)
    {
        var menu = new ContextMenu();
        var key = sideEntries.FirstOrDefault(p => source is not null && (ReferenceEquals(p.Value[0], source) || IsInside(source, p.Value[0]))).Key;
        if (key is not null && Sidebar.CanHide(key) && sidebarEditor is null)
            menu.Items.Add(Ui.Menu($"Скрыть «{SidebarCatalog.Entries[key].Title}»", () => SidebarEditor.Save(Sidebar.SetVisible(Sidebar.Resolve(App.Store.Settings), key, false))));
        menu.Items.Add(Ui.Menu(sidebarEditor is null ? "Настроить панель…" : "Готово", ToggleSidebarEditor, "", IconCatalog.Settings));
        return menu;
        static bool IsInside(DependencyObject node, DependencyObject root) { for (var n = node; n is not null; n = n is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(n) : LogicalTreeHelper.GetParent(n)) if (ReferenceEquals(n, root)) return true; return false; }
    }
    Button Nav(string glyph, string text, string key)
    {
        var b = Ui.Button("", () => ShowSection(key), text); var label = Ui.Text(text); label.ClearValue(TextBlock.FontWeightProperty); label.TextTrimming = TextTrimming.CharacterEllipsis;
        var content = new DockPanel { LastChildFill = true }; var icon = Ui.Icon(glyph); DockPanel.SetDock(icon, Dock.Left); content.Children.Add(icon); content.Children.Add(label); b.Content = content;
        b.SetResourceReference(StyleProperty, key is "processes" or "performance" or "services" ? "SubNavigationButtonStyle" : "NavigationButtonStyle"); navigationButtons[key] = b; return b;
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
            // Scheduled scripts run while this server is connected, whether or not their section is open.
            if (!presentationOnly && Session.State != recordedState)
            {
                // Metric history: this tab's connection records once a minute; connects and drops are marks on the graphs.
                if (Session.State == SessionState.Connected) { MetricsHub.Attach(Session); MetricsHub.Note(Session.Profile.Id, "connect", "Подключение из приложения"); }
                else if (recordedState == SessionState.Connected && Session.State == SessionState.Disconnected) MetricsHub.Note(Session.Profile.Id, "disconnect", "Связь с сервером потеряна");
                recordedState = Session.State;
            }
            if (Session.State == SessionState.Connected && !presentationOnly)
            {
                if (!Session.IsWindows) EnsureScriptRunner();
                _ = AutoStartTunnelsAsync(); StartHealthChecks();
                if (Session.IsWindows && !windowsLayout) { windowsLayout = true; ApplySidebar(); }
            }
        });
    }
    TunnelManager EnsureTunnelManager() => tunnelManager ??= new TunnelManager(Session);
    /// <summary>Opens the server's «при подключении» tunnels once per connection; a failure is shown in the status line.</summary>
    async Task AutoStartTunnelsAsync()
    {
        if (tunnelsAutoStarted) return; tunnelsAutoStarted = true;
        var specs = (App.Store.Profiles.FirstOrDefault(p => p.Id == Session.Profile.Id)?.Tunnels ?? Session.Profile.Tunnels).Where(t => t.StartOnConnect).Select(t => t.Clone()).ToArray();
        foreach (var spec in specs)
        {
            try { await EnsureTunnelManager().StartAsync(spec, Session.Lifetime); }
            catch (Exception ex) when (ex is not OperationCanceledException) { if (!disposed) SetNotice($"Туннель «{(spec.Name.Length > 0 ? spec.Name : SSHExplorer.Core.Tunnels.Describe(spec))}» не открылся: {ErrorMessages.Describe(ex)}"); }
            catch (OperationCanceledException) { return; }
        }
    }
    internal ScriptRunner EnsureScriptRunner()
    {
        if (scriptRunner is not null) return scriptRunner;
        scriptRunner = new ScriptRunner(Session, () => App.Store.Profiles.FirstOrDefault(p => p.Id == Session.Profile.Id)?.Scripts ?? Session.Profile.Scripts, () => App.Store.Settings.ScriptsModule);
        scriptRunner.Finished += (_, run) => Dispatcher.BeginInvoke(async () =>
        {
            if (disposed) return;
            try { await App.Store.SaveAsync(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await LocalDiagnostics.RecordAsync("scripts.save.failed", ex); }
            if (!run.Succeeded && run.Reason != "Вручную") AlertCenter.Raise(Session.Profile.Id, Session.Profile.Name, $"Скрипт «{run.ScriptName}» завершился с ошибкой ({(run.TimedOut ? "таймаут" : "код " + run.ExitCode)})");
            if (!run.Succeeded && run.Reason != "Вручную") SetNotice($"Скрипт «{run.ScriptName}» ({run.Reason.ToLowerInvariant()}) завершился с ошибкой: {(run.TimedOut ? "таймаут" : "код " + run.ExitCode)}. Подробности — в разделе «Скрипты».");
        });
        if (!presentationOnly) scriptRunner.StartSchedule();
        return scriptRunner;
    }
    internal async Task<bool> ConnectAsync(SessionCredentials credentials, ConnectionInteraction prompts, ConnectionAttempt attempt, Func<HostKeyPrompt, Task<TrustDecision>>? trustOverride = null)
    {
        string stage = "connection.transport";
        try
        {
            await Session.ConnectAsync(credentials, (key, ct) => trustOverride is null ? prompts.TrustAsync(key, ct) : trustOverride(key).WaitAsync(ct), attempt.Token, attempt.Id);
            attempt.Token.ThrowIfCancellationRequested();
            navigationButtons["terminal"].IsEnabled = Session.ShellAvailable;
            navigationButtons["monitor"].IsEnabled = navigationButtons["processes"].IsEnabled = navigationButtons["performance"].IsEnabled = navigationButtons["services"].IsEnabled = Session.ShellAvailable;
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
            files?.Dispose(); files = null; terminals?.Dispose(); terminals = null; monitor?.Dispose(); monitor = null;
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
            (key == "files" && ReferenceEquals(screen.Content, files) || key == "monitor" && ReferenceEquals(screen.Content, monitor) || key == "terminal" && ReferenceEquals(screen.Content, terminals) || key == "server" && serverPanel is not null && ReferenceEquals(screen.Content, serverPanel) || key == "docker" && docker is not null && ReferenceEquals(screen.Content, docker) || key == "scripts" && scriptsView is not null && ReferenceEquals(screen.Content, scriptsView) || key == "services" && services is not null && ReferenceEquals(screen.Content, services) || key == "tunnels" && tunnelsView is not null && ReferenceEquals(screen.Content, tunnelsView) || key == "firewall" && firewallView is not null && ReferenceEquals(screen.Content, firewallView) || key == "security" && securityView is not null && ReferenceEquals(screen.Content, securityView) || key == "cron" && cronView is not null && ReferenceEquals(screen.Content, cronView))) return;
        var previousContent = screen.Content; var previousPage = monitor?.Page;
        section = key;
        UpdateConnectionStatusVisibility();
        if (key != "monitor") monitor?.SetActive(false);
        monitorNavigation.Visibility = key is "monitor" or "services" ? Visibility.Visible : Visibility.Collapsed;
        if (files is not null) Ui.SetIsFileSectionActive(files.Navigation, key == "files");
        addressHost.Visibility = commandHost.Visibility = fileStatusHost.Visibility = key == "files" ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (name, button) in navigationButtons)
        {
            Ui.SetIsNavigationActive(button, key == "monitor" ? name == (monitorPage == MonitorPage.Processes ? "processes" : "performance") : name == key);
            button.FontWeight = name == "monitor" && key is "monitor" or "services" ? FontWeights.SemiBold : FontWeights.Normal;
        }
        if (key == "terminal") { _ = OpenTerminalAsync(); return; }
        if (key == "scripts")
        {
            scriptsView ??= new ScriptsView(Session, EnsureScriptRunner());
            screen.Content = scriptsView;
            if (!ReferenceEquals(previousContent, screen.Content)) UiMotion.SurfaceChanged(screen);
            return;
        }
        if (key == "history")
        {
            historyView ??= new HistoryView(Session.Profile.Id, Session);
            screen.Content = historyView; historyView.Reload();
            if (!ReferenceEquals(previousContent, screen.Content)) UiMotion.SurfaceChanged(screen);
            return;
        }
        if (key == "cron")
        {
            cronView ??= new CronView(new CronService(Session));
            screen.Content = cronView;
            if (!ReferenceEquals(previousContent, screen.Content)) UiMotion.SurfaceChanged(screen);
            return;
        }
        if (key == "security")
        {
            securityView ??= new SecurityView(new KeyLoginService(Session, App.Store));
            screen.Content = securityView;
            if (!ReferenceEquals(previousContent, screen.Content)) UiMotion.SurfaceChanged(screen);
            return;
        }
        if (key == "firewall")
        {
            // Explains an unconnected session or missing rights inside the page, like Docker and «Службы».
            if (Session.IsWindows) { windowsFirewallView ??= new WindowsFirewallView(new WindowsFirewallService(Session)); screen.Content = windowsFirewallView; }
            else { firewallView ??= new FirewallView(new FirewallService(Session)); screen.Content = firewallView; }
            if (!ReferenceEquals(previousContent, screen.Content)) UiMotion.SurfaceChanged(screen);
            return;
        }
        if (key == "tunnels")
        {
            tunnelsView ??= new TunnelsView(Session, EnsureTunnelManager());
            screen.Content = tunnelsView; tunnelsView.Reload();
            if (!ReferenceEquals(previousContent, screen.Content)) UiMotion.SurfaceChanged(screen);
            return;
        }
        if (key == "services")
        {
            // Like Docker: an unconnected or shell-less session is explained inside the page, never by a dialog or crash.
            services ??= new ServicesView(new SystemdService(Session));
            screen.Content = services;
            if (!ReferenceEquals(previousContent, screen.Content)) UiMotion.SurfaceChanged(screen);
            return;
        }
        if (key == "docker")
        {
            docker ??= new DockerView(new DockerService(Session));
            screen.Content = docker;
            if (!ReferenceEquals(previousContent, screen.Content)) UiMotion.SurfaceChanged(screen);
            return;
        }
        if (key == "server")
        {
            serverPanel ??= new ServerPanelView(Session, presentationOnly ? null : () => main.Reconnect(this, files?.CurrentPath), Session.ShellAvailable ? () => ShowSection("terminal") : null, PendingWork) { TypeInTerminal = Session.ShellAvailable ? TypeInTerminalAsync : null };
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
    /// <summary>The «Терминал» section: up to four shells side by side or stacked, each its own PTY on its own connection.</summary>
    SplitWorkspace CreateTerminals()
    {
        SplitWorkspace? workspace = null;
        workspace = new SplitWorkspace(() =>
        {
            var view = new TerminalView(Session);
            // «Во все терминалы»: what is typed in one pane is repeated in the others, and the switch is shared.
            view.Typed += text => { foreach (var other in workspace!.Bodies.OfType<TerminalView>()) if (!ReferenceEquals(other, view)) _ = other.TypeAsync(text); };
            view.BroadcastChanged += on => { foreach (var other in workspace!.Bodies.OfType<TerminalView>()) other.Broadcasting = on; };
            view.PaneShortcut += key => workspace!.Shortcut(view, key);
            view.Focused += () => workspace!.FocusBody(view);
            return view;
        }, body => { ((TerminalView)body).Dispose(); Dispatcher.InvokeAsync(() => UpdateBroadcast(workspace!)); });
        workspace.PaneAdded += body => { _ = StartPaneAsync((TerminalView)body); UpdateBroadcast(workspace); };
        return workspace;
    }
    static void UpdateBroadcast(SplitWorkspace workspace) { var all = workspace.Bodies.OfType<TerminalView>().ToArray(); bool on = all.Any(t => t.Broadcasting); foreach (var t in all) { t.SetBroadcastAvailable(all.Length > 1); if (all.Length > 1) t.Broadcasting = on; } }
    async Task StartPaneAsync(TerminalView view)
    {
        try { await view.StartAsync(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { await LocalDiagnostics.RecordAsync("terminal.split.failed", ex); if (!disposed) SetNotice("Новый терминал не открылся: " + ErrorMessages.Describe(ex)); }
        catch (OperationCanceledException) { }
    }
    async Task OpenTerminalAsync()
    {
        if (!Session.ShellAvailable || disposed) return;
        var operation = Guid.NewGuid();
        try
        {
            await LocalDiagnostics.RecordOperationAsync(operation, "terminal.initialize", "started");
            if (disposed || section != "terminal") throw new OperationCanceledException();
            terminals ??= CreateTerminals(); screen.Content = terminals;
            // WebView2 is an HWND host: do not animate/reposition it or delay its input.
            UiMotion.Stop(screen); screen.Opacity = 1; screen.RenderTransform = Transform.Identity;
            await ((TerminalView)terminals.Start()).StartAsync();
            await LocalDiagnostics.RecordOperationAsync(operation, "terminal.initialize", "succeeded");
        }
        catch (Exception ex)
        {
            await LocalDiagnostics.RecordOperationAsync(operation, "terminal.initialize", ex is OperationCanceledException ? "cancelled" : "failed", ex);
            if (ex is not OperationCanceledException) { terminals?.Dispose(); terminals = null; }
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
        if (disposed) return; disposed = true; healthTimer?.Stop(); AlertCenter.Forget(Session.Profile.Id); MetricsHub.Detach(Session); historyView?.Dispose(); if (modulesChanged is not null) App.ModulesChanged -= modulesChanged; docker?.Dispose(); services?.Dispose(); tunnelsView?.Dispose(); firewallView?.Dispose(); windowsFirewallView?.Dispose(); securityView?.Dispose(); cronView?.Dispose(); if (tunnelManager is not null) _ = tunnelManager.DisposeAsync().AsTask(); scriptRunner?.Dispose(); connecting?.Cancel(); interaction?.Dispose(); UiMotion.Stop(screen);
        Session.StateChanged -= StateChanged;
        monitor?.Dispose(); files?.Dispose(); terminals?.Dispose();
        try { await Session.DisposeAsync(); }
        finally { if (diagnostics is not null) Session.StageChanged -= diagnostics; }
    }
    internal void CancelDragFeedback() => files?.CancelDragFeedback();
}
