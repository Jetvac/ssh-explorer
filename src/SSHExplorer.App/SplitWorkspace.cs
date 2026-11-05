using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SSHExplorer.App;

/// <summary>
/// Up to <see cref="MaxPanes"/> panes split side by side or one above another (a split tree of grids with splitters).
/// Each pane has a thin header: its title, «split right», «split down» and «close». The pane that last got the mouse or
/// keyboard is active; new panes split the active one. Pane contents come from a factory, so the layout is independent
/// of what is inside (terminals in the app, plain elements in tests).
/// </summary>
public sealed class SplitWorkspace : UserControl, IDisposable
{
    public const int MaxPanes = 4;
    readonly Func<FrameworkElement> create;
    readonly Action<FrameworkElement> release;
    readonly Border root = new();
    readonly List<Pane> panes = [];
    int counter;
    Pane? active;

    /// <summary>One pane: header + content. Kept public to the assembly for tests.</summary>
    internal sealed class Pane : DockPanel
    {
        public FrameworkElement Body { get; }
        public TextBlock Title { get; }
        public Border Header { get; }
        public Button SplitRight { get; }
        public Button SplitDown { get; }
        public Button Close { get; }
        public Pane(FrameworkElement body, string title, SplitWorkspace owner)
        {
            Body = body; Title = Ui.Text(title, 12); Title.VerticalAlignment = VerticalAlignment.Center; Title.Margin = new Thickness(10, 0, 8, 0); Title.TextTrimming = TextTrimming.CharacterEllipsis;
            SplitRight = Ui.IconButton(IconCatalog.SplitRight, () => owner.Split(this, Orientation.Horizontal), "Разделить: новый терминал справа · Ctrl+Shift+D"); SplitRight.Name = "PaneSplitRight";
            SplitDown = Ui.IconButton(IconCatalog.SplitDown, () => owner.Split(this, Orientation.Vertical), "Разделить: новый терминал снизу · Ctrl+Shift+E"); SplitDown.Name = "PaneSplitDown";
            Close = Ui.IconButton(Ui.CloseGlyph, () => owner.Close(this), "Закрыть эту панель (её оболочка завершится) · Ctrl+Shift+W"); Close.Name = "PaneClose";
            foreach (var b in new[] { SplitRight, SplitDown, Close }) { b.Height = 26; b.Width = 30; }
            var buttons = Ui.Row(SplitRight, SplitDown, Close); DockPanel.SetDock(buttons, Dock.Right);
            var row = new DockPanel { Height = 28 }; row.Children.Add(buttons); row.Children.Add(Title);
            Header = new Border { Child = row, BorderThickness = new Thickness(0, 0, 0, 2) };
            Header.SetResourceReference(Border.BackgroundProperty, "NavigationSurface");
            DockPanel.SetDock(Header, Dock.Top); Children.Add(Header); Children.Add(body);
            // The terminal is an HWND (WebView2): its clicks never reach WPF, so focus is the reliable signal too.
            PreviewMouseDown += (_, _) => owner.Activate(this);
            IsKeyboardFocusWithinChanged += (_, e) => { if (e.NewValue is true) owner.Activate(this); };
            body.GotFocus += (_, _) => owner.Activate(this);
        }
    }

