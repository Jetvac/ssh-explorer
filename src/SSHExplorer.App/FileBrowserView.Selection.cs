using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using SSHExplorer.App.ViewModels;

namespace SSHExplorer.App;

public sealed partial class FileBrowserView
{
    Control? lassoOwner;
    Point lassoStart, lassoPointer, lassoViewportStart;
    bool lassoMoved;
    HashSet<FileItemViewModel> lassoBaseline = [];
    SelectionAdorner? lassoAdorner;
    AdornerLayer? lassoLayer;
    DispatcherTimer? lassoTimer;
    void ConfigureLasso(Control owner)
    {
        owner.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (nameEdit?.IsActive == true || ItemAt(e.OriginalSource as DependencyObject) is not null ||
                FindAncestor<ScrollBar>(e.OriginalSource as DependencyObject) is not null ||
                FindAncestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject) is not null) return;
            BeginLasso(owner, e.GetPosition(owner), (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0);
            e.Handled = lassoOwner is not null;
        };
        owner.PreviewMouseMove += (_, e) =>
        {
            if (lassoOwner != owner) return;
            if (e.LeftButton != MouseButtonState.Pressed) { EndLasso(); return; }
            UpdateLasso(e.GetPosition(owner)); e.Handled = true;
        };
        owner.PreviewMouseLeftButtonUp += (_, e) => { if (lassoOwner == owner) { EndLasso(); e.Handled = true; } };
        owner.LostMouseCapture += (_, _) => { if (lassoOwner == owner) EndLasso(); };
        owner.Unloaded += (_, _) => { if (lassoOwner == owner) EndLasso(); };
    }
    // Same entry points are used by mouse handlers and isolated CI layout checks.
    internal void BeginLasso(Control owner, Point point, bool additive)
    {
        EndLasso(); lassoOwner = owner;
        lassoBaseline = additive ? ActiveSelected().OfType<FileItemViewModel>().ToHashSet() : [];
        lassoStart = ContentPoint(point); lassoPointer = lassoViewportStart = point; lassoMoved = false;
        lassoLayer = AdornerLayer.GetAdornerLayer(owner);
        if (lassoLayer is null) { lassoOwner = null; return; }
        lassoAdorner = new SelectionAdorner(owner); lassoLayer.Add(lassoAdorner);
        if (!owner.CaptureMouse()) { EndLasso(); return; }
        if (!additive) ActiveSelected().Clear();
        dragStart = null;
        lassoTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Input, (_, _) => ScrollLasso(), Dispatcher);
        UpdateLasso(point);
    }
    System.Collections.IList ActiveSelected() => mode == "Details" ? table.SelectedItems : tiles.SelectedItems;
    (double Width, double Height, int Columns, Point Origin, double Offset) LassoLayout()
    {
        var owner = lassoOwner!; var scroller = FindVisualChild<ScrollViewer>(owner);
        if (owner == tiles && FindVisualChild<VirtualizingWrapPanel>(tiles) is { } wrap)
            return (wrap.ItemWidth, wrap.ItemHeight, Math.Max(1, (int)(wrap.ViewportWidth / wrap.ItemWidth)), wrap.TranslatePoint(new Point(), owner), wrap.VerticalOffset);
        FrameworkElement? first = owner == table ? FindVisualChildren<DataGridRow>(table).FirstOrDefault() : FindVisualChildren<ListBoxItem>(tiles).FirstOrDefault();
        var height = first is null ? Math.Max(32, table.MinRowHeight) : first.ActualHeight + first.Margin.Top + first.Margin.Bottom;
        if (height <= 0 || !double.IsFinite(height)) height = 34;
        var index = first is DataGridRow row ? row.GetIndex() : first is null ? 0 : tiles.ItemContainerGenerator.IndexFromContainer(first);
        var offset = scroller?.VerticalOffset ?? 0;
        if (scroller?.CanContentScroll != false) offset *= height;
        var origin = first?.TranslatePoint(new Point(), owner) ?? new Point(0, owner == table ? table.ColumnHeaderHeight : 0);
        if (!double.IsFinite(origin.Y)) origin.Y = 34;
        origin.Y += offset - Math.Max(0, index) * height;
        return (Math.Max(1, scroller?.ViewportWidth > 0 ? scroller.ViewportWidth : owner.ActualWidth), height, 1, origin, offset);
    }
    Point ContentPoint(Point point)
    {
        var layout = LassoLayout();
        return new Point(point.X - layout.Origin.X, point.Y - layout.Origin.Y + layout.Offset);
    }
    internal void UpdateLasso(Point point)
    {
        if (lassoOwner is null || lassoAdorner is null) return;
        if (!lassoMoved && (point - lassoViewportStart).Length < Math.Max(SystemParameters.MinimumHorizontalDragDistance, SystemParameters.MinimumVerticalDragDistance)) return;
        lassoMoved = true;
        lassoPointer = point; var layout = LassoLayout(); var end = ContentPoint(point);
        var rectangle = new Rect(lassoStart, end);
        var selected = new HashSet<FileItemViewModel>(lassoBaseline);
        foreach (var index in FileSelectionGeometry.Intersecting(rectangle, viewModel.VisibleEntries.Count, layout.Columns, layout.Width, layout.Height)) selected.Add(viewModel.VisibleEntries[index]);
        var active = ActiveSelected();
        foreach (var item in active.OfType<FileItemViewModel>().Where(r => !selected.Contains(r)).ToArray()) active.Remove(item);
        foreach (var item in selected) if (!active.Contains(item)) active.Add(item);
        lassoAdorner.Rectangle = Rect.Intersect(new Rect(new Point(lassoStart.X + layout.Origin.X, lassoStart.Y + layout.Origin.Y - layout.Offset), point), new Rect(lassoOwner.RenderSize));
    }
    void ScrollLasso()
    {
        if (lassoOwner is null || !lassoMoved) return;
        var scroller = FindVisualChild<ScrollViewer>(lassoOwner);
        if (scroller is null) return;
        if (lassoPointer.Y < 30) scroller.LineUp();
        else if (lassoPointer.Y > lassoOwner.ActualHeight - 30) scroller.LineDown();
        else return;
        lassoOwner.UpdateLayout(); UpdateLasso(lassoPointer);
    }
    internal void EndLasso()
    {
        lassoTimer?.Stop(); lassoTimer = null;
        var owner = lassoOwner; lassoOwner = null;
        if (lassoAdorner is not null) lassoLayer?.Remove(lassoAdorner);
        lassoAdorner = null; lassoLayer = null; lassoBaseline.Clear();
        if (owner?.IsMouseCaptured == true) owner.ReleaseMouseCapture();
    }
}
