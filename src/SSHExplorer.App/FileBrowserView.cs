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
using Microsoft.Win32;
using SSHExplorer.Core;
using SSHExplorer.App.ViewModels;

namespace SSHExplorer.App;

public sealed class FileBrowserView : UserControl, IDisposable
{
    readonly ServerSession session; readonly MainWindow main;
    readonly DockPanel root = new(); readonly DataGrid table = new(); readonly ListBox tiles = new() { SelectionMode = SelectionMode.Extended };
    readonly ContentControl content = new(); readonly TextBox address = new(); readonly StackPanel breadcrumbs = new() { Orientation = Orientation.Horizontal };
    readonly TextBox search = new() { MinWidth = 180, ToolTip = "Поиск по имени в текущей папке" }; readonly TextBlock status = Ui.Text(""), message = Ui.Text("");
    readonly TreeView tree = new() { MinHeight = 100 }; readonly StackPanel navigation = new();
    readonly StackPanel pins = new(); readonly Border preview = new() { Width = 280, Padding = new Thickness(16), BorderThickness = new Thickness(1, 0, 0, 0) };
    readonly FileBrowserViewModel viewModel = new();
    CancellationTokenSource navigationCts = new(); CancellationTokenSource previewCts = new(); readonly CancellationTokenSource lifetime = new();
    readonly Dictionary<string, ImageSource> imageCache = []; long cacheBytes; readonly SemaphoreSlim imageSlots;
    readonly Dictionary<string, Task> treeLoads = []; bool syncingTree; Control? dropTarget; TreeViewItem? hoverNode; DateTime hoverSince;
    CancellationTokenSource? recursiveSearchCts; CancellationTokenSource thumbnailCts = new();
    readonly Dictionary<string, StringCollection> preparedFiles = [];
    readonly HashSet<Window> ownedWindows = [];
    static readonly Dictionary<string, ClipboardToken> remoteClipboardTokens = [];
    List<FileItemViewModel> all { get => viewModel.Entries; set => viewModel.SetEntries(value); }
    string mode { get => viewModel.ViewMode; set => viewModel.ViewMode = value; }
    string sortKey { get => viewModel.SortKey; set => viewModel.SortKey = value; }
    bool descending { get => viewModel.Descending; set => viewModel.Descending = value; }
    long generation => viewModel.NavigationGeneration;
    bool disposed, restoringSelection; Point? dragStart; bool preparingDrag;
    public string CurrentPath => viewModel.CurrentPath;
    public event Action? FileNavigationRequested;
    public UIElement Navigation => navigation;
    public FileBrowserView(ServerSession session, MainWindow main)
    {
        this.session = session; this.main = main; mode = App.Store.Settings.ViewMode; imageSlots = new SemaphoreSlim(Math.Clamp(App.Store.Settings.PreviewParallelism, 1, 4));
        DataContext = viewModel;
        status.SetBinding(TextBlock.TextProperty, new Binding(nameof(FileBrowserViewModel.StatusText)));
        message.SetBinding(TextBlock.TextProperty, new Binding(nameof(FileBrowserViewModel.Message)));
        SetResourceReference(BackgroundProperty, "Paper");
        BuildNavigation(); BuildAddress(); BuildCommands(); BuildTable(); BuildTiles();
        var lower = new Grid(); lower.ColumnDefinitions.Add(new ColumnDefinition()); lower.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); lower.Children.Add(content); Grid.SetColumn(preview, 1); lower.Children.Add(preview); preview.SetResourceReference(Border.BorderBrushProperty, "Edge"); root.Children.Add(lower);
        Content = root; SetMode(mode, false); PreviewKeyDown += HandleKey;
        PreviewMouseDown += (_, e) => { if (e.ChangedButton == MouseButton.XButton1) { e.Handled = true; _ = Ui.Guard(Back); } if (e.ChangedButton == MouseButton.XButton2) { e.Handled = true; _ = Ui.Guard(Forward); } };
        if (!session.SftpAvailable) viewModel.Message = "SFTP недоступен. Терминал и мониторинг остаются доступны при наличии shell.";
    }
    void BuildNavigation()
    {
        var home = Ui.AsyncButton("⌂  Домашняя папка", () => NavigateAsync("~")); home.HorizontalContentAlignment = HorizontalAlignment.Left; home.BorderThickness = new Thickness(0); navigation.Children.Add(home);
        var rootButton = Ui.AsyncButton("/   Корень", () => NavigateAsync("/")); rootButton.ToolTip = "Корень, доступный этой учётной записи (с учётом SFTP chroot)"; rootButton.HorizontalContentAlignment = HorizontalAlignment.Left; rootButton.BorderThickness = new Thickness(0); navigation.Children.Add(rootButton);
        navigation.Children.Add(pins); RefreshPins(); navigation.Children.Add(tree);
        var rootNode = Node("/", "/"); tree.Items.Add(rootNode);
        tree.SelectedItemChanged += async (_, e) => { if (!syncingTree && e.NewValue is TreeViewItem { Tag: string path } && path != CurrentPath) await Ui.Guard(() => NavigateAsync(path)); };
    }
    TreeViewItem Node(string name, string path)
    {
        var node = new TreeViewItem { Header = Ui.Row(Ui.Icon("\uE8B7", 16), Ui.Text(name)), Tag = path, ToolTip = path, AllowDrop = true }; node.Items.Add(new TreeViewItem { Header = "Загрузка…", IsEnabled = false });
        node.Expanded += async (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, node)) return;
            await LoadNode(node);
        };
        node.DragOver += (_, e) => DropOver(e, path, node);
        node.DragLeave += (_, _) => { dropTarget?.ClearValue(BackgroundProperty); dropTarget = null; node.ClearValue(BackgroundProperty); };
        node.Drop += async (_, e) => { e.Handled = true; dropTarget?.ClearValue(BackgroundProperty); dropTarget = null; node.ClearValue(BackgroundProperty); await Ui.Guard(() => DropData(e.Data, path, e.KeyStates)); };
        var menu = new ContextMenu(); menu.Items.Add(Ui.AsyncMenu("Открыть", () => NavigateAsync(path))); menu.Items.Add(Ui.AsyncMenu("Открыть в новой вкладке", () => main.OpenServer(session.Profile, path))); menu.Items.Add(Ui.AsyncMenu("Открыть в терминале", () => OpenTerminal(path))); menu.Items.Add(Ui.AsyncMenu("Закрепить / открепить", () => TogglePin(path))); menu.Items.Add(Ui.Menu("Копировать путь", () => Clipboard.SetText(path))); node.ContextMenu = menu; return node;
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
        pins.Children.Clear(); foreach (var path in session.Profile.Bookmarks)
        {
            var b = Ui.AsyncButton("★  " + (PosixPath.Name(path) is { Length: > 0 } n ? n : path), () => NavigateAsync(path)); b.HorizontalContentAlignment = HorizontalAlignment.Left; b.ToolTip = path; b.BorderThickness = new Thickness(0);
            var menu = new ContextMenu(); menu.Items.Add(Ui.AsyncMenu("Открепить", () => TogglePin(path))); b.ContextMenu = menu; pins.Children.Add(b);
        }
    }
    async Task TogglePin(string path) { if (!session.Profile.Bookmarks.Remove(path)) session.Profile.Bookmarks.Add(path); await App.Store.SaveAsync(); RefreshPins(); }
    void BuildAddress()
    {
        var bar = new Grid { Margin = new Thickness(10, 7, 12, 7) }; bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); bar.ColumnDefinitions.Add(new ColumnDefinition()); bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        var buttons = Ui.Row(Ui.AsyncButton("←", Back, "Назад · Alt+←"), Ui.AsyncButton("→", Forward, "Вперёд · Alt+→"), Ui.AsyncButton("↑", Up, "Вверх · Alt+↑"), Ui.AsyncButton("⟳", RefreshAsync, "Обновить · F5")); bar.Children.Add(buttons);
        var pathHost = new Grid { Margin = new Thickness(8, 0, 12, 0), MinHeight = 38 }; var pathBorder = new Border { CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 0, 8, 0), Child = new ScrollViewer { Content = breadcrumbs, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled } }; pathBorder.SetResourceReference(Border.BackgroundProperty, "Surface");
        pathBorder.MouseDown += (_, e) => { if (e.OriginalSource is Border or ScrollViewer) EditAddress(); }; pathHost.Children.Add(pathBorder);
        address.Visibility = Visibility.Collapsed; address.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await Ui.Guard(() => NavigateAsync(address.Text)); address.Visibility = Visibility.Collapsed; } if (e.Key == Key.Escape) { address.Visibility = Visibility.Collapsed; table.Focus(); e.Handled = true; } }; address.LostKeyboardFocus += (_, _) => address.Visibility = Visibility.Collapsed; pathHost.Children.Add(address); Grid.SetColumn(pathHost, 1); bar.Children.Add(pathHost);
        search.SetBinding(TextBox.TextProperty, new Binding(nameof(FileBrowserViewModel.SearchQuery)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        search.TextChanged += (_, _) => { viewModel.SearchQuery = search.Text; ApplyItems(); }; System.Windows.Automation.AutomationProperties.SetName(search, "Поиск в текущей папке");
        var searchMenu = new ContextMenu(); searchMenu.Items.Add(Ui.AsyncMenu("Искать во вложенных папках…", RecursiveSearch)); searchMenu.Items.Add(Ui.Menu("Отменить рекурсивный поиск", () => recursiveSearchCts?.Cancel())); searchMenu.Items.Add(Ui.AsyncMenu("Вернуться к содержимому папки", RefreshAsync)); search.ContextMenu = searchMenu;
        var searchHost = new Grid(); searchHost.Children.Add(search); var hint = Ui.Text("Поиск в этой папке", 13); hint.Margin = new Thickness(9, 0, 6, 0); hint.IsHitTestVisible = false; hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); searchHost.Children.Add(hint); search.TextChanged += (_, _) => hint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(searchHost, 2); bar.Children.Add(searchHost); DockPanel.SetDock(bar, Dock.Top); root.Children.Add(bar);
    }
    void BuildCommands()
    {
        var commands = new WrapPanel { Margin = new Thickness(10, 1, 10, 7) };
        var create = Ui.Button("＋  Создать ▾", () => { var m = new ContextMenu(); m.Items.Add(Ui.AsyncMenu("Папку", () => Create(true), "Ctrl+Shift+N")); m.Items.Add(Ui.AsyncMenu("Файл", () => Create(false))); m.IsOpen = true; }); commands.Children.Add(create); commands.Children.Add(Ui.Line());
        commands.Children.Add(Ui.AsyncButton("✂", () => CopySelection(true), "Вырезать · Ctrl+X")); commands.Children.Add(Ui.AsyncButton("⧉", () => CopySelection(false), "Копировать · Ctrl+C")); commands.Children.Add(Ui.AsyncButton("▣", Paste, "Вставить · Ctrl+V")); var rename = Ui.AsyncButton("", Rename, "Переименовать · F2"); rename.Content = Ui.Icon("\uE8AC", 18); commands.Children.Add(rename); var delete = Ui.AsyncButton("", Delete, "Окончательно удалить · Delete"); delete.Content = Ui.Icon("\uE74D", 18); commands.Children.Add(delete); commands.Children.Add(Ui.Line());
        commands.Children.Add(Ui.AsyncButton("↑ Загрузить", Upload)); commands.Children.Add(Ui.AsyncButton("↓ Скачать", Download));
        commands.Children.Add(Ui.Button("Сортировать ▾", SortMenu)); commands.Children.Add(Ui.Button("Просмотреть ▾", ViewMenu));
        var more = Ui.Button("•••", () => { var m = BuildContextMenu(false); m.IsOpen = true; }); commands.Children.Add(more);
        var border = new Border { Child = commands, BorderThickness = new Thickness(0, 0, 0, 1) }; border.SetResourceReference(Border.BorderBrushProperty, "Edge"); DockPanel.SetDock(border, Dock.Top); root.Children.Add(border);
        var bottom = new DockPanel { Margin = new Thickness(14, 7, 14, 7), MinHeight = 23 }; status.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); bottom.Children.Add(status); var transfers = Ui.Button("⇄ Передачи", main.ShowTransfers); transfers.MinHeight = 24; transfers.Padding = new Thickness(9, 2, 9, 2); transfers.HorizontalAlignment = HorizontalAlignment.Right; DockPanel.SetDock(transfers, Dock.Right); bottom.Children.Add(transfers); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        message.Margin = new Thickness(18, 8, 18, 8); message.TextWrapping = TextWrapping.Wrap; message.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); DockPanel.SetDock(message, Dock.Top); root.Children.Add(message);
    }
    void BuildTable()
    {
        table.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(FileBrowserViewModel.VisibleEntries)));
        var template = new DataTemplate(); var row = new FrameworkElementFactory(typeof(StackPanel)); row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal); var glyph = new FrameworkElementFactory(typeof(TextBlock)); glyph.SetBinding(TextBlock.TextProperty, new Binding("Glyph")); glyph.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets")); glyph.SetValue(TextBlock.FontSizeProperty, 19d); glyph.SetValue(TextBlock.MarginProperty, new Thickness(2, 0, 9, 0)); glyph.SetBinding(TextBlock.ForegroundProperty, new Binding("IconBrush")); row.AppendChild(glyph); var name = new FrameworkElementFactory(typeof(TextBlock)); name.SetBinding(TextBlock.TextProperty, new Binding("DisplayName")); name.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center); row.AppendChild(name); template.VisualTree = row;
        table.Columns.Add(new DataGridTemplateColumn { Header = "Имя", CellTemplate = template, Width = 300, SortMemberPath = "Name" });
        MainWindow.AddColumn(table, "Дата изменения", "Modified", 180, "{0:dd.MM.yyyy HH:mm}"); MainWindow.AddColumn(table, "Тип", "Type", 180); MainWindow.AddColumn(table, "Размер", "Size", 110); MainWindow.AddColumn(table, "Права", "Permissions", 90); MainWindow.AddColumn(table, "Владелец", "Owner", 90); MainWindow.AddColumn(table, "Группа", "Group", 90);
        foreach (var c in table.Columns)
        {
            var s = App.Store.Settings.Columns.FirstOrDefault(x => x.Key == c.SortMemberPath); if (s is null) { if (c.DisplayIndex > 3) c.Visibility = Visibility.Collapsed; continue; } c.Width = Math.Max(45, s.Width); c.Visibility = s.Visible ? Visibility.Visible : Visibility.Collapsed;
        }
        foreach (var s in App.Store.Settings.Columns.OrderBy(x => x.Order)) { var c = table.Columns.FirstOrDefault(c => c.SortMemberPath == s.Key); if (c is not null) c.DisplayIndex = Math.Clamp(s.Order, 0, table.Columns.Count - 1); }
        table.ColumnReordered += (_, _) => SaveColumns(); table.PreviewMouseLeftButtonUp += (_, _) => SaveColumns();
        table.Sorting += (_, e) => { e.Handled = true; var key = e.Column.SortMemberPath; descending = key == sortKey && !descending; sortKey = key; foreach (var c in table.Columns) c.SortDirection = c == e.Column ? descending ? ListSortDirection.Descending : ListSortDirection.Ascending : null; ApplyItems(); };
        table.SelectionChanged += (_, _) => SelectionChanged(); table.MouseDoubleClick += async (_, e) => { if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is not null) await Ui.Guard(Open); };
        var rowStyle = new Style(typeof(DataGridRow), (Style)FindResource(typeof(DataGridRow))); rowStyle.Setters.Add(new Setter(OpacityProperty, new Binding("Opacity"))); table.RowStyle = rowStyle;
        ConfigureDrag(table); ConfigureRightClick(table); table.ContextMenu = BuildContextMenu(false); table.ContextMenuOpening += (_, _) => table.ContextMenu = BuildContextMenu(Selected().Count > 0);
    }
    void BuildTiles()
    {
        tiles.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(FileBrowserViewModel.VisibleEntries)));
        tiles.SelectionChanged += (_, _) => SelectionChanged(); tiles.MouseDoubleClick += async (_, e) => { if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is not null) await Ui.Guard(Open); }; ConfigureDrag(tiles); ConfigureRightClick(tiles); ConfigureLasso(); tiles.ContextMenu = BuildContextMenu(false); tiles.ContextMenuOpening += (_, _) => tiles.ContextMenu = BuildContextMenu(Selected().Count > 0);
    }
    void ConfigureRightClick(Control control)
    {
        control.PreviewMouseRightButtonDown += (_, e) =>
        {
            var row = ItemAt(e.OriginalSource as DependencyObject);
            if (control is DataGrid grid) { if (row is null) grid.UnselectAll(); else if (!grid.SelectedItems.Contains(row)) { grid.UnselectAll(); grid.SelectedItems.Add(row); } }
            else if (control is ListBox list) { if (row is null) list.UnselectAll(); else if (!list.SelectedItems.Contains(row)) { list.UnselectAll(); list.SelectedItems.Add(row); } }
        };
    }
    void ConfigureLasso()
    {
        Point? start = null; SelectionAdorner? adorner = null; AdornerLayer? layer = null; HashSet<FileItemViewModel> baseline = [];
        void End() { if (adorner is not null) layer?.Remove(adorner); adorner = null; start = null; if (tiles.IsMouseCaptured) tiles.ReleaseMouseCapture(); }
        tiles.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is not null || FindAncestor<ScrollBar>(e.OriginalSource as DependencyObject) is not null) return;
            start = e.GetPosition(tiles); baseline = (Keyboard.Modifiers & ModifierKeys.Control) != 0 ? tiles.SelectedItems.OfType<FileItemViewModel>().ToHashSet() : [];
            if (baseline.Count == 0) tiles.UnselectAll(); layer = AdornerLayer.GetAdornerLayer(tiles); if (layer is null) return; adorner = new SelectionAdorner(tiles); layer.Add(adorner); tiles.CaptureMouse(); e.Handled = true;
        };
        tiles.PreviewMouseMove += (_, e) =>
        {
            if (start is not Point a || adorner is null) return; if (e.LeftButton != MouseButtonState.Pressed) { End(); return; }
            var b = e.GetPosition(tiles); var rect = new Rect(a, b); adorner.Rectangle = rect;
            var selected = new HashSet<FileItemViewModel>(baseline);
            foreach (var container in FindVisualChildren<ListBoxItem>(tiles))
            {
                if (container.DataContext is not FileItemViewModel row) continue;
                var bounds = container.TransformToAncestor(tiles).TransformBounds(new Rect(container.RenderSize)); if (rect.IntersectsWith(bounds)) selected.Add(row);
            }
            foreach (var row in tiles.SelectedItems.OfType<FileItemViewModel>().Where(x => !selected.Contains(x)).ToArray()) tiles.SelectedItems.Remove(row);
            foreach (var row in selected) if (!tiles.SelectedItems.Contains(row)) tiles.SelectedItems.Add(row);
            e.Handled = true;
        };
        tiles.PreviewMouseLeftButtonUp += (_, e) => { if (start is not null) { End(); e.Handled = true; } };
        tiles.LostMouseCapture += (_, _) => { if (adorner is not null) layer?.Remove(adorner); adorner = null; start = null; };
    }
    void SaveColumns()
    {
        App.Store.Settings.Columns = table.Columns.Select(c => new ColumnSetting { Key = c.SortMemberPath, Width = c.ActualWidth, Order = c.DisplayIndex, Visible = c.Visibility == Visibility.Visible }).ToList(); _ = App.Store.SaveAsync();
    }
    void UpdateBreadcrumbs()
    {
        breadcrumbs.Children.Clear(); breadcrumbs.Children.Add(Ui.AsyncButton("/", () => NavigateAsync("/"), "Корень")); var path = "";
        foreach (var part in CurrentPath.Split('/', StringSplitOptions.RemoveEmptyEntries)) { path += "/" + part; var target = path; var separator = Ui.Text("›"); separator.Margin = new Thickness(3, 0, 3, 0); breadcrumbs.Children.Add(separator); var b = Ui.AsyncButton(part, () => NavigateAsync(target)); b.BorderThickness = new Thickness(0); b.Padding = new Thickness(7, 3, 7, 3); breadcrumbs.Children.Add(b); }
        address.Text = CurrentPath;
    }
    void EditAddress() { address.Text = CurrentPath; address.Visibility = Visibility.Visible; address.Focus(); address.SelectAll(); }
    public async Task NavigateAsync(string path) { FileNavigationRequested?.Invoke(); await Navigate(path, true); }
    async Task Navigate(string path, bool addHistory, BrowserHistoryItem? restore = null, int? targetHistoryIndex = null)
    {
        recursiveSearchCts?.Cancel();
        thumbnailCts.Cancel(); thumbnailCts.Dispose(); thumbnailCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, session.Lifetime);
        if (!session.SftpAvailable) throw new InvalidOperationException("SFTP-подсистема недоступна. Откройте терминал или проверьте доступ сервера без изменения его конфигурации.");
        navigationCts.Cancel(); navigationCts.Dispose(); navigationCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, session.Lifetime); var ct = navigationCts.Token; var version = viewModel.BeginNavigation();
        var oldSelected = viewModel.SelectedPaths.ToList(); var oldOffset = GetScrollOffset();
        try
        {
            var normalized = await session.Files.ResolvePathAsync(path.StartsWith('/') || path.StartsWith('~') ? path : PosixPath.Combine(string.IsNullOrEmpty(CurrentPath) ? session.Files.HomeDirectory : CurrentPath, path), ct);
            var entries = await session.Files.ListAsync(normalized, ct); if (version != generation || ct.IsCancellationRequested) return;
            if (!viewModel.CommitNavigation(version, normalized, entries, addHistory, oldSelected, oldOffset, targetHistoryIndex)) return;
            ApplyItems(); RestoreSelection(viewModel.SelectedPaths.ToList());
            if (restore is not null) _ = Dispatcher.BeginInvoke(() => FindVisualChild<ScrollViewer>(mode == "Details" ? table : tiles)?.ScrollToVerticalOffset(restore.Offset));
            UpdateBreadcrumbs();
            _ = SyncTree(normalized, version); UpdateStatus();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { viewModel.FailNavigation(version, path, "Операция отменена."); }
        catch (Exception ex) { viewModel.FailNavigation(version, path, ErrorMessages.Describe(ex)); }
    }
    async Task Back() { if (viewModel.HistoryTarget(-1) is { } target) await Navigate(target.Item.Path, false, target.Item, target.Index); }
    async Task Forward() { if (viewModel.HistoryTarget(1) is { } target) await Navigate(target.Item.Path, false, target.Item, target.Index); }
    Task Up() => CurrentPath.Length > 0 ? NavigateAsync(PosixPath.Parent(CurrentPath)) : Task.CompletedTask;
    public Task RefreshAsync()
    {
        imageCache.Clear(); cacheBytes = 0;
        void Invalidate(ItemsControl parent) { foreach (var n in parent.Items.OfType<TreeViewItem>()) { if ((n.Tag as string) == CurrentPath) { n.Items.Clear(); n.Items.Add(new TreeViewItem { Header = "Загрузка…", IsEnabled = false }); if (n.IsExpanded) _ = LoadNode(n); return; } Invalidate(n); } } Invalidate(tree);
        return CurrentPath.Length > 0 ? Navigate(CurrentPath, false) : Task.CompletedTask;
    }
    async Task SyncTree(string path, long version)
    {
        try
        {
            var node = tree.Items.OfType<TreeViewItem>().FirstOrDefault(); if (node is null) return; var full = "";
            foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                await LoadNode(node); if (version != generation || disposed) return; node.IsExpanded = true; full += "/" + part; var next = node.Items.OfType<TreeViewItem>().FirstOrDefault(x => (x.Tag as string) == full); if (next is null) return; node = next;
            }
            if (version == generation && !disposed) { syncingTree = true; node.IsSelected = true; syncingTree = false; }
        }
        catch (OperationCanceledException) { }
    }
    double GetScrollOffset() => FindVisualChild<ScrollViewer>(mode == "Details" ? table : tiles)?.VerticalOffset ?? 0;
    public object CaptureContext() => viewModel.Capture(GetScrollOffset());
    public void RestoreContext(object? context)
    {
        if (context is not BrowserContext saved || !viewModel.Restore(saved)) return; SetMode(saved.Mode, false); RestoreSelection(saved.Selection); _ = Dispatcher.BeginInvoke(() => FindVisualChild<ScrollViewer>(mode == "Details" ? table : tiles)?.ScrollToVerticalOffset(saved.Offset));
    }
    void ApplyItems()
    {
        if (disposed) return; var selection = viewModel.SelectedPaths.ToList(); restoringSelection = true;
        try { viewModel.ApplyFilter(App.Store.Settings.ShowHidden, App.Store.Settings.ShowExtensions); RestoreSelection(selection); }
        finally { restoringSelection = false; }
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
        var selection = Selected().Select(r => r.Entry.FullPath).ToList(); mode = value; App.Store.Settings.ViewMode = mode; content.Content = mode == "Details" ? table : tiles;
        if (mode != "Details")
        {
            var width = mode switch { "Huge" => 180d, "Large" => 144d, "Medium" => 112d, "Small" => 180d, "List" => 250d, "Tile" => 310d, _ => 800d };
            var height = mode switch { "Huge" => 192d, "Large" => 156d, "Medium" => 126d, "Small" => 38d, "List" => 30d, "Tile" => 82d, _ => 92d };
            var factory = new FrameworkElementFactory(mode is "List" or "Content" ? typeof(VirtualizingStackPanel) : typeof(VirtualizingWrapPanel)); if (mode is not "List" and not "Content") { factory.SetValue(VirtualizingWrapPanel.ItemWidthProperty, width); factory.SetValue(VirtualizingWrapPanel.ItemHeightProperty, height); } tiles.ItemsPanel = new ItemsPanelTemplate(factory);
            tiles.ItemTemplate = TileTemplate(mode); tiles.ItemContainerStyle = new Style(typeof(ListBoxItem)) { Setters = { new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch), new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Stretch), new Setter(FrameworkElement.MarginProperty, new Thickness(3)), new Setter(Control.PaddingProperty, new Thickness(5)), new Setter(OpacityProperty, new Binding("Opacity")) } };
            if (mode is "List" or "Content") tiles.ItemContainerStyle.Setters.Add(new Setter(HeightProperty, height));
        }
        RestoreSelection(selection); UpdatePreviewVisibility(); if (save) _ = App.Store.SaveAsync();
    }
    DataTemplate TileTemplate(string view)
    {
        bool horizontal = view is "Small" or "List" or "Tile" or "Content"; var iconSize = view switch { "Huge" => 120d, "Large" => 88d, "Medium" => 56d, "Small" => 22d, "List" => 18d, _ => 48d };
        var template = new DataTemplate(); var panel = new FrameworkElementFactory(typeof(StackPanel)); panel.SetValue(StackPanel.OrientationProperty, horizontal ? Orientation.Horizontal : Orientation.Vertical);
        var iconHost = new FrameworkElementFactory(typeof(Grid)); iconHost.SetValue(FrameworkElement.WidthProperty, iconSize); iconHost.SetValue(FrameworkElement.HeightProperty, iconSize); iconHost.SetValue(FrameworkElement.MarginProperty, horizontal ? new Thickness(0, 0, 12, 0) : new Thickness(0, 5, 0, 8));
        var icon = new FrameworkElementFactory(typeof(TextBlock)); icon.SetBinding(TextBlock.TextProperty, new Binding("Glyph")); icon.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets")); icon.SetValue(TextBlock.FontSizeProperty, iconSize * .82); icon.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center); icon.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); icon.SetBinding(TextBlock.ForegroundProperty, new Binding("IconBrush")); icon.SetBinding(VisibilityProperty, new Binding("Thumbnail") { Converter = new IconVisibilityConverter() }); iconHost.AppendChild(icon);
        var image = new FrameworkElementFactory(typeof(Image)); image.SetBinding(Image.SourceProperty, new Binding("Thumbnail")); image.SetValue(Image.StretchProperty, Stretch.Uniform); image.AddHandler(FrameworkElement.LoadedEvent, new RoutedEventHandler(async (s, _) => { if (((FrameworkElement)s).DataContext is FileItemViewModel r && IsImage(r.Entry)) await Ui.Guard(() => LoadThumbnail(r)); })); iconHost.AppendChild(image); panel.AppendChild(iconHost);
        var texts = new FrameworkElementFactory(typeof(StackPanel)); texts.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        var name = new FrameworkElementFactory(typeof(TextBlock)); name.SetBinding(TextBlock.TextProperty, new Binding("DisplayName")); name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); name.SetValue(TextBlock.TextWrappingProperty, horizontal ? TextWrapping.NoWrap : TextWrapping.Wrap); name.SetValue(TextBlock.MaxHeightProperty, horizontal ? 24d : 44d); name.SetValue(TextBlock.TextAlignmentProperty, horizontal ? TextAlignment.Left : TextAlignment.Center); texts.AppendChild(name);
        if (view is "Tile" or "Content") { var details = new FrameworkElementFactory(typeof(TextBlock)); details.SetBinding(TextBlock.TextProperty, new Binding("TileDetails")); details.SetValue(TextBlock.FontSizeProperty, 12d); details.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); texts.AppendChild(details); }
        if (view == "Content") { var path = new FrameworkElementFactory(typeof(TextBlock)); path.SetBinding(TextBlock.TextProperty, new Binding("Entry.FullPath")); path.SetValue(TextBlock.FontSizeProperty, 12d); path.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); texts.AppendChild(path); }
        panel.AppendChild(texts); template.VisualTree = panel; return template;
    }
    void ViewMenu()
    {
        var m = new ContextMenu(); foreach (var (key, label) in new[] { ("Huge", "Огромные значки"), ("Large", "Крупные значки"), ("Medium", "Обычные значки"), ("Small", "Мелкие значки"), ("List", "Список"), ("Details", "Таблица"), ("Tile", "Плитка"), ("Content", "Содержимое") }) { var item = Ui.Menu(label, () => SetMode(key)); item.IsCheckable = true; item.IsChecked = mode == key; m.Items.Add(item); }
        m.Items.Add(new Separator()); void Toggle(string label, bool initial, Action<bool> action) { var i = new MenuItem { Header = label, IsCheckable = true, IsChecked = initial }; i.Click += (_, _) => { action(i.IsChecked); _ = App.Store.SaveAsync(); }; m.Items.Add(i); }
        Toggle("Скрытые элементы", App.Store.Settings.ShowHidden, x => { App.Store.Settings.ShowHidden = x; ApplyItems(); }); Toggle("Расширения имён файлов", App.Store.Settings.ShowExtensions, x => { App.Store.Settings.ShowExtensions = x; ApplyItems(); }); Toggle("Область сведений", App.Store.Settings.DetailsPane, x => { App.Store.Settings.DetailsPane = x; UpdatePreviewVisibility(); _ = Ui.Guard(UpdatePreview); }); Toggle("Область просмотра", App.Store.Settings.PreviewPane, x => { App.Store.Settings.PreviewPane = x; UpdatePreviewVisibility(); _ = Ui.Guard(UpdatePreview); });
        var columns = new MenuItem { Header = "Колонки таблицы" }; foreach (var c in table.Columns) { var i = new MenuItem { Header = c.Header, IsCheckable = true, IsChecked = c.Visibility == Visibility.Visible, IsEnabled = c != table.Columns[0] }; i.Click += (_, _) => { c.Visibility = i.IsChecked ? Visibility.Visible : Visibility.Collapsed; SaveColumns(); }; columns.Items.Add(i); } m.Items.Add(columns); m.IsOpen = true;
    }
    void SortMenu()
    {
        var m = new ContextMenu(); foreach (var (key, title) in new[] { ("Name", "Имя"), ("Modified", "Дата изменения"), ("Type", "Тип"), ("Size", "Размер") }) { var i = Ui.Menu(title, () => { sortKey = key; ApplyItems(); }); i.IsCheckable = true; i.IsChecked = key == sortKey; m.Items.Add(i); }
        m.Items.Add(new Separator()); var desc = new MenuItem { Header = "По убыванию", IsCheckable = true, IsChecked = descending }; desc.Click += (_, _) => { descending = desc.IsChecked; ApplyItems(); }; m.Items.Add(desc); m.IsOpen = true;
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
        var m = new ContextMenu(); if (selected)
        {
            m.Items.Add(Ui.AsyncMenu("Открыть", Open, "Enter")); var first = Selected().FirstOrDefault()?.Entry; var newTab = Ui.AsyncMenu("Открыть в новой вкладке", () => first is not null ? main.OpenServer(session.Profile, first.FullPath) : Task.CompletedTask); newTab.IsEnabled = Selected().Count == 1 && first?.IsDirectory == true && !first.IsSymbolicLink; newTab.ToolTip = newTab.IsEnabled ? "Открыть каталог в независимой сессии" : "Доступно для одного обычного каталога"; m.Items.Add(newTab); m.Items.Add(Ui.AsyncMenu(first?.IsDirectory == true ? "Открыть в терминале" : "Открыть родительскую папку в терминале", () => OpenTerminal(first?.IsDirectory == true && !first.IsSymbolicLink ? first.FullPath : first is not null ? PosixPath.Parent(first.FullPath) : CurrentPath)));
            m.Items.Add(new Separator()); m.Items.Add(Ui.AsyncMenu("Вырезать", () => CopySelection(true), "Ctrl+X")); m.Items.Add(Ui.AsyncMenu("Копировать", () => CopySelection(false), "Ctrl+C")); m.Items.Add(Ui.AsyncMenu("Скачать…", Download)); m.Items.Add(Ui.AsyncMenu("Переименовать", Rename, "F2")); m.Items.Add(Ui.AsyncMenu("Удалить окончательно…", Delete, "Delete")); m.Items.Add(Ui.Menu("Копировать путь", () => { var p = Selected().Select(r => r.Entry.FullPath); Clipboard.SetText(string.Join(Environment.NewLine, p)); })); m.Items.Add(Ui.AsyncMenu("Свойства", Properties, "Alt+Enter"));
        }
        else { m.Items.Add(Ui.AsyncMenu("Создать папку", () => Create(true), "Ctrl+Shift+N")); m.Items.Add(Ui.AsyncMenu("Создать файл", () => Create(false))); m.Items.Add(Ui.AsyncMenu("Обновить", RefreshAsync, "F5")); m.Items.Add(Ui.AsyncMenu("Загрузить файлы…", Upload)); m.Items.Add(Ui.AsyncMenu("Загрузить папку…", UploadFolder)); m.Items.Add(Ui.AsyncMenu("Поиск во вложенных папках…", RecursiveSearch)); m.Items.Add(Ui.AsyncMenu("Открыть папку в терминале", () => OpenTerminal(CurrentPath))); }
        m.Items.Add(Ui.AsyncMenu("Вставить", Paste, "Ctrl+V")); m.Items.Add(Ui.AsyncMenu("Отменить последнее переименование / перемещение", Undo, "Ctrl+Z")); m.Items.Add(new Separator()); m.Items.Add(Ui.AsyncMenu("Закрепить / открепить папку", () => TogglePin(Selected().FirstOrDefault(r => r.Entry.IsDirectory)?.Entry.FullPath ?? CurrentPath))); return m;
    }
    async Task Open()
    {
        var row = Selected().FirstOrDefault(); if (row is null) return;
        var entry = row.Entry;
        if (entry.IsSymbolicLink) { if (!Ui.Confirm($"Открыть цель символической ссылки «{entry.Name}»?\n\n{entry.LinkTarget ?? "Цель будет определена сервером"}")) return; entry = await session.Files.FollowLinkAsync(entry.FullPath, lifetime.Token); }
        if (entry.IsDirectory) { await NavigateAsync(entry.FullPath); return; }
        if (IsImage(entry)) { await ShowImage(entry); return; }
        if (!entry.IsRegularFile) throw new InvalidOperationException("Сокеты, устройства и FIFO нельзя открывать как обычные файлы.");
        if (entry.Length <= 256 * 1024)
        {
            var data = await session.Files.ReadBytesAsync(entry.FullPath, 256 * 1024, lifetime.Token);
            if (!data.Contains((byte)0)) { var w = new Window { Title = entry.Name + " — только просмотр", Width = 850, Height = 600, Owner = main, WindowStartupLocation = WindowStartupLocation.CenterOwner }; w.Content = new TextBox { Text = Encoding.UTF8.GetString(data), IsReadOnly = true, FontFamily = new FontFamily("Consolas"), AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; TrackWindow(w); w.Show(); return; }
        }
        if (Ui.Confirm("Для этого типа нет встроенного просмотра. Скачать выбранный файл?")) { if (entry.FullPath == row.Entry.FullPath) await Download(); else { var f = new OpenFolderDialog { Title = "Скачать цель ссылки" }; if (f.ShowDialog(main) == true) { var job = App.Transfers.Download(session, [entry.FullPath], f.FolderName); main.ShowTransfers(); await App.Transfers.WaitAsync(job, lifetime.Token); ReportTransfer(job); } } }
    }
    async Task Create(bool directory)
    {
        if (string.IsNullOrEmpty(CurrentPath)) return; var name = Ui.Prompt(directory ? "Новая папка" : "Новый файл", "Имя", directory ? "Новая папка" : "Новый файл.txt"); if (name is null) return; PosixPath.ValidateName(name); var path = PosixPath.Combine(CurrentPath, name);
        if (directory) await session.Files.CreateDirectoryAsync(path, lifetime.Token); else await session.Files.CreateFileAsync(path, lifetime.Token); await RefreshAsync(); RestoreSelection([path]);
    }
    async Task Rename()
    {
        var selected = Selected(); if (selected.Count != 1) return; var entry = selected[0].Entry; var name = Ui.Prompt("Переименовать", "Новое имя", entry.Name); if (name is null || name == entry.Name) return; PosixPath.ValidateName(name); var target = PosixPath.Combine(PosixPath.Parent(entry.FullPath), name);
        await session.Files.RenameAsync(entry.FullPath, target, lifetime.Token); await RefreshAsync(); RestoreSelection([target]);
    }
    async Task Delete()
    {
        var entries = Selected().Select(r => r.Entry).ToList(); if (entries.Count == 0) return;
        if (!Ui.Confirm($"Окончательно удалить {entries.Count} объектов на сервере «{session.Profile.Name}»?\n\nПапка: {CurrentPath}\n{string.Join("\n", entries.Take(6).Select(e => e.Name))}\n\nКорзины и восстановления нет. Ссылки удаляются без удаления их цели.", "Окончательное удаление на Linux")) return;
        var done = 0;
        try { foreach (var e in entries) { await session.Files.DeleteAsync(e.FullPath, lifetime.Token); done++; } }
        catch (Exception ex) { Ui.Error($"Удалено объектов: {done} из {entries.Count}.\n" + ErrorMessages.Describe(ex)); }
        await RefreshAsync();
    }
    async Task Undo() { await session.Files.UndoAsync(lifetime.Token); await RefreshAsync(); }
    async Task Upload()
    {
        var dialog = new OpenFileDialog { Multiselect = true, Title = "Загрузить файлы на " + session.Profile.Name }; if (dialog.ShowDialog(main) != true) return;
        var job = App.Transfers.Upload(session, dialog.FileNames, CurrentPath); main.ShowTransfers(); await WaitAndRefresh(job);
    }
    async Task UploadFolder()
    {
        var dialog = new OpenFolderDialog { Multiselect = true, Title = "Загрузить папки" }; if (dialog.ShowDialog(main) != true) return; var job = App.Transfers.Upload(session, dialog.FolderNames, CurrentPath); main.ShowTransfers(); await WaitAndRefresh(job);
    }
    async Task Download()
    {
        var entries = Selected(); if (entries.Count == 0) return; var dialog = new OpenFolderDialog { Title = "Папка для скачивания" }; if (dialog.ShowDialog(main) != true) return;
        var job = App.Transfers.Download(session, entries.Select(r => r.Entry.FullPath), dialog.FolderName); main.ShowTransfers(); await App.Transfers.WaitAsync(job, lifetime.Token); ReportTransfer(job);
    }
    async Task WaitAndRefresh(TransferJob job) { await App.Transfers.WaitAsync(job, lifetime.Token); ReportTransfer(job); await RefreshAsync(); }
    void ReportTransfer(TransferJob job) { if (job.State == TransferState.Failed) Ui.Error(job.Error ?? "Передача завершилась ошибкой."); }
    async Task CopySelection(bool cut)
    {
        var selected = Selected(); if (selected.Count == 0) return; var payload = new RemoteClipboard(session, selected.Select(r => r.Entry.FullPath).ToArray(), cut);
        var data = RemoteData(payload); Clipboard.SetDataObject(data, false); var clipboardSequence = WindowsClipboard.CurrentSequence; foreach (var r in all) { r.IsCut = cut && selected.Contains(r); r.Notify(); }
        if (cut) { viewModel.Message = "Объекты вырезаны. Вставьте их в папку этого приложения. Для передачи в Windows используйте копирование."; return; }
        viewModel.Message = "Подготовка файлов для системного буфера Windows…";
        var files = await PrepareWindowsFiles(payload);
        if (files is not null)
        {
            if (WindowsClipboard.CurrentSequence != clipboardSequence) { viewModel.Message = "Подготовка завершена, но буфер обмена уже изменён. Повторите копирование, чтобы поместить файлы в Windows."; return; }
            data.SetFileDropList(files); Clipboard.SetDataObject(data, false); viewModel.Message = "Файлы скопированы. Можно вставить в Windows Explorer или серверную вкладку.";
        }
    }
    async Task Paste()
    {
        var data = Clipboard.GetDataObject(); var clipboardSequence = WindowsClipboard.CurrentSequence; if (data is null || string.IsNullOrEmpty(CurrentPath)) return;
        if (GetRemote(data) is RemoteClipboard remote)
        {
            var job = App.Transfers.Copy(remote.Session, remote.Paths, session, CurrentPath, remote.Cut); main.ShowTransfers(); await WaitAndRefresh(job);
            if (job.State == TransferState.Completed && remote.Cut) { foreach (var r in all) { r.IsCut = false; r.Notify(); } if (WindowsClipboard.CurrentSequence == clipboardSequence) Clipboard.Clear(); }
        }
        else if (data.GetData(DataFormats.FileDrop) is string[] local) { var job = App.Transfers.Upload(session, local, CurrentPath); main.ShowTransfers(); await WaitAndRefresh(job); }
        else viewModel.Message = "В буфере нет файлов для вставки.";
    }
    async Task<StringCollection?> PrepareWindowsFiles(RemoteClipboard payload)
    {
        var signatures = new List<string>(); bool cacheable = true;
        foreach (var path in payload.Paths) { var e = await payload.Session.Files.StatAsync(path, lifetime.Token); if (!e.IsRegularFile || e.IsSymbolicLink) { cacheable = false; break; } signatures.Add($"{payload.Session.Id}|{e.FullPath}|{e.Length}|{e.LastWriteTimeUtc.Ticks}"); }
        var cacheKey = string.Join("\0", signatures.Order(StringComparer.Ordinal)); if (cacheable && preparedFiles.TryGetValue(cacheKey, out var ready) && ready.Cast<string>().All(File.Exists)) return ready;
        var temp = Path.Combine(App.Store.DirectoryPath, "TransferCache", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
        var job = App.Transfers.Download(payload.Session, payload.Paths, temp); await App.Transfers.WaitAsync(job, lifetime.Token); if (job.State != TransferState.Completed) { ReportTransfer(job); return null; }
        var paths = Directory.GetFileSystemEntries(temp); var files = new StringCollection(); files.AddRange(paths); if (cacheable) { if (preparedFiles.Count > 64) preparedFiles.Clear(); preparedFiles[cacheKey] = files; } return files;
    }
    void ConfigureDrag(Control control)
    {
        control.AllowDrop = true;
        control.PreviewMouseLeftButtonDown += (_, e) => dragStart = ItemAt(e.OriginalSource as DependencyObject) is not null ? e.GetPosition(control) : null;
        control.PreviewMouseMove += async (_, e) =>
        {
            if (preparingDrag || e.LeftButton != MouseButtonState.Pressed || dragStart is not Point start || (e.GetPosition(control) - start).Length < 10 || Selected().Count == 0) return;
            if (FindAncestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject) is not null) return;
            dragStart = null; preparingDrag = true;
            try
            {
                var payload = new RemoteClipboard(session, Selected().Select(r => r.Entry.FullPath).ToArray(), false); var data = RemoteData(payload); var clipboardSequence = WindowsClipboard.CurrentSequence;
                viewModel.Message = "Подготовка перетаскивания в Windows. Дождитесь скачивания; исходники останутся на сервере.";
                var files = await PrepareWindowsFiles(payload); if (files is null) return; data.SetFileDropList(files);
                viewModel.Message = "Готово к перетаскиванию. Внутри сессии — перемещение, Ctrl — копирование; в Windows и другую вкладку — копирование.";
                if (Mouse.LeftButton == MouseButtonState.Pressed) DragDrop.DoDragDrop(control, data, DragDropEffects.Copy | DragDropEffects.Move);
                else if (WindowsClipboard.CurrentSequence == clipboardSequence) { Clipboard.SetDataObject(data, false); viewModel.Message = "Подготовленные файлы помещены в буфер: вставьте их в Windows или перетащите снова."; }
                else viewModel.Message = "Файлы подготовлены. Буфер обмена изменён пользователем; повторите перетаскивание или копирование.";
            }
            catch (Exception ex) { viewModel.Message = ErrorMessages.Describe(ex); }
            finally { preparingDrag = false; }
        };
        control.DragOver += (_, e) => { var row = ItemAt(e.OriginalSource as DependencyObject); DropOver(e, row?.Entry.IsDirectory == true ? row.Entry.FullPath : CurrentPath, control); };
        control.DragLeave += (_, _) => { dropTarget?.ClearValue(BackgroundProperty); dropTarget = null; control.ClearValue(BorderBrushProperty); };
        control.Drop += async (_, e) => { e.Handled = true; dropTarget?.ClearValue(BackgroundProperty); dropTarget = null; control.ClearValue(BorderBrushProperty); var row = ItemAt(e.OriginalSource as DependencyObject); await Ui.Guard(() => DropData(e.Data, row?.Entry.IsDirectory == true ? row.Entry.FullPath : CurrentPath, e.KeyStates)); };
    }
    void DropOver(DragEventArgs e, string path, Control control)
    {
        if (GetRemote(e.Data) is RemoteClipboard payload) e.Effects = payload.Session.Id == session.Id && (e.KeyStates & DragDropKeyStates.ControlKey) == 0 ? DragDropEffects.Move : DragDropEffects.Copy;
        else e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true; if (e.Effects != DragDropEffects.None)
        {
            var target = (Control?)FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) ?? FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) ?? control;
            if (dropTarget != target) { dropTarget?.ClearValue(BackgroundProperty); dropTarget = target; } target.SetResourceReference(BackgroundProperty, "Selected"); viewModel.Message = $"{(e.Effects == DragDropEffects.Move ? "Переместить" : "Копировать")} в {path}";
            if (control is TreeViewItem node) { if (hoverNode != node) { hoverNode = node; hoverSince = DateTime.UtcNow; } else if ((DateTime.UtcNow - hoverSince).TotalMilliseconds > 800) node.IsExpanded = true; }
            var y = e.GetPosition(control).Y; var scroller = FindVisualChild<ScrollViewer>(control); if (y < 24) scroller?.LineUp(); else if (y > control.ActualHeight - 24) scroller?.LineDown();
        }
    }
    async Task DropData(IDataObject data, string path, DragDropKeyStates keys)
    {
        TransferJob? job = null;
        if (GetRemote(data) is RemoteClipboard payload) job = App.Transfers.Copy(payload.Session, payload.Paths, session, path, payload.Session.Id == session.Id && (keys & DragDropKeyStates.ControlKey) == 0);
        else if (data.GetData(DataFormats.FileDrop) is string[] local) job = App.Transfers.Upload(session, local, path);
        if (job is not null) { main.ShowTransfers(); await WaitAndRefresh(job); }
    }
    static FileItemViewModel? ItemAt(DependencyObject? source) => FindAncestor<DataGridRow>(source)?.Item as FileItemViewModel ?? FindAncestor<ListBoxItem>(source)?.DataContext as FileItemViewModel;
    static DataObject RemoteData(RemoteClipboard payload)
    {
        var token = Guid.NewGuid().ToString("N"); if (remoteClipboardTokens.Count >= 64) remoteClipboardTokens.Remove(remoteClipboardTokens.Keys.First()); remoteClipboardTokens[token] = new ClipboardToken(payload.Session.Id, new WeakReference<ServerSession>(payload.Session), payload.Paths, payload.Cut);
        var data = new DataObject(); data.SetData("SSHExplorer.RemoteFiles", new MemoryStream(Encoding.UTF8.GetBytes(token), false), false); return data;
    }
    static RemoteClipboard? GetRemote(IDataObject data)
    {
        // Only an opaque raw token crosses OLE. Live sessions are never serialized or deserialized.
        if (!data.GetDataPresent("SSHExplorer.RemoteFiles", false)) return null;
        var raw = data.GetData("SSHExplorer.RemoteFiles", false); var token = raw is MemoryStream stream ? Encoding.UTF8.GetString(stream.ToArray()) : raw as string;
        return token is not null && remoteClipboardTokens.TryGetValue(token, out var payload) && payload.Session.TryGetTarget(out var owner) && owner.State is not SessionState.Closing and not SessionState.Closed ? new RemoteClipboard(owner, payload.Paths, payload.Cut) : null;
    }
    async Task OpenTerminal(string path)
    {
        var window = new Window { Title = session.Profile.Name + " — отдельный терминал · " + path, Width = 1050, Height = 650, Owner = main, WindowStartupLocation = WindowStartupLocation.CenterOwner }; var terminal = new TerminalView(session); window.Content = terminal; window.Closed += (_, _) => terminal.Dispose();
        var registration = session.Lifetime.Register(() => Dispatcher.BeginInvoke(() => { if (window.IsLoaded) window.Close(); })); window.Closed += (_, _) => registration.Dispose(); TrackWindow(window); window.Show(); await terminal.StartAsync(path);
    }
    async Task Properties()
    {
        var entry = Selected().FirstOrDefault()?.Entry; if (entry is null) return; entry = await session.Files.StatAsync(entry.FullPath, lifetime.Token);
        var w = new Window { Title = "Свойства: " + entry.Name, Width = 580, SizeToContent = SizeToContent.Height, MaxHeight = 820, Owner = main, WindowStartupLocation = WindowStartupLocation.CenterOwner }; var panel = new StackPanel { Margin = new Thickness(24) };
        var text = Ui.Text($"{entry.Name}\n\nПуть: {entry.FullPath}\nТип: {entry.TypeName}\nРазмер: {entry.SizeText}\nИзменён (местное время): {TimeText(entry.LastWriteTimeUtc)}\nДоступ (местное время): {TimeText(entry.LastAccessTimeUtc)}\nUID: {IdText(entry.UserId)}   GID: {IdText(entry.GroupId)}" + (entry.IsSymbolicLink ? "\nЦель ссылки: " + (entry.LinkTarget ?? "Недоступно") : "")); text.TextWrapping = TextWrapping.Wrap; panel.Children.Add(text);
        var modeText = new TextBox { Text = entry.PermissionsText, Margin = new Thickness(0, 14, 0, 6), ToolTip = "POSIX-права в восьмеричной записи, например 0755" }; panel.Children.Add(Ui.Text("Права (восьмеричные)")); panel.Children.Add(modeText);
        var rwx = new UniformGrid { Columns = 3 }; var boxes = new List<CheckBox>(); foreach (var label in new[] { "Владелец: чтение", "Запись", "Выполнение", "Группа: чтение", "Запись", "Выполнение", "Остальные: чтение", "Запись", "Выполнение" }) { var b = new CheckBox { Content = label }; boxes.Add(b); rwx.Children.Add(b); } panel.Children.Add(rwx);
        bool updating = false;
        void SyncChecks() { if (updating) return; if (!short.TryParse(ConvertOctal(modeText.Text), out var bits)) return; updating = true; for (var i = 0; i < 9; i++) boxes[i].IsChecked = (bits & (1 << (8 - i))) != 0; updating = false; }
        foreach (var box in boxes) box.Click += (_, _) => { if (updating) return; updating = true; var bits = short.TryParse(ConvertOctal(modeText.Text), out var previous) ? previous & 0xE00 : 0; for (var i = 0; i < 9; i++) if (boxes[i].IsChecked == true) bits |= 1 << (8 - i); modeText.Text = Convert.ToString(bits, 8).PadLeft(4, '0'); updating = false; }; modeText.TextChanged += (_, _) => SyncChecks(); SyncChecks();
        var uid = new TextBox { Text = entry.UserId.ToString() }; var gid = new TextBox { Text = entry.GroupId.ToString() }; panel.Children.Add(Ui.Text("Владелец UID / группа GID")); panel.Children.Add(Ui.Row(uid, gid));
        var recursive = new CheckBox { Content = "Рекурсивно изменить вложенные объекты (отдельное действие)", IsEnabled = entry.IsDirectory && !entry.IsSymbolicLink, Margin = new Thickness(0, 12, 0, 6) }; panel.Children.Add(recursive);
        panel.Children.Add(Ui.AsyncButton("Применить права", async () => { var bits = Convert.ToInt16(modeText.Text, 8); if (bits < 0 || bits > 4095) throw new ArgumentException("Права должны быть 0000–7777."); if (recursive.IsChecked == true && !Ui.Confirm("Рекурсивно изменить права всего доступного дерева этой папки?")) return; await session.Files.SetPermissionsAsync(entry.FullPath, short.Parse(modeText.Text), recursive.IsChecked == true, lifetime.Token); await RefreshAsync(); }));
        panel.Children.Add(Ui.AsyncButton("Применить владельца / группу", async () => { if (recursive.IsChecked == true && !Ui.Confirm("Рекурсивно изменить владельца и группу?")) return; await session.Files.SetOwnerAsync(entry.FullPath, int.Parse(uid.Text), int.Parse(gid.Text), recursive.IsChecked == true, lifetime.Token); await RefreshAsync(); }));
        var sizeCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); var sizeLabel = Ui.Text(""); if (entry.IsDirectory) { panel.Children.Add(Ui.AsyncButton("Рассчитать размер дерева", async () => { sizeLabel.Text = "Подсчёт…"; sizeLabel.Text = Ui.Size(await session.Files.CalculateSizeAsync(entry.FullPath, sizeCts.Token)); })); panel.Children.Add(Ui.Button("Отменить подсчёт", () => sizeCts.Cancel())); panel.Children.Add(sizeLabel); }
        var note = Ui.Text("Доступ ограничен правами SSH-учётной записи. sudo в терминале не повышает права SFTP.", 12); note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(0, 12, 0, 0); panel.Children.Add(note); w.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; w.Closed += (_, _) => { sizeCts.Cancel(); sizeCts.Dispose(); }; TrackWindow(w); w.Show();
    }
    static string ConvertOctal(string text) { try { return Convert.ToInt16(text, 8).ToString(); } catch { return ""; } }
    void UpdatePreviewVisibility() => preview.Visibility = App.Store.Settings.DetailsPane || App.Store.Settings.PreviewPane ? Visibility.Visible : Visibility.Collapsed;
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
        var controls = Ui.Row(Ui.AsyncButton("←", async () => { index = (index - 1 + images.Count) % images.Count; await Load(); }), Ui.AsyncButton("→", async () => { index = (index + 1) % images.Count; await Load(); }), Ui.Button("Вписать", () => { fit = true; SizeImage(); }), Ui.Button("100%", () => { fit = false; SizeImage(); }), Ui.Button("＋", () => { fit = false; image.Width = (double.IsNaN(image.Width) ? image.ActualWidth : image.Width) * 1.25; image.Height = (double.IsNaN(image.Height) ? image.ActualHeight : image.Height) * 1.25; }), Ui.Button("−", () => { fit = false; image.Width = Math.Max(32, (double.IsNaN(image.Width) ? image.ActualWidth : image.Width) / 1.25); image.Height = Math.Max(32, (double.IsNaN(image.Height) ? image.ActualHeight : image.Height) / 1.25); }), Ui.AsyncButton("Скачать", async () => { var f = new OpenFolderDialog { Title = "Скачать изображение" }; if (f.ShowDialog(w) == true) { var job = App.Transfers.Download(session, [images[index].FullPath], f.FolderName); main.ShowTransfers(); await App.Transfers.WaitAsync(job, lifetime.Token); ReportTransfer(job); } })); DockPanel.SetDock(controls, Dock.Top); p.Children.Add(controls); info.Margin = new Thickness(14, 9, 14, 9); DockPanel.SetDock(info, Dock.Bottom); p.Children.Add(info); p.Children.Add(scroller); w.Content = p; w.Closed += (_, _) => { frameGeneration++; frameCts?.Cancel(); frameCts?.Dispose(); localCts.Cancel(); localCts.Dispose(); }; TrackWindow(w); w.Show(); await Load();
    }
    void HandleKey(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0; var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0; var alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0; var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (ctrl && key == Key.L || alt && key == Key.D) { e.Handled = true; EditAddress(); return; }
        if (ctrl && key == Key.F) { e.Handled = true; search.Focus(); search.SelectAll(); return; }
        if (key == Key.Escape) recursiveSearchCts?.Cancel();
        if (e.OriginalSource is TextBox or PasswordBox) return;
        Func<Task>? action = null;
        if (key == Key.F5) action = RefreshAsync; else if (key == Key.F2) action = Rename; else if (key == Key.Delete) action = Delete; else if (key == Key.Enter && alt) action = Properties; else if (key == Key.Enter) action = Open; else if (key == Key.Left && alt) action = Back; else if (key == Key.Right && alt) action = Forward; else if (key == Key.Up && alt) action = Up; else if (ctrl && key == Key.C) action = () => CopySelection(false); else if (ctrl && key == Key.X) action = () => CopySelection(true); else if (ctrl && key == Key.V) action = Paste; else if (ctrl && key == Key.Z) action = Undo; else if (ctrl && shift && key == Key.N) action = () => Create(true);
        else if (ctrl && key == Key.A) { if (mode == "Details") table.SelectAll(); else tiles.SelectAll(); e.Handled = true; }
        else if (key == Key.Escape) { navigationCts.Cancel(); recursiveSearchCts?.Cancel(); viewModel.SearchQuery = ""; if (mode == "Details") table.UnselectAll(); else tiles.UnselectAll(); e.Handled = true; }
        else if (shift && key == Key.F10) { var menu = BuildContextMenu(Selected().Count > 0); menu.PlacementTarget = mode == "Details" ? table : tiles; menu.IsOpen = true; e.Handled = true; }
        if (action is not null) { e.Handled = true; _ = Ui.Guard(action); }
    }
    public void RouteGlobalKey(KeyEventArgs e) => HandleKey(this, e);
    static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject { while (node is not null) { if (node is T t) return t; node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node); } return null; }
    static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject { for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) { var child = VisualTreeHelper.GetChild(parent, i); if (child is T t) return t; var found = FindVisualChild<T>(child); if (found is not null) return found; } return null; }
    static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject { for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) { var child = VisualTreeHelper.GetChild(parent, i); if (child is T t) yield return t; else foreach (var nested in FindVisualChildren<T>(child)) yield return nested; } }
    void TrackWindow(Window window) { ownedWindows.Add(window); window.Closed += (_, _) => ownedWindows.Remove(window); }
    public void Dispose() { disposed = true; viewModel.InvalidateNavigation(); foreach (var token in remoteClipboardTokens.Where(x => x.Value.SessionId == session.Id).Select(x => x.Key).ToArray()) remoteClipboardTokens.Remove(token); foreach (var window in ownedWindows.ToArray()) if (window.IsLoaded) window.Close(); ownedWindows.Clear(); lifetime.Cancel(); navigationCts.Cancel(); recursiveSearchCts?.Cancel(); thumbnailCts.Cancel(); previewCts.Cancel(); imageCache.Clear(); preparedFiles.Clear(); lifetime.Dispose(); navigationCts.Dispose(); recursiveSearchCts?.Dispose(); thumbnailCts.Dispose(); previewCts.Dispose(); }
    sealed record RemoteClipboard(ServerSession Session, string[] Paths, bool Cut);
    sealed record ClipboardToken(Guid SessionId, WeakReference<ServerSession> Session, string[] Paths, bool Cut);
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

