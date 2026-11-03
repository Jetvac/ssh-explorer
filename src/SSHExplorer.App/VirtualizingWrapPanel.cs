using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace SSHExplorer.App;

// A recycling, pixel-scrolling panel: a large directory only creates the visible rows.
public sealed class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(nameof(ItemWidth), typeof(double), typeof(VirtualizingWrapPanel), new FrameworkPropertyMetadata(112d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(nameof(ItemHeight), typeof(double), typeof(VirtualizingWrapPanel), new FrameworkPropertyMetadata(124d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public double ItemWidth { get => (double)GetValue(ItemWidthProperty); set => SetValue(ItemWidthProperty, value); }
    public double ItemHeight { get => (double)GetValue(ItemHeightProperty); set => SetValue(ItemHeightProperty, value); }
    Size viewport, extent; double offset; int columns = 1;
    protected override Size MeasureOverride(Size available)
    {
        var owner = ItemsControl.GetItemsOwner(this); if (owner is null) return available;
        var width = double.IsInfinity(available.Width) ? Math.Max(ItemWidth, ActualWidth) : available.Width;
        var height = double.IsInfinity(available.Height) ? Math.Max(ItemHeight, ActualHeight) : available.Height;
        columns = Math.Max(1, (int)(width / ItemWidth)); viewport = new Size(width, height); extent = new Size(width, Math.Ceiling(owner.Items.Count / (double)columns) * ItemHeight);
        offset = Math.Clamp(offset, 0, Math.Max(0, extent.Height - viewport.Height)); ScrollOwner?.InvalidateScrollInfo();
        var first = Math.Max(0, (int)(offset / ItemHeight) * columns);
        var last = Math.Min(owner.Items.Count - 1, ((int)((offset + height) / ItemHeight) + 2) * columns - 1);
        // InternalChildren initializes the WPF generator for a new items host.
        _ = InternalChildren; var gen = ItemContainerGenerator;
        if (gen is null) return viewport;
        for (var i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var pos = new GeneratorPosition(i, 0); var index = gen.IndexFromGeneratorPosition(pos);
            if (index < first || index > last) { if (gen is IRecyclingItemContainerGenerator recycler) recycler.Recycle(pos, 1); else gen.Remove(pos, 1); RemoveInternalChildRange(i, 1); }
        }
        if (last < first) return viewport;
        var start = gen.GeneratorPositionFromIndex(first); var child = start.Offset == 0 ? start.Index : start.Index + 1;
        using (gen.StartAt(start, GeneratorDirection.Forward, true))
        {
            for (var index = first; index <= last; index++, child++)
            {
                var element = (UIElement)gen.GenerateNext(out var fresh);
                if (fresh) { if (child >= InternalChildren.Count) AddInternalChild(element); else InsertInternalChild(child, element); gen.PrepareItemContainer(element); }
                element.Measure(new Size(ItemWidth, ItemHeight));
            }
        }
        return viewport;
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var index = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            InternalChildren[i].Arrange(new Rect((index % columns) * ItemWidth, (index / columns) * ItemHeight - offset, ItemWidth, ItemHeight));
        }
        return finalSize;
    }
    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args) { base.OnItemsChanged(sender, args); InvalidateMeasure(); }
    protected override void BringIndexIntoView(int index)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        if (owner is null || index < 0 || index >= owner.Items.Count) return;
        UpdateLayout(); // SetMode/refresh may have changed the column count and extent.
        // ListBox.ScrollIntoView uses this path when the item has no container yet.
        // Moving the viewport realizes only its row and the normal small buffer.
        var top = index / Math.Max(1, columns) * ItemHeight;
        if (top < offset) SetVerticalOffset(top);
        else if (top + ItemHeight > offset + viewport.Height) SetVerticalOffset(top + ItemHeight - viewport.Height);
        UpdateLayout();
        if (owner.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement container) container.BringIntoView();
    }
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; } = true;
    public double ExtentWidth => extent.Width;
    public double ExtentHeight => extent.Height;
    public double ViewportWidth => viewport.Width;
    public double ViewportHeight => viewport.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => offset;
    public ScrollViewer ScrollOwner { get; set; } = null!;
    public void LineUp() => SetVerticalOffset(offset - ItemHeight);
    public void LineDown() => SetVerticalOffset(offset + ItemHeight);
    public void PageUp() => SetVerticalOffset(offset - viewport.Height);
    public void PageDown() => SetVerticalOffset(offset + viewport.Height);
    public void MouseWheelUp() => SetVerticalOffset(offset - ItemHeight * 3);
    public void MouseWheelDown() => SetVerticalOffset(offset + ItemHeight * 3);
    public void LineLeft() { } public void LineRight() { } public void PageLeft() { } public void PageRight() { } public void MouseWheelLeft() { } public void MouseWheelRight() { } public void SetHorizontalOffset(double value) { }
    public void SetVerticalOffset(double value) { offset = Math.Clamp(value, 0, Math.Max(0, extent.Height - viewport.Height)); ScrollOwner?.InvalidateScrollInfo(); InvalidateMeasure(); }
    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        var child = visual as DependencyObject;
        while (child is not null && VisualTreeHelper.GetParent(child) != this) child = VisualTreeHelper.GetParent(child);
        if (child is UIElement element)
        {
            var itemIndex = InternalChildren.IndexOf(element); var index = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(itemIndex, 0)); var y = (index / columns) * ItemHeight;
            if (y < offset) SetVerticalOffset(y); else if (y + ItemHeight > offset + viewport.Height) SetVerticalOffset(y + ItemHeight - viewport.Height);
        }
        return rectangle;
    }
}