    public SplitWorkspace(Func<FrameworkElement> create, Action<FrameworkElement> release)
    {
        this.create = create; this.release = release; Name = "SplitWorkspace";
        Content = root;
        PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers != (ModifierKeys.Control | ModifierKeys.Shift) || active is null) return;
            if (e.Key == Key.D) { Split(active, Orientation.Horizontal); e.Handled = true; }
            else if (e.Key == Key.E) { Split(active, Orientation.Vertical); e.Handled = true; }
            else if (e.Key == Key.W && panes.Count > 1) { Close(active); e.Handled = true; }
        };
    }

    public int Count => panes.Count;
    public FrameworkElement? ActiveBody => active?.Body;
    public IReadOnlyList<FrameworkElement> Bodies => panes.Select(p => p.Body).ToArray();
    internal IReadOnlyList<Pane> Panes => panes;
    /// <summary>Raised with the new pane's content after a split, so the owner can start it (a shell, say).</summary>
    public event Action<FrameworkElement>? PaneAdded;

    /// <summary>The first pane; later panes come from splits.</summary>
    public FrameworkElement Start()
    {
        if (panes.Count > 0) return panes[0].Body;
        var pane = NewPane(); root.Child = pane; Activate(pane); Update();
        return pane.Body;
    }

    Pane NewPane()
    {
        var pane = new Pane(create(), "Терминал " + (++counter), this);
        panes.Add(pane); return pane;
    }

    internal bool Split(Pane target, Orientation orientation)
    {
        if (panes.Count >= MaxPanes || !panes.Contains(target)) return false;
        var parent = target.Parent;
        var grid = new Grid();
        bool horizontal = orientation == Orientation.Horizontal;
        for (int i = 0; i < 3; i++)
        {
            if (horizontal) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 1 ? new GridLength(5) : new GridLength(1, GridUnitType.Star), MinWidth = i == 1 ? 0 : 160 });
            else grid.RowDefinitions.Add(new RowDefinition { Height = i == 1 ? new GridLength(5) : new GridLength(1, GridUnitType.Star), MinHeight = i == 1 ? 0 : 90 });
        }
        Replace(parent, target, grid);
        var splitter = new GridSplitter { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, ResizeBehavior = GridResizeBehavior.PreviousAndNext, Focusable = false };
        splitter.SetResourceReference(BackgroundProperty, "Edge");
        var added = NewPane();
        Place(target, 0, horizontal); Place(splitter, 1, horizontal); Place(added, 2, horizontal);
        grid.Children.Add(target); grid.Children.Add(splitter); grid.Children.Add(added);
        Activate(added); Update();
        PaneAdded?.Invoke(added.Body);
        return true;
    }

    internal bool Close(Pane target)
    {
        if (panes.Count <= 1 || !panes.Contains(target) || target.Parent is not Grid grid) return false;
        var sibling = grid.Children.OfType<FrameworkElement>().First(c => c is not GridSplitter && !ReferenceEquals(c, target));
        grid.Children.Clear();
        Grid.SetRow(sibling, 0); Grid.SetColumn(sibling, 0);
        Replace(grid.Parent, grid, sibling);
        panes.Remove(target); release(target.Body);
        Activate(Descendants(sibling).FirstOrDefault() ?? panes[0]); Update();
        return true;
    }

    static IEnumerable<Pane> Descendants(FrameworkElement element) => element is Pane p ? [p] : element is Grid g ? g.Children.OfType<FrameworkElement>().SelectMany(Descendants) : [];

    static void Place(UIElement element, int index, bool horizontal) { if (horizontal) Grid.SetColumn(element, index); else Grid.SetRow(element, index); }

    /// <summary>Puts <paramref name="replacement"/> exactly where <paramref name="old"/> was (root or a grid cell).</summary>
    void Replace(DependencyObject? parent, FrameworkElement old, FrameworkElement replacement)
    {
        if (parent is Border border && ReferenceEquals(border.Child, old)) { border.Child = replacement; return; }
        if (parent is Grid grid)
        {
            int row = Grid.GetRow(old), column = Grid.GetColumn(old), index = grid.Children.IndexOf(old);
            grid.Children.Remove(old); Grid.SetRow(replacement, row); Grid.SetColumn(replacement, column); grid.Children.Insert(index, replacement);
            return;
        }
        throw new InvalidOperationException("Панель не найдена в раскладке.");
    }

    internal void Activate(Pane pane)
    {
        if (!panes.Contains(pane)) return;
        active = pane;
        foreach (var p in panes)
        {
            if (ReferenceEquals(p, active)) p.Header.SetResourceReference(Border.BorderBrushProperty, "Accent");
            else p.Header.BorderBrush = Brushes.Transparent;
        }
    }

    void Update()
    {
        bool canSplit = panes.Count < MaxPanes;
        foreach (var p in panes)
        {
            p.SplitRight.IsEnabled = p.SplitDown.IsEnabled = canSplit; p.Close.IsEnabled = panes.Count > 1;
            p.Header.Visibility = Visibility.Visible;
        }
    }

    Pane? PaneOf(FrameworkElement body) => panes.FirstOrDefault(p => ReferenceEquals(p.Body, body));

    /// <summary>Ctrl+Shift+D (split right), E (split down), W (close) pressed inside a pane's own content.</summary>
    public void Shortcut(FrameworkElement body, string key)
    {
        if (PaneOf(body) is not { } pane) return;
        if (key == "D") Split(pane, Orientation.Horizontal); else if (key == "E") Split(pane, Orientation.Vertical); else if (key == "W") Close(pane);
    }

    /// <summary>The content got focus by itself (an HWND): its pane becomes active.</summary>
    public void FocusBody(FrameworkElement body) { if (PaneOf(body) is { } pane) Activate(pane); }

    public void Dispose()
    {
        foreach (var p in panes) release(p.Body);
        panes.Clear(); root.Child = null; active = null;
    }
}
