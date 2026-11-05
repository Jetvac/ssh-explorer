using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using SSHExplorer.Core;
using SSHExplorer.App.ViewModels;

namespace SSHExplorer.App;

public sealed partial class FileBrowserView : UserControl, IDisposable
{
    readonly ServerSession session; readonly MainWindow main;
    readonly IFileBrowserOperations operations;
    FileNameEdit? nameEdit;
    Panel? viewModeRow;
    bool creating, addressPending;
    int addressRequest;
    readonly DockPanel root = new(); readonly DataGrid table = new(); readonly ListBox tiles = new() { SelectionMode = SelectionMode.Extended };
    readonly ContentControl content = new() { Focusable = false, IsTabStop = false, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch }; readonly TextBox address = new(); readonly StackPanel breadcrumbs = new() { Orientation = Orientation.Horizontal };
    readonly TextBox search = new() { MinWidth = 180, ToolTip = "Поиск по имени в текущей папке" }; readonly TextBlock status = Ui.Text(""), message = Ui.Text("");
    readonly TreeView tree = new() { MinHeight = 0 }; readonly Grid navigation = new();
    readonly List<TreeViewItem> pins = []; readonly Border preview = new() { Width = 280, Padding = new Thickness(16), BorderThickness = new Thickness(1, 0, 0, 0) };
    readonly FileBrowserViewModel viewModel = new();
    FrameworkElement addressBar = null!, commandBar = null!, statusBar = null!;
    CancellationTokenSource navigationCts = new(); CancellationTokenSource previewCts = new(); readonly CancellationTokenSource lifetime = new();
    readonly Dictionary<string, ImageSource> imageCache = []; long cacheBytes; readonly SemaphoreSlim imageSlots;
    readonly Dictionary<string, Task> treeLoads = []; bool syncingTree;
    CancellationTokenSource? recursiveSearchCts; CancellationTokenSource thumbnailCts = new();
    readonly HashSet<Window> ownedWindows = [];
    List<FileItemViewModel> all { get => viewModel.Entries; set => viewModel.SetEntries(value); }
    string mode { get => viewModel.ViewMode; set => viewModel.ViewMode = value; }
    string sortKey { get => viewModel.SortKey; set => viewModel.SortKey = value; }
    bool descending { get => viewModel.Descending; set => viewModel.Descending = value; }
    long generation => viewModel.NavigationGeneration;
    bool disposed, restoringSelection;
    public string CurrentPath => viewModel.CurrentPath;
    /// <summary>Read-only text view of the selected regular file (up to 10 MiB); no external program, nothing is written.</summary>
    Task ViewTextAsync()
    {
        var chosen = Selected();
        if (chosen.Count != 1 || chosen[0].Entry is not { IsRegularFile: true, IsSymbolicLink: false } entry) { viewModel.Message = "Выберите один обычный файл для просмотра."; return Task.CompletedTask; }
        if (entry.Length > TextViewerWindow.MaximumBytes) { viewModel.Message = $"Файл больше {Ui.Size(TextViewerWindow.MaximumBytes)}: скачайте его или откройте в редакторе."; return Task.CompletedTask; }
        new TextViewerWindow(entry.Name, ct => session.Files.ReadBytesAsync(entry.FullPath, TextViewerWindow.MaximumBytes, ct)) { Owner = Window.GetWindow(this) }.Show();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Renames the selection by pattern after a checked preview. Blocked sources go through temporary names first, so a swap or
    /// chain never overwrites. Stops at the first failure and says exactly what was renamed and what is left.
    /// </summary>
    async Task BatchRenameAsync()
    {
        var chosen = Selected(); if (chosen.Count == 0) return;
        var names = viewModel.Entries.Select(e => e.Entry.Name).ToHashSet(StringComparer.Ordinal);
        var directory = viewModel.CurrentPath;
        var window = new BatchRenameWindow(chosen.Select(r => (directory, r.Entry.Name, r.Entry.IsDirectory)).ToArray(), _ => names) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true) return;
        var operations = BatchRename.Operations(window.Plan, _ => ".sshexplorer-rename-" + Guid.NewGuid().ToString("N")[..12]);
        var done = new List<(string From, string To)>();
        try { foreach (var (from, to) in operations) { await session.Files.RenameAsync(from, to, lifetime.Token); done.Add((from, to)); } }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var temporary = done.Where(d => d.To.Contains("/.sshexplorer-rename-", StringComparison.Ordinal) && !done.Any(x => x.From == d.To)).Select(d => $"{PosixPath.Name(d.From)} → {PosixPath.Name(d.To)}").ToArray();
            Ui.Error($"Переименование остановлено на шаге {done.Count + 1} из {operations.Count}: {ErrorMessages.Describe(ex)}" + (temporary.Length > 0 ? "\n\nВо временных именах остались:\n" + string.Join("\n", temporary) : ""));
        }
        finally { await RefreshAsync(); }
    }

    /// <summary>Opens the comparison of this folder with a chosen Windows folder (non-modal, so transfers stay visible).</summary>
    Task CompareWithLocal()
    {
        var pick = new Microsoft.Win32.OpenFolderDialog { Title = "Папка на компьютере для сравнения с " + viewModel.CurrentPath };
        if (pick.ShowDialog(Window.GetWindow(this)) != true) return Task.CompletedTask;
        new CompareWindow(session, viewModel.CurrentPath, pick.FolderName) { Owner = Window.GetWindow(this) }.Show();
        return Task.CompletedTask;
    }
    public event Action? FileNavigationRequested;
    public event Action? NavigationCommitted;
    public UIElement Navigation => navigation;
    public FrameworkElement AddressBar => addressBar;
    public FrameworkElement CommandBar => commandBar;
    public FrameworkElement StatusBar => statusBar;
    public FileBrowserView(ServerSession session, MainWindow main) : this(session, main, new SftpBrowserOperations(session.Files)) { }
    internal FileBrowserView(ServerSession session, MainWindow main, IFileBrowserOperations operations)
    {
        this.session = session; this.main = main; this.operations = operations; mode = App.Store.Settings.ViewMode; imageSlots = new SemaphoreSlim(Math.Clamp(App.Store.Settings.PreviewParallelism, 1, 4));
        DataContext = viewModel; viewModel.SortKey = string.IsNullOrEmpty(App.Store.Settings.FileSortKey) ? "Name" : App.Store.Settings.FileSortKey; viewModel.Descending = App.Store.Settings.FileSortDescending;
        viewModel.Available = operations.Available;
        status.SetBinding(TextBlock.TextProperty, new Binding(nameof(FileBrowserViewModel.StatusText)));
        message.SetBinding(TextBlock.TextProperty, new Binding(nameof(FileBrowserViewModel.Message)));
        message.SetBinding(VisibilityProperty, new Binding(nameof(FileBrowserViewModel.Message)) { Converter = new NonEmptyTextVisibilityConverter() });
        SetResourceReference(BackgroundProperty, "Paper");
        BuildNavigation(); BuildAddress(); BuildCommands(); BuildTable(); BuildTiles();
        var lower = new Grid(); lower.ColumnDefinitions.Add(new ColumnDefinition()); lower.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); lower.Children.Add(content); Grid.SetColumn(preview, 1); lower.Children.Add(preview); preview.SetResourceReference(Border.BorderBrushProperty, "Edge"); root.Children.Add(lower);
        Content = root; SetMode(mode, false); PreviewKeyDown += HandleKey;
        // Finish a deferred group click before a child ListBox's class handler releases capture.
        AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler((_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left || pressedGroupOwner is null) return;
            FinishSelectedGroupClick(); e.Handled = true;
        }), true);
        SizeChanged += (_, _) => UpdatePreviewVisibility();
        PreviewMouseDown += (_, e) => { if (e.ChangedButton == MouseButton.XButton1) { e.Handled = true; _ = Ui.Guard(Back); } if (e.ChangedButton == MouseButton.XButton2) { e.Handled = true; _ = Ui.Guard(Forward); } };
        if (!session.SftpAvailable) viewModel.Message = "SFTP недоступен. Терминал и мониторинг остаются доступны при наличии shell.";
        Theme.Changed += RefreshPalette;
        Unloaded += (_, _) => { EndLasso(); CancelNameEdit(); };
    }
    void BuildNavigation()
    {
        navigation.DataContext = viewModel;
        navigation.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); navigation.RowDefinitions.Add(new RowDefinition());
        var home = Ui.AsyncCommandButton(Ui.HomeGlyph, "Домашняя папка", () => NavigateAsync("~")); home.SetResourceReference(StyleProperty, "NavigationButtonStyle"); navigation.Children.Add(home);
        var homeMenu = new ContextMenu(); homeMenu.Items.Add(Ui.AsyncMenu("Открыть", () => NavigateAsync("~"), glyph: Ui.HomeGlyph)); homeMenu.Items.Add(Ui.AsyncMenu("Открыть в новой вкладке", () => main.OpenServer(session.Profile, "~"), glyph: Ui.NewTabGlyph)); homeMenu.Items.Add(Ui.Menu("Копировать путь", () => Clipboard.SetText(session.Files.HomeDirectory), glyph: Ui.CopyGlyph)); Ui.AttachContextMenu(home, homeMenu);
        Grid.SetRow(tree, 1); navigation.Children.Add(tree);
        ScrollViewer.SetVerticalScrollBarVisibility(tree, ScrollBarVisibility.Auto); ScrollViewer.SetHorizontalScrollBarVisibility(tree, ScrollBarVisibility.Disabled);
        var rootNode = Node("Корень /", "/"); rootNode.ToolTip = "Корень файловой системы, доступный этой учётной записи (с учётом SFTP chroot)"; tree.Items.Add(rootNode);
        RefreshPins();
        tree.SelectedItemChanged += async (_, e) => { if (!syncingTree && e.NewValue is TreeViewItem { Tag: string path } && path != CurrentPath) await Ui.Guard(() => NavigateAsync(path)); };
    }
    TreeViewItem Node(string name, string path)
    {
        var header = new DockPanel { LastChildFill = true }; var image = new ShellFileIcon { Entry = new RemoteEntry { IsDirectory = true }, Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0) }; DockPanel.SetDock(image, Dock.Left); header.Children.Add(image); var label = Ui.Text(name); label.TextTrimming = TextTrimming.CharacterEllipsis; header.Children.Add(label);
        var node = new TreeViewItem { Header = header, Tag = path, ToolTip = path, AllowDrop = true }; Ui.SetTreeDepth(node, path.Split('/', StringSplitOptions.RemoveEmptyEntries).Length); Ui.SetIsCurrentFolder(node, path == CurrentPath); node.Items.Add(new TreeViewItem { Header = "Загрузка…", IsEnabled = false });
        node.Expanded += async (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, node)) return;
            await LoadNode(node);
        };
        node.DragOver += (_, e) => DropOver(e, path, node);
        node.DragLeave += (_, _) => ClearDropTarget();
        node.Drop += async (_, e) => { e.Handled = true; var effect = CanDropTo(e.Data, path, e.KeyStates); e.Effects = effect; ClearDropTarget(); if (effect != DragDropEffects.None) await Ui.Guard(() => DropData(e.Data, path, e.KeyStates)); };
        var menu = new ContextMenu(); menu.Items.Add(Ui.AsyncMenu("Открыть", () => NavigateAsync(path), glyph: Ui.FolderGlyph)); menu.Items.Add(Ui.AsyncMenu("Открыть в новой вкладке", () => main.OpenServer(session.Profile, path), glyph: Ui.NewTabGlyph)); menu.Items.Add(Ui.AsyncMenu("Открыть в терминале", () => OpenTerminal(path), glyph: Ui.TerminalGlyph)); menu.Items.Add(Ui.AsyncMenu("Закрепить / открепить", () => TogglePin(path), glyph: Ui.PinGlyph)); menu.Items.Add(Ui.Menu("Копировать путь", () => Clipboard.SetText(path), glyph: Ui.CopyGlyph)); menu.Opened += (_, _) => { if (menu.Items[3] is MenuItem pinItem) pinItem.Header = session.Profile.Bookmarks.Contains(path) ? "Открепить папку" : "Закрепить папку"; }; Ui.AttachContextMenu(node, menu); return node;
    }
    async Task LoadNode(TreeViewItem node)
    {
        var path = node.Tag as string; if (path is null || node.Items.Count != 1 || (node.Items[0] as TreeViewItem)?.Tag is not null) return;
        if (treeLoads.TryGetValue(path, out var existing)) { await existing; return; }
        async Task Load()
        {
            try { var entries = await session.Files.ListAsync(path, lifetime.Token); node.Items.Clear(); foreach (var d in entries.Where(x => x.IsDirectory && !x.IsSymbolicLink).OrderBy(x => x.Name, NaturalNameComparer.Instance)) node.Items.Add(Node(d.Name, d.FullPath)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { node.Items.Clear(); node.Items.Add(new TreeViewItem { Header = ErrorMessages.Describe(ex), IsEnabled = false }); }
        }
        var task = Load(); treeLoads[path] = task; try { await task; } finally { treeLoads.Remove(path); }
    }
    void RefreshPins()
    {
        foreach (var pin in pins) tree.Items.Remove(pin); pins.Clear(); foreach (var path in session.Profile.Bookmarks)
        {
            var label = Ui.Text(PosixPath.Name(path) is { Length: > 0 } n ? n : path); label.TextTrimming = TextTrimming.CharacterEllipsis;
            var pin = new TreeViewItem { Header = Ui.Row(Ui.Icon(Ui.PinGlyph, 16), label), Tag = path, ToolTip = "Закреплённая папка: " + path, AllowDrop = true };
            Ui.SetIsCurrentFolder(pin, path == CurrentPath);
            pin.DragOver += (_, e) => DropOver(e, path, pin); pin.DragLeave += (_, _) => ClearDropTarget();
            pin.Drop += async (_, e) => { e.Handled = true; var effect = CanDropTo(e.Data, path, e.KeyStates); e.Effects = effect; ClearDropTarget(); if (effect != DragDropEffects.None) await Ui.Guard(() => DropData(e.Data, path, e.KeyStates)); };
            var menu = new ContextMenu(); menu.Items.Add(Ui.AsyncMenu("Открыть", () => NavigateAsync(path), glyph: Ui.FolderGlyph)); menu.Items.Add(Ui.AsyncMenu("Открепить", () => TogglePin(path), glyph: Ui.PinGlyph)); Ui.AttachContextMenu(pin, menu); tree.Items.Insert(pins.Count, pin); pins.Add(pin);
        }
    }
    async Task TogglePin(string path) { if (!session.Profile.Bookmarks.Remove(path)) session.Profile.Bookmarks.Add(path); await App.Store.SaveAsync(); RefreshPins(); }
    void BuildAddress()
    {
        var bar = new Grid { Margin = new Thickness(10, 8, 16, 8), DataContext = viewModel, MinHeight = 38 }; bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); bar.ColumnDefinitions.Add(new ColumnDefinition { MinWidth = 120 }); bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180), MinWidth = 140 });
        search.MinWidth = 140;
        bar.SizeChanged += (_, _) => bar.ColumnDefinitions[2].Width = new GridLength(Math.Clamp(bar.ActualWidth * .27, 140, 260));
        var back = Ui.AsyncIconButton(Ui.BackGlyph, Back, "Назад · Alt+←"); back.SetBinding(IsEnabledProperty, new Binding(nameof(FileBrowserViewModel.CanGoBack)));
        var forward = Ui.AsyncIconButton(Ui.ForwardGlyph, Forward, "Вперёд · Alt+→"); forward.SetBinding(IsEnabledProperty, new Binding(nameof(FileBrowserViewModel.CanGoForward)));
        var up = Ui.AsyncIconButton(Ui.UpGlyph, Up, "Вверх · Alt+↑"); up.SetBinding(IsEnabledProperty, new Binding(nameof(FileBrowserViewModel.CanGoUp)));
        var buttons = Ui.Row(back, forward, up, Ui.AsyncIconButton(Ui.RefreshGlyph, RefreshAsync, "Обновить · F5")); bar.Children.Add(buttons);
        var pathHost = new Grid { Margin = new Thickness(8, 0, 12, 0), MinHeight = 38 }; var breadcrumbScroll = new ScrollViewer { Content = breadcrumbs, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false }; var pathBorder = new Border { Name = "AddressCrumbs", CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 0, 8, 0), Child = breadcrumbScroll, Cursor = Cursors.IBeam }; pathBorder.SetResourceReference(Border.BackgroundProperty, "Surface");
        // beta.5 listened to the bubbling MouseDown of the Border. The inner ScrollViewer marks
        // MouseLeftButtonDown handled when it takes focus, so the free area never started editing.
        // Preview runs before the ScrollViewer; breadcrumb buttons keep their own navigation.
        pathBorder.PreviewMouseLeftButtonDown += (_, e) => { if (FindAncestor<ButtonBase>(e.OriginalSource as DependencyObject) is null) { e.Handled = true; EditAddress(); } };
        breadcrumbs.Cursor = Cursors.Arrow; pathHost.Children.Add(pathBorder);
        address.Visibility = Visibility.Collapsed;
        address.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; await SubmitAddressAsync(); }
            else if (e.Key == Key.Escape) { e.Handled = true; CancelAddress(); }
        };
        // Focus may move to a command or an error. Only success or Escape ends address editing.
        System.Windows.Automation.AutomationProperties.SetName(address, "Путь к папке");
        address.TextChanged += (_, _) => { if (!addressPending) { Ui.SetValidationMessage(address, ""); address.ToolTip = null; System.Windows.Automation.AutomationProperties.SetHelpText(address, "Enter — открыть · Esc — отменить"); } };
        pathHost.Children.Add(address); Grid.SetColumn(pathHost, 1); bar.Children.Add(pathHost);
        search.SetBinding(TextBox.TextProperty, new Binding(nameof(FileBrowserViewModel.SearchQuery)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        search.TextChanged += (_, _) => { viewModel.SearchQuery = search.Text; ApplyItems(); }; System.Windows.Automation.AutomationProperties.SetName(search, "Поиск в текущей папке");
        var searchMenu = new ContextMenu(); searchMenu.Items.Add(Ui.AsyncMenu("Искать во вложенных папках…", RecursiveSearch, glyph: Ui.SearchGlyph)); searchMenu.Items.Add(Ui.Menu("Отменить рекурсивный поиск", () => recursiveSearchCts?.Cancel(), glyph: Ui.CloseGlyph)); searchMenu.Items.Add(Ui.AsyncMenu("Вернуться к содержимому папки", RefreshAsync, glyph: Ui.RefreshGlyph)); Ui.AttachContextMenu(search, searchMenu);
        var searchHost = new Grid(); searchHost.Children.Add(search); var hint = Ui.Text("Поиск в этой папке", 13); hint.Margin = new Thickness(9, 0, 6, 0); hint.IsHitTestVisible = false; hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); searchHost.Children.Add(hint); search.TextChanged += (_, _) => hint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(searchHost, 2); bar.Children.Add(searchHost); addressBar = bar; bar.PreviewKeyDown += HandleBarShortcut;
    }
    void BuildCommands()
    {
        var commands = new ResponsiveCommandBar(() => BuildContextMenu(Selected().Count > 0)) { Margin = new Thickness(10, 3, 10, 5) };
        ContextMenu CreateMenu() { var m = new ContextMenu(); m.Items.Add(Ui.AsyncMenu("Папку", () => Create(true), "Ctrl+Shift+N", Ui.FolderGlyph)); m.Items.Add(Ui.AsyncMenu("Файл", () => Create(false), glyph: Ui.FileGlyph)); return m; }
        MenuItem Submenu(string label, string glyph, Func<ContextMenu> factory) { var item = new MenuItem { Header = label, Icon = Ui.Icon(glyph, 16) }; var menu = factory(); while (menu.Items.Count > 0) { var child = menu.Items[0]; menu.Items.RemoveAt(0); item.Items.Add(child); } return item; }
        var create = Ui.FlyoutButton(Ui.AddGlyph, "Создать", CreateMenu); create.SetBinding(IsEnabledProperty, new Binding(nameof(FileBrowserViewModel.CanCreate))); commands.Add("Create", create, () => Submenu("Создать", Ui.AddGlyph, CreateMenu), 100);
        void Action(string key, string glyph, string label, Func<Task> action, string availability, int priority, string gesture = "")
        {
            var button = Ui.AsyncIconButton(glyph, action, label + (gesture.Length > 0 ? " · " + gesture : ""));
            button.SetBinding(IsEnabledProperty, new Binding(availability));
            commands.Add(key, button, () => Ui.AsyncMenu(label, action, gesture, glyph), priority);
        }
        Action("Cut", Ui.CutGlyph, "Вырезать", () => CopySelection(true), nameof(FileBrowserViewModel.HasSelection), 70, "Ctrl+X");
        Action("Copy", Ui.CopyGlyph, "Копировать", () => CopySelection(false), nameof(FileBrowserViewModel.HasSelection), 90, "Ctrl+C");
        var pasteCommand = new RoutedCommand();
        CommandBindings.Add(new CommandBinding(pasteCommand, (_, e) => e.Handled = true, (_, e) => { e.CanExecute = CanPaste(); e.Handled = true; }));
        var paste = Ui.AsyncIconButton(Ui.PasteGlyph, Paste, "Вставить · Ctrl+V"); paste.Command = pasteCommand; paste.CommandTarget = this;
        commands.Add("Paste", paste, () => { var item = Ui.AsyncMenu("Вставить", Paste, "Ctrl+V", Ui.PasteGlyph); item.IsEnabled = CanPaste(); return item; }, 85);
        Action("Rename", Ui.RenameGlyph, "Переименовать", Rename, nameof(FileBrowserViewModel.CanRename), 80, "F2");
        Action("Delete", Ui.DeleteGlyph, "Удалить окончательно", Delete, nameof(FileBrowserViewModel.HasSelection), 60, "Delete");
        Action("Upload", Ui.UploadGlyph, "Загрузить файлы…", Upload, nameof(FileBrowserViewModel.CanCreate), 10);
        Action("Download", Ui.DownloadGlyph, "Скачать…", Download, nameof(FileBrowserViewModel.HasSelection), 20);
        Action("Compare", "", "Сравнить с папкой Windows…", CompareWithLocal, nameof(FileBrowserViewModel.CanCreate), 5);
        Action("BatchRename", Ui.RenameGlyph, "Переименовать несколько…", BatchRenameAsync, nameof(FileBrowserViewModel.HasSelection), 6);
        Action("ViewText", "", "Просмотреть как текст", ViewTextAsync, nameof(FileBrowserViewModel.HasSelection), 7, "F3");
        commands.Add("Sort", Ui.FlyoutButton(Ui.SortGlyph, "Сортировать", BuildSortMenu), () => Submenu("Сортировать", Ui.SortGlyph, BuildSortMenu), 40);
        var viewButton = Ui.FlyoutButton(CommandGlyphs.View(mode), "Просмотреть", BuildViewMenu);
        viewModeRow = (Panel)viewButton.Content;
        commands.Add("View", viewButton, () => Submenu("Просмотреть", CommandGlyphs.View(mode), BuildViewMenu), 50);
        var border = new Border { Child = commands, DataContext = viewModel }; border.SetResourceReference(StyleProperty, "CommandBarStyle"); commandBar = border; border.PreviewKeyDown += HandleBarShortcut;
        var bottom = new Grid { DataContext = viewModel, Margin = new Thickness(14, 5, 14, 5), Height = 20 }; bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); bottom.ColumnDefinitions.Add(new ColumnDefinition()); status.SetResourceReference(TextBlock.FontSizeProperty, "UICaptionSize"); status.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); bottom.Children.Add(status); statusBar = bottom;
        message.Margin = new Thickness(16, 0, 0, 0); message.TextWrapping = TextWrapping.NoWrap; message.TextTrimming = TextTrimming.CharacterEllipsis; message.SetResourceReference(TextBlock.FontSizeProperty, "UICaptionSize"); message.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); message.SetBinding(ToolTipProperty, new Binding(nameof(FileBrowserViewModel.Message))); Grid.SetColumn(message, 1); bottom.Children.Add(message);
    }
    void BuildTable()
    {
        table.SetResourceReference(StyleProperty, "FileTableStyle");
        table.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(FileBrowserViewModel.VisibleEntries)));
        var template = new DataTemplate(); var row = new FrameworkElementFactory(typeof(DockPanel)); row.SetValue(DockPanel.LastChildFillProperty, true); var icon = new FrameworkElementFactory(typeof(ShellFileIcon)); icon.SetBinding(ShellFileIcon.EntryProperty, new Binding(nameof(FileItemViewModel.Entry))); icon.SetValue(Image.WidthProperty, CommandGlyphs.SmallFileSize); icon.SetValue(Image.HeightProperty, CommandGlyphs.SmallFileSize); icon.SetValue(MarginProperty, new Thickness(0, 0, 8, 0)); icon.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center); icon.SetValue(DockPanel.DockProperty, Dock.Left); row.AppendChild(icon); var name = new FrameworkElementFactory(typeof(FileNamePresenter)); name.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Left); row.AppendChild(name); template.VisualTree = row;
        table.Columns.Add(new DataGridTemplateColumn { Header = "Имя", CellTemplate = template, Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 220, SortMemberPath = "Name" });
        MainWindow.AddColumn(table, "Дата изменения", "Modified", 180, "{0:dd.MM.yyyy HH:mm}"); MainWindow.AddColumn(table, "Тип", "Type", 150); MainWindow.AddColumn(table, "Размер", "Size", 104); MainWindow.AddColumn(table, "Права", "Permissions", 90); MainWindow.AddColumn(table, "Владелец", "Owner", 90); MainWindow.AddColumn(table, "Группа", "Group", 90);
        foreach (var column in table.Columns.OfType<DataGridTextColumn>().Where(c => c.SortMemberPath is "Size" or "Owner" or "Group")) column.ElementStyle = new Style(typeof(TextBlock)) { Setters = { new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right) } };
        foreach (var c in table.Columns)
        {
            var s = App.Store.Settings.Columns.FirstOrDefault(x => x.Key == c.SortMemberPath); if (s is null) { if (table.Columns.IndexOf(c) > 3) c.Visibility = Visibility.Collapsed; continue; } c.Width = Math.Max(45, s.Width); c.Visibility = s.Visible ? Visibility.Visible : Visibility.Collapsed;
        }
        foreach (var s in App.Store.Settings.Columns.OrderBy(x => x.Order)) { var c = table.Columns.FirstOrDefault(c => c.SortMemberPath == s.Key); if (c is not null) c.DisplayIndex = Math.Clamp(s.Order, 0, table.Columns.Count - 1); }
        table.ColumnReordered += (_, _) => SaveColumns(); table.PreviewMouseLeftButtonUp += (_, _) => SaveColumns();
        table.Sorting += (_, e) => { e.Handled = true; var key = e.Column.SortMemberPath; descending = key == sortKey && !descending; sortKey = key; foreach (var c in table.Columns) c.SortDirection = c == e.Column ? descending ? ListSortDirection.Descending : ListSortDirection.Ascending : null; ApplyItems(); };
        table.SelectionChanged += (_, _) => SelectionChanged(); table.MouseDoubleClick += async (_, e) => { if (FindAncestor<TextBox>(e.OriginalSource as DependencyObject) is null && IsFileObjectHit(e.OriginalSource as DependencyObject) && FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is not null) await Ui.Guard(Open); };
        var rowStyle = new Style(typeof(DataGridRow), (Style)FindResource(typeof(DataGridRow))); rowStyle.Setters.Add(new Setter(OpacityProperty, new Binding("Opacity"))); table.RowStyle = rowStyle;
        ConfigureDrag(table); ConfigureRightClick(table); ConfigureLasso(table); Ui.AttachContextMenu(table, () => BuildContextMenu(Selected().Count > 0));
    }
    void BuildTiles()
    {
        tiles.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(FileBrowserViewModel.VisibleEntries)));
        tiles.SelectionChanged += (_, _) => SelectionChanged(); tiles.MouseDoubleClick += async (_, e) => { if (FindAncestor<TextBox>(e.OriginalSource as DependencyObject) is null && FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is not null) await Ui.Guard(Open); }; ConfigureDrag(tiles); ConfigureRightClick(tiles); ConfigureLasso(tiles); Ui.AttachContextMenu(tiles, () => BuildContextMenu(Selected().Count > 0));
    }
    void ConfigureRightClick(Control control)
    {
        control.PreviewMouseRightButtonDown += (_, e) =>
        {
            var row = ItemAt(e.OriginalSource as DependencyObject);
            if (control is DataGrid grid) { if (row is null) grid.UnselectAll(); else if (!grid.SelectedItems.Contains(row)) { grid.UnselectAll(); grid.SelectedItems.Add(row); } else if (grid.SelectedItems.Count > 1) e.Handled = true; }
            else if (control is ListBox list) { if (row is null) list.UnselectAll(); else if (!list.SelectedItems.Contains(row)) { list.UnselectAll(); list.SelectedItems.Add(row); } else if (list.SelectedItems.Count > 1) e.Handled = true; }
        };
    }
    void SaveColumns()
    {
        App.Store.Settings.Columns = table.Columns.Select(c => new ColumnSetting { Key = c.SortMemberPath, Width = c.ActualWidth, Order = c.DisplayIndex, Visible = c.Visibility == Visibility.Visible }).ToList(); _ = App.Store.SaveAsync();
    }
    void UpdateBreadcrumbs()
    {
        breadcrumbs.Children.Clear(); breadcrumbs.Children.Add(Ui.AsyncCommandButton(Ui.FolderGlyph, "/", () => NavigateAsync("/"), "Корень")); var path = "";
        foreach (var part in CurrentPath.Split('/', StringSplitOptions.RemoveEmptyEntries)) { path += "/" + part; var target = path; var separator = Ui.Icon("\uE76C", 10, false); separator.Margin = new Thickness(2, 0, 2, 0); breadcrumbs.Children.Add(separator); var b = Ui.AsyncButton(part, () => NavigateAsync(target)); b.SetResourceReference(StyleProperty, "CommandButtonStyle"); b.Padding = new Thickness(7, 3, 7, 3); breadcrumbs.Children.Add(b); }
        if (address.Visibility != Visibility.Visible) address.Text = CurrentPath;
    }
    internal bool IsEditingAddress => address.Visibility == Visibility.Visible;
    internal void EditAddress()
    {
        if (address.Visibility != Visibility.Visible) { address.Text = CurrentPath; address.Visibility = Visibility.Visible; }
        // The TextBox becomes focusable only after it is visible in layout; focus now and once more
        // at input priority so a click on the crumbs never leaves the caret elsewhere.
        void Activate() { if (address.Visibility != Visibility.Visible) return; address.Focus(); Keyboard.Focus(address); address.SelectAll(); }
        UpdateLayout(); Activate();
        if (!address.IsKeyboardFocused) Dispatcher.BeginInvoke(DispatcherPriority.Input, Activate);
    }
    /// <summary>Ctrl+L / Alt+D / Ctrl+F from the address and command bars, which live outside this view's tree.</summary>
    internal void HandleBarShortcut(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers; var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (mods == ModifierKeys.Control && key == Key.L || mods == ModifierKeys.Alt && key == Key.D) { e.Handled = true; EditAddress(); }
        else if (mods == ModifierKeys.Control && key == Key.F && !ReferenceEquals(e.OriginalSource, search)) { e.Handled = true; search.Focus(); search.SelectAll(); }
    }
    internal async Task SubmitAddressAsync()
    {
        if (addressPending) return;
        var attempt = ++addressRequest;
        addressPending = true; address.IsReadOnly = true;
        try
        {
            var success = await Navigate(address.Text, true);
            if (attempt != addressRequest) return;
            if (success) { address.Visibility = Visibility.Collapsed; FocusFileList(); }
            else if (!disposed && address.Visibility == Visibility.Visible) { address.ToolTip = viewModel.Message; Ui.SetValidationMessage(address, viewModel.Message); address.Focus(); }
        }
        finally { if (attempt == addressRequest) { addressPending = false; address.IsReadOnly = false; } }
    }
    void CancelAddress() { addressRequest++; addressPending = false; address.IsReadOnly = false; navigationCts.Cancel(); address.Text = CurrentPath; address.ToolTip = null; Ui.SetValidationMessage(address, ""); address.Visibility = Visibility.Collapsed; FocusFileList(); }
    void FocusFileList() { if (!IsLoaded || !IsVisible) return; if (mode == "Details") table.Focus(); else tiles.Focus(); }
    public async Task NavigateAsync(string path) { FileNavigationRequested?.Invoke(); await Navigate(path, true); }
    public Task InitializeAsync(string path, CancellationToken ct) => Navigate(path, true, requestToken: ct, throwOnFailure: true);
    async Task<bool> Navigate(string path, bool addHistory, BrowserHistoryItem? restore = null, int? targetHistoryIndex = null, CancellationToken requestToken = default, bool throwOnFailure = false)
    {
        EndLasso();
        CancelDragFeedback();
        recursiveSearchCts?.Cancel();
        thumbnailCts.Cancel(); thumbnailCts.Dispose(); thumbnailCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, session.Lifetime);
        if (!operations.Available) { viewModel.Message = "SFTP-подсистема недоступна."; if (throwOnFailure) throw new InvalidOperationException(viewModel.Message); return false; }
        navigationCts.Cancel(); navigationCts.Dispose(); navigationCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, session.Lifetime, requestToken); var ct = navigationCts.Token; var version = viewModel.BeginNavigation();
        var oldSelected = viewModel.SelectedPaths.ToList(); var oldOffset = GetScrollOffset();
        try
        {
            var normalized = await operations.ResolvePathAsync(path.StartsWith('/') || path.StartsWith('~') ? path : PosixPath.Combine(string.IsNullOrEmpty(CurrentPath) ? session.Files.HomeDirectory : CurrentPath, path), ct);
            var entries = await operations.ListAsync(normalized, ct); if (version != generation || ct.IsCancellationRequested) { if (throwOnFailure) throw new OperationCanceledException("Начальная навигация отменена или заменена новым запросом.", ct); return false; }
            if (CurrentPath != normalized) CancelNameEdit();
            if (!viewModel.CommitNavigation(version, normalized, entries, addHistory, oldSelected, oldOffset, targetHistoryIndex)) { if (throwOnFailure) throw new OperationCanceledException("Начальная навигация заменена новым запросом.", ct); return false; }
            ApplyItems(); RestoreSelection(viewModel.SelectedPaths.ToList());
            if (restore is not null) _ = Dispatcher.BeginInvoke(() => FindVisualChild<ScrollViewer>(mode == "Details" ? table : tiles)?.ScrollToVerticalOffset(restore.Offset));
            UpdateBreadcrumbs(); address.Text = normalized; address.ToolTip = null; Ui.SetValidationMessage(address, ""); address.Visibility = Visibility.Collapsed; UpdateCurrentTree();
            _ = SyncTree(normalized, version); UpdateStatus(); NavigationCommitted?.Invoke(); return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { viewModel.FailNavigation(version, path, "Операция отменена."); if (throwOnFailure) throw; }
        catch (Exception ex) { viewModel.FailNavigation(version, path, ErrorMessages.Describe(ex)); if (throwOnFailure) throw; }
        return false;
    }
    async Task Back() { if (viewModel.HistoryTarget(-1) is { } target) await Navigate(target.Item.Path, false, target.Item, target.Index); }
    async Task Forward() { if (viewModel.HistoryTarget(1) is { } target) await Navigate(target.Item.Path, false, target.Item, target.Index); }
    Task Up() => CurrentPath.Length > 0 ? NavigateAsync(PosixPath.Parent(CurrentPath)) : Task.CompletedTask;
    public Task RefreshAsync()
    {
        imageCache.Clear(); cacheBytes = 0;
        void Invalidate(ItemsControl parent) { foreach (var n in parent.Items.OfType<TreeViewItem>()) { if (pins.Contains(n)) continue; if ((n.Tag as string) == CurrentPath) { n.Items.Clear(); n.Items.Add(new TreeViewItem { Header = "Загрузка…", IsEnabled = false }); if (n.IsExpanded) _ = LoadNode(n); return; } Invalidate(n); } } Invalidate(tree);
        return CurrentPath.Length > 0 ? Navigate(CurrentPath, false) : Task.CompletedTask;
    }
    async Task SyncTree(string path, long version)
    {
        try
        {
            var node = tree.Items.OfType<TreeViewItem>().FirstOrDefault(n => n.Tag is string root && root == "/" && !pins.Contains(n)); if (node is null) return; var full = "";
            foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                await LoadNode(node); if (version != generation || disposed) return; node.IsExpanded = true; full += "/" + part; var next = node.Items.OfType<TreeViewItem>().FirstOrDefault(x => (x.Tag as string) == full); if (next is null) return; node = next;
            }
            if (version == generation && !disposed) { syncingTree = true; try { node.IsSelected = true; UpdateCurrentTree(); } finally { syncingTree = false; } }
        }
        catch (OperationCanceledException) { }
    }
    void UpdateCurrentTree()
    {
        void Visit(ItemsControl parent)
        {
            foreach (var node in parent.Items.OfType<TreeViewItem>()) { Ui.SetIsCurrentFolder(node, node.Tag is string path && path == CurrentPath); Visit(node); }
        }
        Visit(tree);
    }
    double GetScrollOffset() => FindVisualChild<ScrollViewer>(mode == "Details" ? table : tiles)?.VerticalOffset ?? 0;
    public object CaptureContext() => viewModel.Capture(GetScrollOffset());
    public void RestoreContext(object? context)
    {
        if (context is not BrowserContext saved || !viewModel.Restore(saved)) return; SetMode(saved.Mode, false); RestoreSelection(saved.Selection); _ = Dispatcher.BeginInvoke(() => FindVisualChild<ScrollViewer>(mode == "Details" ? table : tiles)?.ScrollToVerticalOffset(saved.Offset));
    }
    /// <summary>Header arrows, the "Сортировка" menu and the saved preference describe one order.</summary>
    void SyncSortState()
    {
        foreach (var column in table.Columns) column.SortDirection = column.SortMemberPath == sortKey ? descending ? ListSortDirection.Descending : ListSortDirection.Ascending : null;
        var settings = App.Store.Settings;
        if (settings.FileSortKey != sortKey || settings.FileSortDescending != descending) { settings.FileSortKey = sortKey; settings.FileSortDescending = descending; _ = SaveSortAsync(); }
    }
    static async Task SaveSortAsync()
    {
        try { await App.Store.SaveAsync(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { await LocalDiagnostics.RecordAsync("files.sort.save.failed", error); }
    }
    void ApplyItems()
    {
        EndLasso();
        if (disposed) return; var selection = viewModel.SelectedPaths.ToList(); restoringSelection = true;
        try { viewModel.ApplyFilter(App.Store.Settings.ShowHidden, App.Store.Settings.ShowExtensions); RestoreSelection(selection); }
        finally { restoringSelection = false; }
        SyncSortState();
        if (nameEdit is { IsActive: true, IsBusy: false } edit && !viewModel.VisibleEntries.Contains(edit.Item)) CancelNameEdit();
        UpdateStatus(); _ = Ui.Guard(UpdatePreview);
    }
    void RestoreSelection(IEnumerable<string> paths)
    {
        var prior = restoringSelection; restoringSelection = true;
        try { var set = paths.ToHashSet(StringComparer.Ordinal); if (mode == "Details") table.UnselectAll(); else tiles.UnselectAll(); foreach (var r in viewModel.VisibleEntries) if (set.Contains(r.Entry.FullPath)) { if (mode == "Details") table.SelectedItems.Add(r); else tiles.SelectedItems.Add(r); } }
        finally { restoringSelection = prior; }
        if (!restoringSelection) UpdateStatus();
    }
    List<FileItemViewModel> Selected() => viewModel.SelectedEntries.ToList();
    void SelectionChanged() { if (restoringSelection) return; UpdateStatus(); _ = Ui.Guard(UpdatePreview); }
    void UpdateStatus()
    {
        if (!restoringSelection) viewModel.SetSelection((mode == "Details" ? table.SelectedItems : tiles.SelectedItems).OfType<FileItemViewModel>().Select(r => r.Entry.FullPath));
    }
    void SetMode(string value, bool save = true)
    {
        EndLasso();
        // The icon slot is the first child (a VectorIcon or glyph TextBlock); the label follows it.
        if (viewModeRow is { Children.Count: > 0 }) { viewModeRow.Children.RemoveAt(0); viewModeRow.Children.Insert(0, Ui.Icon(CommandGlyphs.View(value), 16, true)); }
        var selection = Selected().Select(r => r.Entry.FullPath).ToList(); mode = value; App.Store.Settings.ViewMode = mode; content.Content = mode == "Details" ? table : tiles;
        if (mode != "Details")
        {
            var width = mode switch { "Huge" => 180d, "Large" => 144d, "Medium" => 112d, "Small" => 180d, "List" => 250d, "Tile" => 310d, _ => 800d };
            var height = TileHeight(mode);
            var factory = new FrameworkElementFactory(mode is "List" or "Content" ? typeof(VirtualizingStackPanel) : typeof(VirtualizingWrapPanel)); if (mode is not "List" and not "Content") { factory.SetValue(VirtualizingWrapPanel.ItemWidthProperty, width); factory.SetValue(VirtualizingWrapPanel.ItemHeightProperty, height); } tiles.ItemsPanel = new ItemsPanelTemplate(factory);
            tiles.ItemTemplate = TileTemplate(mode); tiles.ItemContainerStyle = CreateFileItemContainerStyle(mode);
        }
        RestoreSelection(selection); UpdatePreviewVisibility(); if (nameEdit?.IsActive == true) RevealNameEditor(nameEdit.Item, false); if (save) _ = App.Store.SaveAsync();
    }
    static Style CreateFileItemContainerStyle(string view)
    {
        var style = new Style(typeof(ListBoxItem), (Style)Application.Current.FindResource(typeof(ListBoxItem))) { Setters = { new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch), new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Stretch), new Setter(FrameworkElement.MarginProperty, new Thickness(3)), new Setter(Control.PaddingProperty, new Thickness(5)), new Setter(OpacityProperty, new Binding(nameof(FileItemViewModel.Opacity))) } };
        if (view is "List" or "Content") style.Setters.Add(new Setter(MinHeightProperty, view == "List" ? 32d : 92d)); return style;
    }
    double TileHeight(string view)
    {
        double font = Math.Max(14, TryFindResource("UIFontSize") is double value ? value : FontSize);
        double baseline = view switch { "Huge" => 192, "Large" => 156, "Medium" => 126, "Small" => 38, "Tile" => 82, _ => 92 };
        if (view is "Huge" or "Large" or "Medium") return Math.Max(baseline, (view == "Huge" ? 120 : view == "Large" ? 88 : 56) + 29 + font * 2.7);
        return Math.Max(baseline, font * 1.35 + 16 + (view == "Tile" ? 28 : 0));
    }
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == FontSizeProperty && viewModel is not null && tiles is not null && mode != "Details" && FindVisualChild<VirtualizingWrapPanel>(tiles) is { } wrap) wrap.ItemHeight = TileHeight(mode);
    }
    DataTemplate TileTemplate(string view)
    {
        bool horizontal = view is "Small" or "List" or "Tile" or "Content"; var iconSize = view switch { "Huge" => 120d, "Large" => 88d, "Medium" => 56d, "Small" => 22d, "List" => 18d, _ => 48d };
        var template = new DataTemplate(); var panel = new FrameworkElementFactory(horizontal ? typeof(DockPanel) : typeof(StackPanel)); if (horizontal) panel.SetValue(DockPanel.LastChildFillProperty, true);
        var iconHost = new FrameworkElementFactory(typeof(Grid)); iconHost.SetValue(FrameworkElement.WidthProperty, iconSize); iconHost.SetValue(FrameworkElement.HeightProperty, iconSize); iconHost.SetValue(FrameworkElement.MarginProperty, horizontal ? new Thickness(0, 0, 12, 0) : new Thickness(0, 5, 0, 8));
        if (horizontal) iconHost.SetValue(DockPanel.DockProperty, Dock.Left);
        var icon = new FrameworkElementFactory(typeof(ShellFileIcon)); icon.SetBinding(ShellFileIcon.EntryProperty, new Binding(nameof(FileItemViewModel.Entry))); icon.SetValue(Image.StretchProperty, Stretch.Uniform); icon.SetValue(WidthProperty, iconSize); icon.SetValue(HeightProperty, iconSize); icon.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center); icon.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); icon.SetBinding(VisibilityProperty, new Binding("Thumbnail") { Converter = new IconVisibilityConverter() }); iconHost.AppendChild(icon);
        var image = new FrameworkElementFactory(typeof(Image)); image.SetBinding(Image.SourceProperty, new Binding("Thumbnail")); image.SetValue(Image.StretchProperty, Stretch.Uniform); image.AddHandler(FrameworkElement.LoadedEvent, new RoutedEventHandler(async (s, _) => { if (((FrameworkElement)s).DataContext is FileItemViewModel r && IsImage(r.Entry)) await Ui.Guard(() => LoadThumbnail(r)); })); iconHost.AppendChild(image); panel.AppendChild(iconHost);
        var texts = new FrameworkElementFactory(typeof(StackPanel)); texts.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        var name = new FrameworkElementFactory(typeof(FileNamePresenter)); name.SetValue(FileNamePresenter.WrapNameProperty, !horizontal); texts.AppendChild(name);
        if (view is "Tile" or "Content") { var details = new FrameworkElementFactory(typeof(TextBlock)); details.SetBinding(TextBlock.TextProperty, new Binding("TileDetails")); details.SetValue(TextBlock.FontSizeProperty, 12d); details.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); texts.AppendChild(details); }
        if (view == "Content") { var path = new FrameworkElementFactory(typeof(TextBlock)); path.SetBinding(TextBlock.TextProperty, new Binding("Entry.FullPath")); path.SetValue(TextBlock.FontSizeProperty, 12d); path.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); texts.AppendChild(path); }
        panel.AppendChild(texts); template.VisualTree = panel; return template;
    }
    ContextMenu BuildViewMenu()
    {
        var m = new ContextMenu(); foreach (var (key, label, glyph) in new[] { ("Huge", "Огромные значки", IconCatalog.ViewHuge), ("Large", "Крупные значки", IconCatalog.ViewLarge), ("Medium", "Обычные значки", IconCatalog.ViewMedium), ("Small", "Мелкие значки", IconCatalog.ViewSmall), ("List", "Список", IconCatalog.ViewList), ("Details", "Таблица", IconCatalog.ViewDetails), ("Tile", "Плитка", IconCatalog.ViewTiles), ("Content", "Содержимое", IconCatalog.ViewContent) }) { var item = Ui.Menu(label, () => SetMode(key), glyph: glyph); item.IsCheckable = true; item.IsChecked = mode == key; m.Items.Add(item); }
        m.Items.Add(new Separator()); void Toggle(string label, bool initial, Action<bool> action) { var i = new MenuItem { Header = label, IsCheckable = true, IsChecked = initial }; i.Click += (_, _) => { action(i.IsChecked); _ = App.Store.SaveAsync(); }; m.Items.Add(i); }
        Toggle("Скрытые элементы", App.Store.Settings.ShowHidden, x => { App.Store.Settings.ShowHidden = x; ApplyItems(); }); Toggle("Расширения имён файлов", App.Store.Settings.ShowExtensions, x => { App.Store.Settings.ShowExtensions = x; ApplyItems(); }); Toggle("Область сведений", App.Store.Settings.DetailsPane, x => { App.Store.Settings.DetailsPane = x; UpdatePreviewVisibility(); _ = Ui.Guard(UpdatePreview); }); Toggle("Область просмотра", App.Store.Settings.PreviewPane, x => { App.Store.Settings.PreviewPane = x; UpdatePreviewVisibility(); _ = Ui.Guard(UpdatePreview); });
        var columns = new MenuItem { Header = "Колонки таблицы", Icon = Ui.Icon(IconCatalog.ViewDetails, 16, false) }; foreach (var c in table.Columns) { var i = new MenuItem { Header = c.Header, IsCheckable = true, IsChecked = c.Visibility == Visibility.Visible, IsEnabled = c != table.Columns[0] }; i.Click += (_, _) => { c.Visibility = i.IsChecked ? Visibility.Visible : Visibility.Collapsed; SaveColumns(); }; columns.Items.Add(i); } m.Items.Add(columns); return m;
    }
    ContextMenu BuildSortMenu()
    {
        var m = new ContextMenu(); foreach (var (key, title) in new[] { ("Name", "Имя"), ("Modified", "Дата изменения"), ("Type", "Тип"), ("Size", "Размер") }) { var i = Ui.Menu(title, () => { sortKey = key; ApplyItems(); }, glyph: Ui.SortGlyph); i.IsCheckable = true; i.IsChecked = key == sortKey; m.Items.Add(i); }
        m.Items.Add(new Separator()); var desc = new MenuItem { Header = "По убыванию", IsCheckable = true, IsChecked = descending }; desc.Click += (_, _) => { descending = desc.IsChecked; ApplyItems(); }; m.Items.Add(desc); return m;
    }
    async Task RecursiveSearch()
    {
        if (string.IsNullOrEmpty(CurrentPath)) return; var query = search.Text.Trim(); if (query.Length == 0) query = Ui.Prompt("Поиск во вложенных папках", "Часть имени файла или папки")?.Trim() ?? ""; if (query.Length == 0) return;
        if (!Ui.Confirm($"Искать имена с «{query}» в папке {CurrentPath} и её вложенных папках?\n\nСервер: {session.Profile.Name}\nОграничения: 500 каталогов, 10 000 результатов, 60 секунд. Символические ссылки не обходятся. Отмена: Escape или меню поля поиска.")) return;
        recursiveSearchCts?.Cancel(); recursiveSearchCts?.Dispose(); recursiveSearchCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, session.Lifetime); recursiveSearchCts.CancelAfter(TimeSpan.FromSeconds(60)); var ct = recursiveSearchCts.Token; var version = generation; var scope = CurrentPath; var queue = new Queue<string>(); queue.Enqueue(scope); var results = new List<FileItemViewModel>(); var visited = new HashSet<string>(StringComparer.Ordinal); var errors = 0; bool interrupted = false;
        viewModel.SearchQuery = query; viewModel.Message = $"Поиск в {scope} · 0 каталогов · Escape — отмена";
        try
        {
            while (queue.Count > 0 && visited.Count < 500 && results.Count < 10_000)
            {
                ct.ThrowIfCancellationRequested(); var path = queue.Dequeue(); if (!visited.Add(path)) continue;
                IReadOnlyList<RemoteEntry> entries;
                try { entries = await session.Files.ListAsync(path, ct); } catch (OperationCanceledException) { throw; } catch { errors++; continue; }
                if (version != generation) return;
                foreach (var e in entries)
                {
                    if (!App.Store.Settings.ShowHidden && e.Name.StartsWith('.')) continue;
                    if (e.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)) { results.Add(new FileItemViewModel(e)); if (results.Count >= 10_000) break; }
                    if (e.IsDirectory && !e.IsSymbolicLink) queue.Enqueue(e.FullPath);
                }
                if (visited.Count % 8 == 0) { all = results.ToList(); ApplyItems(); viewModel.Message = $"Поиск в {scope} · каталогов: {visited.Count} · найдено: {results.Count} · Escape — отмена"; }
            }
        }
        catch (OperationCanceledException) { interrupted = true; }
        if (version != generation || disposed) return; all = results; ApplyItems(); viewModel.Message = $"{(interrupted ? "Поиск отменён или достигнут лимит времени" : queue.Count > 0 ? "Достигнут лимит поиска" : "Поиск завершён")} · область: {scope} · каталогов: {visited.Count} · найдено: {results.Count}" + (errors > 0 ? $" · недоступно папок: {errors}" : "") + ". F5 возвращает содержимое папки.";
    }
    ContextMenu BuildContextMenu(bool selected)
    {
        var m = new ContextMenu(); var chosen = Selected(); var first = chosen.FirstOrDefault()?.Entry;
        if (selected && first is not null)
        {
            var open = Ui.AsyncMenu("Открыть", Open, "Enter", Ui.FolderGlyph); open.IsEnabled = chosen.Count == 1 && (first.IsDirectory || first.IsSymbolicLink || first.IsRegularFile); open.ToolTip = open.IsEnabled ? "Открыть папку или встроенный просмотр" : "Специальные Linux-объекты не открываются как документы"; m.Items.Add(open);
            var newTab = Ui.AsyncMenu("Открыть в новой вкладке", () => main.OpenServer(session.Profile, first.FullPath), glyph: Ui.NewTabGlyph); newTab.IsEnabled = chosen.Count == 1 && first.IsDirectory && !first.IsSymbolicLink; newTab.ToolTip = newTab.IsEnabled ? "Открыть каталог в независимой сессии" : "Доступно для одного обычного каталога"; m.Items.Add(newTab);
            var editText = Ui.AsyncMenu("Редактировать в Блокноте", EditSelectedText, glyph: "\uE70F"); editText.IsEnabled = chosen.Count == 1 && (first.IsSymbolicLink || first.IsRegularFile && DocumentText.CandidateName(first.Name)); m.Items.Add(editText);
            var previewText = Ui.AsyncMenu("Просмотр", () => OpenFileContent(true), glyph: Ui.SearchGlyph); previewText.IsEnabled = chosen.Count == 1 && (first.IsRegularFile || first.IsSymbolicLink); m.Items.Add(previewText);
            m.Items.Add(Ui.AsyncMenu(first.IsDirectory ? "Открыть в терминале" : "Открыть родительскую папку в терминале", () => OpenTerminal(first.IsDirectory && !first.IsSymbolicLink ? first.FullPath : PosixPath.Parent(first.FullPath)), glyph: Ui.TerminalGlyph));
            var strip = new FileContextCommandStrip();
            strip.Add(Ui.CutGlyph, "Вырезать · Ctrl+X", () => CopySelection(true), viewModel.HasSelection);
            strip.Add(Ui.CopyGlyph, "Копировать · Ctrl+C", () => CopySelection(false), viewModel.HasSelection);
            var pasteTarget = chosen.Count == 1 && first.IsDirectory && !first.IsSymbolicLink ? first.FullPath : CurrentPath;
            strip.Add(Ui.PasteGlyph, "Вставить в «" + pasteTarget + "» · Ctrl+V", () => PasteTo(pasteTarget), CanPaste(pasteTarget));
            strip.Add(Ui.RenameGlyph, "Переименовать · F2", Rename, viewModel.CanRename);
            strip.Add(Ui.DeleteGlyph, "Удалить окончательно… · Delete", Delete, viewModel.HasSelection);
            m.Items.Insert(0, strip); m.Items.Insert(1, new Separator());
            m.Items.Add(new Separator()); m.Items.Add(Ui.AsyncMenu("Скачать…", Download, glyph: Ui.DownloadGlyph));
            m.Items.Add(Ui.Menu("Копировать путь", () => Clipboard.SetText(string.Join(Environment.NewLine, Selected().Select(r => r.Entry.FullPath))), glyph: Ui.CopyGlyph)); var properties = Ui.AsyncMenu("Свойства", Properties, "Alt+Enter", Ui.PropertiesGlyph); properties.IsEnabled = chosen.Count == 1; properties.ToolTip = "Сведения об одном объекте"; m.Items.Add(properties);
        }
        else
        {
            m.Items.Add(Ui.AsyncMenu("Создать папку", () => Create(true), "Ctrl+Shift+N", Ui.FolderGlyph)); m.Items.Add(Ui.AsyncMenu("Создать файл", () => Create(false), glyph: Ui.FileGlyph)); m.Items.Add(Ui.AsyncMenu("Обновить", RefreshAsync, "F5", Ui.RefreshGlyph)); m.Items.Add(Ui.AsyncMenu("Загрузить файлы…", Upload, glyph: Ui.UploadGlyph)); m.Items.Add(Ui.AsyncMenu("Загрузить папку…", UploadFolder, glyph: Ui.UploadGlyph)); m.Items.Add(Ui.AsyncMenu("Поиск во вложенных папках…", RecursiveSearch, glyph: Ui.SearchGlyph)); m.Items.Add(Ui.AsyncMenu("Открыть папку в терминале", () => OpenTerminal(CurrentPath), glyph: Ui.TerminalGlyph));
        }
        if (!selected) { var paste = Ui.AsyncMenu("Вставить", Paste, "Ctrl+V", Ui.PasteGlyph); paste.IsEnabled = CanPaste(); m.Items.Add(paste); }
        var undo = Ui.AsyncMenu(session.Files.UndoDescription ?? "Отменить переименование или перемещение", Undo, "Ctrl+Z", Ui.UndoGlyph); undo.IsEnabled = viewModel.CanCreate && session.Files.CanUndo; m.Items.Add(undo);
        m.Items.Add(new Separator());
        var pinPath = chosen.Count == 1 && first?.IsDirectory == true && !first.IsSymbolicLink ? first.FullPath : CurrentPath;
        var pin = Ui.AsyncMenu((session.Profile.Bookmarks.Contains(pinPath) ? "Открепить" : "Закрепить") + (chosen.Count > 1 ? " текущую папку" : " папку"), () => TogglePin(pinPath), glyph: Ui.PinGlyph); pin.IsEnabled = !string.IsNullOrEmpty(pinPath); m.Items.Add(pin);
        if (!selected) foreach (var item in m.Items.OfType<MenuItem>()) { if (item != undo && item != pin) item.IsEnabled &= viewModel.CanCreate; }
        else foreach (var item in m.Items.OfType<MenuItem>()) { if (item != pin) item.IsEnabled &= viewModel.CanCreate; }
        return m;
    }
    async Task Open()
    {
        await OpenFileContent(false);
    }
    async Task EditSelectedText()
    {
        var chosen = Selected(); if (chosen.Count != 1) return; var entry = chosen[0].Entry;
        if (entry.IsSymbolicLink)
        {
            if (!Ui.Confirm($"Редактировать согласованную цель ссылки «{entry.Name}»?\n\nСама ссылка останется ссылкой.")) return;
            var target = await session.Files.FollowLinkAsync(entry.FullPath, lifetime.Token);
            if (!target.IsRegularFile || !DocumentText.CandidateName(target.Name)) throw new IOException("Цель ссылки не является обычным текстовым документом.");
        }
        await main.EditRemoteTextAsync(session, entry, entry.IsSymbolicLink);
    }
    async Task OpenFileContent(bool previewOnly)
    {
        if (!viewModel.HasSelection) return;
        var row = Selected().FirstOrDefault(); if (row is null) return;
        var entry = row.Entry;
        if (entry.IsSymbolicLink) { if (!Ui.Confirm($"Открыть цель символической ссылки «{entry.Name}»?\n\n{entry.LinkTarget ?? "Цель будет определена сервером"}")) return; entry = await session.Files.FollowLinkAsync(entry.FullPath, lifetime.Token); }
        if (entry.IsDirectory) { await NavigateAsync(entry.FullPath); return; }
        if (IsImage(entry)) { await ShowImage(entry); return; }
        if (!entry.IsRegularFile) throw new InvalidOperationException("Сокеты, устройства и FIFO нельзя открывать как обычные файлы.");
        if (!previewOnly && DocumentText.CandidateName(entry.Name)) { await main.EditRemoteTextAsync(session, row.Entry, row.Entry.IsSymbolicLink); return; }
        if (previewOnly && entry.Length <= 256 * 1024)
        {
            var data = await session.Files.ReadBytesAsync(entry.FullPath, 256 * 1024, lifetime.Token);
            string? encoding = null; string text; try { text = DocumentText.Decode(data).Text; } catch (IOException) { encoding = MainWindow.ChooseDocumentEncoding(); if (encoding is null) return; text = DocumentText.Decode(data, encoding).Text; }
            var w = new Window { Title = entry.Name + " — только просмотр", Width = 850, Height = 600, Owner = main, WindowStartupLocation = WindowStartupLocation.CenterOwner }; w.Content = new TextBox { Text = text, IsReadOnly = true, FontFamily = new FontFamily("Consolas"), AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; TrackWindow(w); w.Show(); return;
        }
        if (Ui.Confirm("Для этого типа нет встроенного просмотра. Скачать выбранный файл?")) { if (entry.FullPath == row.Entry.FullPath) await Download(); else { var f = new OpenFolderDialog { Title = "Скачать цель ссылки" }; if (f.ShowDialog(main) == true) { var job = App.Transfers.Download(session, [entry.FullPath], f.FolderName); main.ShowTransfers(); await App.Transfers.WaitAsync(job, lifetime.Token); ReportTransfer(job); } } }
    }
    async Task Create(bool directory)
    {
        if (!viewModel.CanCreate || creating || nameEdit?.IsBusy == true) return;
        CancelNameEdit(); creating = true; viewModel.IsMutating = true; var parent = CurrentPath; var version = generation;
        viewModel.Message = directory ? "Создание папки…" : "Создание файла…";
        try
        {
            var path = await operations.CreateUniqueAsync(parent, directory, lifetime.Token);
            if (disposed || CurrentPath != parent || version != generation) return;
            await RefreshAsync();
            var row = all.FirstOrDefault(r => r.Entry.FullPath == path);
            if (row is null) { viewModel.Message = $"Создано: {path}. Обновите содержимое папки."; return; }
            // A successful create is never rolled back by Escape in the name editor.
            search.Text = ""; RestoreSelection([path]); BeginNameEdit(row);
        }
        catch (Exception ex) { if (!disposed) viewModel.Message = ErrorMessages.Describe(ex); }
        finally { creating = false; viewModel.IsMutating = false; }
    }
    Task Rename()
    {
        var selected = Selected(); if (selected.Count == 1 && viewModel.CanRename) BeginNameEdit(selected[0]);
        return Task.CompletedTask;
    }
    internal void BeginNameEdit(FileItemViewModel row)
    {
        if (nameEdit?.IsBusy == true || disposed) return;
        CancelNameEdit(); EndLasso(); dragStart = null;
        nameEdit = new FileNameEdit(row); row.Edit = nameEdit; viewModel.IsEditingName = true;
        viewModel.Message = "Enter — сохранить имя · Esc — отменить редактирование";
        RevealNameEditor(row, true);
    }
    void RevealNameEditor(FileItemViewModel row, bool selectBase)
    {
        if (mode == "Details") table.ScrollIntoView(row); else tiles.ScrollIntoView(row);
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            if (disposed || !IsLoaded || !IsVisible || row.Edit?.IsActive != true) return;
            FindVisualChildren<FileNamePresenter>(mode == "Details" ? table : tiles).FirstOrDefault(p => ReferenceEquals(p.DataContext, row))?.FocusEditor(selectBase);
        }));
    }
    internal void CancelNameEdit()
    {
        if (nameEdit is null || !nameEdit.Cancel()) return;
        nameEdit = null; viewModel.IsEditingName = false; viewModel.Message = ""; FocusFileList();
    }
    internal async Task CommitNameEditAsync()
    {
        var edit = nameEdit; if (edit is null || edit.IsBusy) return;
        var parent = CurrentPath;
        viewModel.IsMutating = true;
        viewModel.Message = "Переименование…";
        string? target;
        try { target = await edit.CommitAsync((source, destination) => operations.RenameAsync(source, destination, lifetime.Token)); }
        finally { viewModel.IsMutating = false; }
        if (disposed || !ReferenceEquals(nameEdit, edit)) return;
        if (target is null) { viewModel.Message = edit.Error; RevealNameEditor(edit.Item, false); return; }
        nameEdit = null; viewModel.IsEditingName = false;
        if (CurrentPath != parent) return;
        // The server has confirmed the rename. Retain that row and its selection even if the following listing fails.
        edit.Item.RenameConfirmed(target); viewModel.SetSelection([target]); ApplyItems();
        await RefreshAsync(); RestoreSelection([target]);
        var row = all.FirstOrDefault(r => r.Entry.FullPath == target);
        if (row is not null) { if (mode == "Details") table.ScrollIntoView(row); else tiles.ScrollIntoView(row); }
        FocusFileList();
    }
    async Task Delete()
    {
        if (!viewModel.HasSelection) return;
        var entries = Selected().Select(r => r.Entry).ToList(); if (entries.Count == 0) return;
        if (!Ui.Confirm($"Окончательно удалить {entries.Count} объектов на сервере «{session.Profile.Name}»?\n\nПапка: {CurrentPath}\n{string.Join("\n", entries.Take(6).Select(e => e.Name))}\n\nКорзины и восстановления нет. Ссылки удаляются без удаления их цели.", "Окончательное удаление на Linux")) return;
        var done = 0;
        try { foreach (var e in entries) { await session.Files.DeleteAsync(e.FullPath, lifetime.Token); done++; } }
        catch (Exception ex) { Ui.Error($"Удалено объектов: {done} из {entries.Count}.\n" + ErrorMessages.Describe(ex)); }
        await RefreshAsync();
    }
    async Task Undo() { if (!viewModel.CanCreate || !session.Files.CanUndo) return; await session.Files.UndoAsync(lifetime.Token); await RefreshAsync(); }
    async Task Upload()
    {
        if (!viewModel.CanCreate) return;
        var dialog = new OpenFileDialog { Multiselect = true, Title = "Загрузить файлы на " + session.Profile.Name }; if (dialog.ShowDialog(main) != true) return;
        var job = App.Transfers.Upload(session, dialog.FileNames, CurrentPath); main.ShowTransfers(); await WaitAndRefresh(job);
    }
    async Task UploadFolder()
    {
        if (!viewModel.CanCreate) return;
        var dialog = new OpenFolderDialog { Multiselect = true, Title = "Загрузить папки" }; if (dialog.ShowDialog(main) != true) return; var job = App.Transfers.Upload(session, dialog.FolderNames, CurrentPath); main.ShowTransfers(); await WaitAndRefresh(job);
    }
    async Task Download()
    {
        if (!viewModel.HasSelection) return;
        var entries = Selected(); if (entries.Count == 0) return; var dialog = new OpenFolderDialog { Title = "Папка для скачивания" }; if (dialog.ShowDialog(main) != true) return;
        var job = App.Transfers.Download(session, entries.Select(r => r.Entry.FullPath), dialog.FolderName); main.ShowTransfers(); await App.Transfers.WaitAsync(job, lifetime.Token); ReportTransfer(job);
    }
    async Task WaitAndRefresh(TransferJob job) { await App.Transfers.WaitAsync(job, lifetime.Token); ReportTransfer(job); await RefreshAsync(); }
    void ReportTransfer(TransferJob job) { if (job.State == TransferState.Failed) Ui.Error(job.Error ?? "Передача завершилась ошибкой."); }
    async Task OpenTerminal(string path)
    {
        var window = new Window { Title = session.Profile.Name + " — отдельный терминал · " + path, Width = 1050, Height = 650, Owner = main, WindowStartupLocation = WindowStartupLocation.CenterOwner }; var terminal = new TerminalView(session); window.Content = terminal; window.Closed += (_, _) => terminal.Dispose();
        var registration = session.Lifetime.Register(() => Dispatcher.BeginInvoke(() => { if (window.IsLoaded) window.Close(); })); window.Closed += (_, _) => registration.Dispose(); TrackWindow(window); window.Show(); await terminal.StartAsync(path);
    }
    async Task Properties()
    {
        if (!viewModel.HasSelection || Selected().Count != 1) return;
        var entry = Selected().FirstOrDefault()?.Entry; if (entry is null) return; entry = await session.Files.StatAsync(entry.FullPath, lifetime.Token);
        var w = new Window { Title = "Свойства: " + entry.Name, Width = 580, Height = 760, MinWidth = 420, MinHeight = 400, MaxHeight = 820, Owner = main, WindowStartupLocation = WindowStartupLocation.CenterOwner }; w.SetResourceReference(StyleProperty, "DialogWindowStyle"); var panel = new StackPanel { Margin = new Thickness(24) };
        var heading = Ui.Text("Свойства"); heading.SetResourceReference(StyleProperty, "DialogTitleStyle"); panel.Children.Add(heading);
        var text = Ui.Text($"{entry.Name}\n\nПуть: {entry.FullPath}\nТип: {entry.TypeName}\nРазмер: {entry.SizeText}\nИзменён (местное время): {TimeText(entry.LastWriteTimeUtc)}\nДоступ (местное время): {TimeText(entry.LastAccessTimeUtc)}\nUID: {IdText(entry.UserId)}   GID: {IdText(entry.GroupId)}" + (entry.IsSymbolicLink ? "\nЦель ссылки: " + (entry.LinkTarget ?? "Недоступно") : "")); text.TextWrapping = TextWrapping.Wrap; panel.Children.Add(text);
        var modeText = new TextBox { Text = entry.PermissionsText, Margin = new Thickness(0, 14, 0, 6), ToolTip = "POSIX-права в восьмеричной записи, например 0755" }; panel.Children.Add(Ui.Text("Права (восьмеричные)")); panel.Children.Add(modeText);
        var rwx = new UniformGrid { Columns = 3 }; var boxes = new List<CheckBox>(); foreach (var label in new[] { "Владелец: чтение", "Запись", "Выполнение", "Группа: чтение", "Запись", "Выполнение", "Остальные: чтение", "Запись", "Выполнение" }) { var b = new CheckBox { Content = label }; boxes.Add(b); rwx.Children.Add(b); } panel.Children.Add(rwx);
        // Special bits as their own boxes (setuid 4000, setgid 2000, sticky 1000), kept in sync with the octal field.
        var special = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) }; var specialBoxes = new List<(CheckBox Box, int Bit)>();
        foreach (var (bit, label) in Ownership.SpecialBits) { var b = new CheckBox { Content = label, Margin = new Thickness(0, 0, 16, 0), Name = "Mode" + label }; specialBoxes.Add((b, bit)); special.Children.Add(b); }
        panel.Children.Add(special);
        bool updating = false;
        void SyncChecks() { if (updating) return; if (!short.TryParse(ConvertOctal(modeText.Text), out var bits)) return; updating = true; for (var i = 0; i < 9; i++) boxes[i].IsChecked = (bits & (1 << (8 - i))) != 0; foreach (var (box, bit) in specialBoxes) box.IsChecked = (bits & bit) != 0; updating = false; }
        void FromChecks() { if (updating) return; updating = true; var bits = 0; for (var i = 0; i < 9; i++) if (boxes[i].IsChecked == true) bits |= 1 << (8 - i); foreach (var (box, bit) in specialBoxes) if (box.IsChecked == true) bits |= bit; modeText.Text = Convert.ToString(bits, 8).PadLeft(4, '0'); updating = false; }
        foreach (var box in boxes.Concat(specialBoxes.Select(s => s.Box))) box.Click += (_, _) => FromChecks(); modeText.TextChanged += (_, _) => SyncChecks(); SyncChecks();
        var uid = new TextBox { Text = entry.UserId.ToString(), MinWidth = 140 }; var gid = new TextBox { Text = entry.GroupId.ToString(), MinWidth = 140 };
        panel.Children.Add(Ui.Text("Владелец / группа: имя или число (UID / GID)")); panel.Children.Add(Ui.Row(uid, gid));
        var names = Ui.Text("", 12); names.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); names.Name = "OwnerNames"; panel.Children.Add(names);
        if (session.ShellAvailable) _ = Task.Run(async () =>
        {
            try { var (user, group) = Ownership.ParseNames((await session.ExecuteAsync(Ownership.NamesCommand(entry.UserId, entry.GroupId), lifetime.Token)).Output); await Dispatcher.InvokeAsync(() => names.Text = $"Сейчас: {user ?? "без имени"} ({entry.UserId}) / {group ?? "без имени"} ({entry.GroupId})"); }
            catch (Exception ex) when (ex is not OperationCanceledException) { await Dispatcher.InvokeAsync(() => names.Text = "Имена владельца не получены: " + ErrorMessages.Describe(ex)); }
        });
        // Names are resolved on the server (getent); a number is used as is.
        async Task<int> ResolveId(TextBox field, bool group)
        {
            var text = field.Text.Trim();
            if (int.TryParse(text, out var number)) return number >= 0 ? number : throw new ArgumentException("UID и GID должны быть неотрицательными.");
            if (!Ownership.IsValidName(text)) throw new ArgumentException($"«{text}» — не число и не допустимое имя {(group ? "группы" : "пользователя")}.");
            if (!session.ShellAvailable) throw new InvalidOperationException("Имя можно указать только при доступном SSH exec; введите число.");
            var found = (await session.ExecuteAsync(Ownership.IdCommand(text, group)!, lifetime.Token)).Output.Trim();
            return int.TryParse(found, out var id) ? id : throw new ArgumentException($"{(group ? "Группа" : "Пользователь")} «{text}» не найден(а) на сервере.");
        }
        async Task<bool> ConfirmRecursive(string what)
        {
            var count = session.ShellAvailable ? (await session.ExecuteAsync(Ownership.CountCommand(entry.FullPath), lifetime.Token)).Output.Trim() : "все";
            return Ui.Confirm($"Рекурсивно изменить {what} у {count} объектов в «{entry.FullPath}» (без перехода на другие файловые системы)?\n\nОтменить это действие нельзя.");
        }
        var recursive = new CheckBox { Content = "Рекурсивно изменить вложенные объекты (отдельное действие)", IsEnabled = entry.IsDirectory && !entry.IsSymbolicLink, Margin = new Thickness(0, 12, 0, 6) }; panel.Children.Add(recursive);
        var error = Ui.Text(""); error.SetResourceReference(StyleProperty, "ValidationMessageStyle"); bool propertyBusy = false;
        async Task ApplyProperty(Func<Task<bool>> action, Control field)
        {
            if (propertyBusy) return; propertyBusy = true; panel.IsEnabled = false; error.Text = "Выполнение…"; Ui.SetValidationMessage(field, "");
            try { error.Text = await action() ? "Изменения применены." : ""; }
            catch (Exception ex) { error.Text = ErrorMessages.Describe(ex); Ui.SetValidationMessage(field, error.Text); }
            finally { propertyBusy = false; panel.IsEnabled = true; if (Ui.GetHasValidationError(field) && w.IsVisible) field.Focus(); }
        }
        System.Windows.Automation.AutomationProperties.SetName(modeText, "Права в восьмеричной записи"); System.Windows.Automation.AutomationProperties.SetName(uid, "Владелец UID"); System.Windows.Automation.AutomationProperties.SetName(gid, "Группа GID");
        panel.Children.Add(Ui.AsyncButton("Применить права", () => ApplyProperty(async () => { var bits = Convert.ToInt16(modeText.Text, 8); if (bits < 0 || bits > 4095) throw new ArgumentException("Права должны быть 0000–7777."); if (recursive.IsChecked == true && !await ConfirmRecursive("права")) return false; await session.Files.SetPermissionsAsync(entry.FullPath, short.Parse(modeText.Text), recursive.IsChecked == true, lifetime.Token); await RefreshAsync(); return true; }, modeText)));
        panel.Children.Add(Ui.AsyncButton("Применить владельца / группу", () => ApplyProperty(async () => { var owner = await ResolveId(uid, false); var group = await ResolveId(gid, true); if (recursive.IsChecked == true && !await ConfirmRecursive("владельца и группу")) return false; await session.Files.SetOwnerAsync(entry.FullPath, owner, group, recursive.IsChecked == true, lifetime.Token); await RefreshAsync(); return true; }, uid)));
        panel.Children.Add(error);
        var sizeCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); var sizeLabel = Ui.Text(""); if (entry.IsDirectory) { panel.Children.Add(Ui.AsyncButton("Рассчитать размер дерева", async () => { sizeLabel.Text = "Подсчёт…"; sizeLabel.Text = Ui.Size(await session.Files.CalculateSizeAsync(entry.FullPath, sizeCts.Token)); })); panel.Children.Add(Ui.Button("Отменить подсчёт", () => sizeCts.Cancel())); panel.Children.Add(sizeLabel); }
        var note = Ui.Text("Доступ ограничен правами SSH-учётной записи. sudo в терминале не повышает права SFTP.", 12); note.TextWrapping = TextWrapping.Wrap; panel.Children.Add(new Expander { Header = "О доступе", Content = note, Margin = new Thickness(0, 12, 0, 0) }); var close = Ui.Button("Закрыть", w.Close); close.IsCancel = true; w.Content = Ui.DialogSurface(panel, close); w.Closed += (_, _) => { sizeCts.Cancel(); sizeCts.Dispose(); }; TrackWindow(w); w.Show();
    }
    static string ConvertOctal(string text) { try { return Convert.ToInt16(text, 8).ToString(); } catch { return ""; } }
    void UpdatePreviewVisibility()
    {
        var wasVisible = preview.Visibility == Visibility.Visible; preview.Width = Math.Clamp(ActualWidth * .3, 220, 320);
        preview.Visibility = (App.Store.Settings.DetailsPane || App.Store.Settings.PreviewPane) && (ActualWidth <= 0 || ActualWidth >= 560) ? Visibility.Visible : Visibility.Collapsed;
        if (!wasVisible && preview.Visibility == Visibility.Visible && !disposed) _ = Ui.Guard(UpdatePreview);
    }
    async Task UpdatePreview()
    {
        previewCts.Cancel(); previewCts.Dispose(); previewCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); var ct = previewCts.Token;
        if (preview.Visibility != Visibility.Visible) return; var p = new StackPanel(); preview.Child = p; var entry = Selected().FirstOrDefault()?.Entry;
        if (entry is null) { var hint = Ui.Text("Выберите файл, чтобы увидеть сведения и предварительный просмотр."); hint.TextWrapping = TextWrapping.Wrap; p.Children.Add(hint); return; }
        var title = Ui.Text(entry.Name, 18, true); title.TextWrapping = TextWrapping.Wrap; p.Children.Add(title);
        if (App.Store.Settings.PreviewPane && IsImage(entry))
        {
            var image = new Image { MaxHeight = 300, Margin = new Thickness(0, 18, 0, 12), Stretch = Stretch.Uniform }; p.Children.Add(image);
            try { image.Source = await ReadImage(entry, 800, ct); } catch (Exception ex) when (ex is not OperationCanceledException) { var error = Ui.Text(ErrorMessages.Describe(ex), 12); error.TextWrapping = TextWrapping.Wrap; p.Children.Add(error); }
            if (ct.IsCancellationRequested) return;
            p.Children.Add(Ui.AsyncButton("Открыть изображение", () => ShowImage(entry)));
        }
        if (App.Store.Settings.DetailsPane) { var details = Ui.Text($"\n{entry.TypeName}\n{entry.SizeText}\n\nИзменён:\n{TimeText(entry.LastWriteTimeUtc)}\n\nПрава: {entry.PermissionsText}\nUID: {IdText(entry.UserId)}\nGID: {IdText(entry.GroupId)}"); details.TextWrapping = TextWrapping.Wrap; p.Children.Add(details); }
    }
    static bool IsImage(RemoteEntry e) => e.IsRegularFile && !e.IsSymbolicLink && new[] { ".png", ".jpg", ".jpeg" }.Contains(Path.GetExtension(e.Name), StringComparer.OrdinalIgnoreCase);
    static string TimeText(DateTime time) => time == DateTime.MinValue ? "Недоступно" : time.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss");
    static string IdText(int value) => value < 0 ? "Недоступно" : value.ToString();
    async Task<ImageSource> ReadImage(RemoteEntry entry, int decodeWidth, CancellationToken ct)
    {
        if (entry.Length > App.Store.Settings.PreviewMaxBytes) throw new InvalidOperationException($"Изображение больше лимита {Ui.Size(App.Store.Settings.PreviewMaxBytes)}. Скачайте его отдельно.");
        var key = $"{session.Profile.Endpoint}|{session.Profile.Username}|{entry.FullPath}|{entry.Length}|{entry.LastWriteTimeUtc.Ticks}|{decodeWidth}";
        if (imageCache.TryGetValue(key, out var cached)) return cached;
        await imageSlots.WaitAsync(ct);
        try
        {
            if (imageCache.TryGetValue(key, out cached)) return cached;
            var bytes = await session.Files.ReadBytesAsync(entry.FullPath, App.Store.Settings.PreviewMaxBytes, ct);
            var after = await session.Files.StatAsync(entry.FullPath, ct); if (after.Length != entry.Length || after.LastWriteTimeUtc != entry.LastWriteTimeUtc) throw new IOException("Изображение изменилось на сервере. Обновите папку и повторите просмотр.");
            var bitmap = await Task.Run(() =>
            {
                using var metadataStream = new MemoryStream(bytes, false); var decoder = BitmapDecoder.Create(metadataStream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None); var frame = decoder.Frames[0];
                if ((long)frame.PixelWidth * frame.PixelHeight > App.Store.Settings.PreviewMaxPixels) throw new IOException("Слишком много пикселей для безопасного предпросмотра. Скачайте изображение отдельно.");
                using var stream = new MemoryStream(bytes, false); var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; if (decodeWidth > 0 && frame.PixelWidth > decodeWidth) image.DecodePixelWidth = decodeWidth; image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
            }, ct);
            ct.ThrowIfCancellationRequested(); var size = (long)bitmap.PixelWidth * bitmap.PixelHeight * 4; if (cacheBytes + size > App.Store.Settings.PreviewCacheBytes) { imageCache.Clear(); cacheBytes = 0; } if (size <= App.Store.Settings.PreviewCacheBytes) { imageCache[key] = bitmap; cacheBytes += size; } return bitmap;
        }
        finally { imageSlots.Release(); }
    }
    async Task LoadThumbnail(FileItemViewModel row) { if (row.Thumbnail is not null || row.LoadingThumbnail) return; row.LoadingThumbnail = true; var ct = thumbnailCts.Token; try { var image = await ReadImage(row.Entry, 256, ct); ct.ThrowIfCancellationRequested(); row.Thumbnail = image; row.Notify(); } catch { } finally { row.LoadingThumbnail = false; } }
    async Task ShowImage(RemoteEntry entry)
    {
        var images = (table.ItemsSource as IEnumerable<FileItemViewModel> ?? all).Where(x => IsImage(x.Entry)).Select(x => x.Entry).ToList(); if (images.All(x => x.FullPath != entry.FullPath)) images.Insert(0, entry); var index = Math.Max(0, images.FindIndex(x => x.FullPath == entry.FullPath));
        var w = new Window { Title = entry.Name, Width = 1000, Height = 750, Owner = main, WindowStartupLocation = WindowStartupLocation.CenterOwner }; var p = new DockPanel(); var image = new Image { Stretch = Stretch.Uniform }; var scroller = new ScrollViewer { Content = image, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; var info = Ui.Text("Загрузка…");
        var localCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); CancellationTokenSource? frameCts = null; long frameGeneration = 0; bool fit = true;
        void SizeImage() { if (image.Source is not BitmapSource b) return; if (fit) { image.Width = Math.Max(1, scroller.ViewportWidth); image.Height = Math.Max(1, scroller.ViewportHeight); image.Stretch = Stretch.Uniform; } else { var dpi = VisualTreeHelper.GetDpi(image); image.Width = b.PixelWidth / dpi.DpiScaleX; image.Height = b.PixelHeight / dpi.DpiScaleY; } }
        async Task Load() { var version = ++frameGeneration; frameCts?.Cancel(); frameCts?.Dispose(); frameCts = CancellationTokenSource.CreateLinkedTokenSource(localCts.Token); var ct = frameCts.Token; var e = images[index]; info.Text = e.Name + " · загрузка…"; try { var bitmap = await ReadImage(e, 0, ct); if (version != frameGeneration || ct.IsCancellationRequested) return; image.Source = bitmap; w.Title = e.Name + " — просмотр изображения"; info.Text = e.Name + " · " + e.SizeText; SizeImage(); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { } catch (Exception ex) { if (version == frameGeneration) { image.Source = null; w.Title = e.Name + " — ошибка просмотра"; info.Text = e.Name + ": " + ErrorMessages.Describe(ex); } } }
        scroller.SizeChanged += (_, _) => { if (fit) SizeImage(); };
        var controls = Ui.Row(Ui.AsyncIconButton(Ui.BackGlyph, async () => { index = (index - 1 + images.Count) % images.Count; await Load(); }, "Предыдущее изображение"), Ui.AsyncIconButton(Ui.ForwardGlyph, async () => { index = (index + 1) % images.Count; await Load(); }, "Следующее изображение"), Ui.Button("Вписать", () => { fit = true; SizeImage(); }), Ui.Button("100%", () => { fit = false; SizeImage(); }), Ui.IconButton(Ui.AddGlyph, () => { fit = false; image.Width = (double.IsNaN(image.Width) ? image.ActualWidth : image.Width) * 1.25; image.Height = (double.IsNaN(image.Height) ? image.ActualHeight : image.Height) * 1.25; }, "Увеличить"), Ui.IconButton("\uE738", () => { fit = false; image.Width = Math.Max(32, (double.IsNaN(image.Width) ? image.ActualWidth : image.Width) / 1.25); image.Height = Math.Max(32, (double.IsNaN(image.Height) ? image.ActualHeight : image.Height) / 1.25); }, "Уменьшить"), Ui.AsyncCommandButton(Ui.DownloadGlyph, "Скачать", async () => { var f = new OpenFolderDialog { Title = "Скачать изображение" }; if (f.ShowDialog(w) == true) { var job = App.Transfers.Download(session, [images[index].FullPath], f.FolderName); main.ShowTransfers(); await App.Transfers.WaitAsync(job, lifetime.Token); ReportTransfer(job); } })); DockPanel.SetDock(controls, Dock.Top); p.Children.Add(controls); info.Margin = new Thickness(14, 9, 14, 9); DockPanel.SetDock(info, Dock.Bottom); p.Children.Add(info); p.Children.Add(scroller); w.Content = p; w.Closed += (_, _) => { frameGeneration++; frameCts?.Cancel(); frameCts?.Dispose(); localCts.Cancel(); localCts.Dispose(); }; TrackWindow(w); w.Show(); await Load();
    }
    void HandleKey(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0; var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0; var alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0; var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (ctrl && key == Key.L || alt && key == Key.D) { e.Handled = true; EditAddress(); return; }
        if (ctrl && key == Key.F) { e.Handled = true; search.Focus(); search.SelectAll(); return; }
        if (key == Key.Escape)
        {
            recursiveSearchCts?.Cancel(); EndLasso(); CancelDragFeedback();
            if (nameEdit?.IsActive == true && (FindAncestor<FileNamePresenter>(e.OriginalSource as DependencyObject) is not null || FindAncestor<TextBox>(e.OriginalSource as DependencyObject) is null)) { CancelNameEdit(); e.Handled = true; return; }
        }
        if (FindAncestor<TextBox>(e.OriginalSource as DependencyObject) is not null || FindAncestor<PasswordBox>(e.OriginalSource as DependencyObject) is not null) return;
        if (navigation.IsKeyboardFocusWithin)
        {
            if (shift && key == Key.F10 || key == Key.Apps)
            {
                if (FindAncestor<Control>(e.OriginalSource as DependencyObject) is { ContextMenu: { } menu } owner) { Ui.OpenContextMenu(owner, menu); e.Handled = true; }
                return;
            }
            // A tree/pin keyboard action must not delete/open the stale table selection.
            if (!(alt && key is Key.Left or Key.Right or Key.Up) && key != Key.F5 && key != Key.Escape) return;
        }
        Func<Task>? action = null;
        if (key == Key.F5) action = RefreshAsync; else if (key == Key.F3) action = ViewTextAsync; else if (key == Key.F2) action = Rename; else if (key == Key.Delete) action = Delete; else if (key == Key.Enter && alt) action = Properties; else if (key == Key.Enter) action = Open; else if (key == Key.Left && alt) action = Back; else if (key == Key.Right && alt) action = Forward; else if (key == Key.Up && alt) action = Up; else if (ctrl && key == Key.C) action = () => CopySelection(false); else if (ctrl && key == Key.X) action = () => CopySelection(true); else if (ctrl && key == Key.V) action = Paste; else if (ctrl && key == Key.Z) action = Undo; else if (ctrl && shift && key == Key.N) action = () => Create(true);
        else if (ctrl && key == Key.A) { if (mode == "Details") table.SelectAll(); else tiles.SelectAll(); e.Handled = true; }
        else if (key == Key.Escape) { navigationCts.Cancel(); recursiveSearchCts?.Cancel(); viewModel.SearchQuery = ""; if (mode == "Details") table.UnselectAll(); else tiles.UnselectAll(); e.Handled = true; }
        else if (shift && key == Key.F10) { var menu = BuildContextMenu(Selected().Count > 0); Ui.OpenContextMenu(mode == "Details" ? table : tiles, menu); e.Handled = true; }
        if (action is not null) { e.Handled = true; _ = Ui.Guard(action); }
    }
    public void RouteGlobalKey(KeyEventArgs e) => HandleKey(this, e);
    public void SetAvailability(bool available) => viewModel.Available = available;
    internal Task ReceiveDropAsync(IDataObject data, DragDropKeyStates keys) => DropData(data, CurrentPath, keys);
    void RefreshPalette()
    {
        if (disposed) return; foreach (var row in viewModel.VisibleEntries) row.Notify();
        void Visit(ItemsControl parent) { foreach (var node in parent.Items.OfType<TreeViewItem>()) { if (node.Header is DockPanel header && header.Children.OfType<Image>().FirstOrDefault() is Image icon) icon.Source = FileSystemIcons.For(new RemoteEntry { IsDirectory = true }); Visit(node); } } Visit(tree);
    }
    static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject { while (node is not null) { if (node is T t) return t; node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node); } return null; }
    static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject { for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) { var child = VisualTreeHelper.GetChild(parent, i); if (child is T t) return t; var found = FindVisualChild<T>(child); if (found is not null) return found; } return null; }
    static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject { for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) { var child = VisualTreeHelper.GetChild(parent, i); if (child is T t) yield return t; else foreach (var nested in FindVisualChildren<T>(child)) yield return nested; } }
    void TrackWindow(Window window) { ownedWindows.Add(window); window.Closed += (_, _) => ownedWindows.Remove(window); }
    public void Dispose() { if (disposed) return; EndLasso(); CancelNameEdit(); disposed = true; Theme.Changed -= RefreshPalette; viewModel.InvalidateNavigation(); DisposeDragDrop(); foreach (var window in ownedWindows.ToArray()) if (window.IsLoaded) window.Close(); ownedWindows.Clear(); lifetime.Cancel(); navigationCts.Cancel(); recursiveSearchCts?.Cancel(); thumbnailCts.Cancel(); previewCts.Cancel(); imageCache.Clear(); lifetime.Dispose(); navigationCts.Dispose(); recursiveSearchCts?.Dispose(); thumbnailCts.Dispose(); previewCts.Dispose(); }
    sealed class SelectionAdorner(UIElement element) : Adorner(element)
    {
        Rect rectangle;
        public Rect Rectangle { get => rectangle; set { rectangle = value; InvalidateVisual(); } }
        protected override void OnRender(DrawingContext drawingContext) => drawingContext.DrawRectangle(new SolidColorBrush(Color.FromArgb(45, 0, 103, 192)), new Pen(Brushes.DodgerBlue, 1), rectangle);
        protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) => null;
    }
    sealed class IconVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is ImageSource ? Visibility.Collapsed : Visibility.Visible;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
