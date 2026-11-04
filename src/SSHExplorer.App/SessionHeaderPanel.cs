using System.Windows;
using System.Windows.Controls;

namespace SSHExplorer.App;

/// <summary>
/// Lays out [scroll-left][tab viewport][scroll-right][+] inside the width the caption row gives it.
/// Tabs take only their real extent; the "+" button follows the last visible tab; arrow areas
/// exist only while the tabs truly overflow.
/// </summary>
/// <remarks>
/// beta.5 cause of the "Home disappears after opening a server" defect: the inner ScrollViewer is
/// measured with the previous viewport, and its ScrollContentPresenter clamps its desired size to
/// that constraint. Adding a tab therefore re-measured the StackPanel but did not change the
/// ScrollViewer's desired size, so this panel was never invalidated; the stale viewport (Home only)
/// stayed in place and the deferred BringIntoView of the new tab scrolled Home out of view. The
/// stale <see cref="ScrollViewer.ExtentWidth"/> (updated only after layout) also kept a wider
/// overflow state alive after tabs were closed. The panel now measures the real extent itself and
/// re-measures whenever the scroller reports an extent change.
/// </remarks>
public sealed class SessionHeaderPanel : Panel
{
    public const double ArrowWidth = 28, AddWidth = 36, RowHeight = 44;
    double extent, viewport;
    bool overflow;
    ScrollViewer? observed;

    /// <summary>True while the tab extent exceeds the available width (arrows and list are needed).</summary>
    public bool IsOverflowing => overflow;
    /// <summary>The measured extent of all tab headers in DIP.</summary>
    public double TabExtent => extent;
    /// <summary>The width given to the visible tab viewport in DIP.</summary>
    public double TabViewport => viewport;
    public event EventHandler? OverflowChanged;

    protected override Size MeasureOverride(Size available)
    {
        if (InternalChildren.Count != 4) return new();
        var left = InternalChildren[0]; var scroll = (ScrollViewer)InternalChildren[1]; var right = InternalChildren[2]; var add = InternalChildren[3];
        Observe(scroll);
        add.Measure(new Size(AddWidth, RowHeight));
        // Unconstrained measure gives the true extent. ExtentWidth is not used: it is
        // published by the ScrollViewer only after layout and can belong to the previous tab set.
        scroll.Measure(new Size(double.PositiveInfinity, RowHeight));
        extent = scroll.DesiredSize.Width;
        double width = double.IsFinite(available.Width) ? available.Width : extent + AddWidth;
        bool wasOverflowing = overflow;
        overflow = extent + AddWidth > width + .5;
        var arrows = overflow ? Visibility.Visible : Visibility.Collapsed;
        if (left.Visibility != arrows) left.Visibility = arrows;
        if (right.Visibility != arrows) right.Visibility = arrows;
        left.Measure(new Size(ArrowWidth, RowHeight)); right.Measure(new Size(ArrowWidth, RowHeight));
        viewport = Math.Max(0, Math.Min(extent, width - AddWidth - (overflow ? ArrowWidth * 2 : 0)));
        scroll.Measure(new Size(viewport, RowHeight));
        if (wasOverflowing != overflow) Dispatcher.BeginInvoke(() => OverflowChanged?.Invoke(this, EventArgs.Empty));
        return new Size(Math.Min(width, viewport + AddWidth + (overflow ? ArrowWidth * 2 : 0)), RowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count != 4) return finalSize;
        double arrow = overflow ? ArrowWidth : 0, x = 0;
        InternalChildren[0].Arrange(new Rect(x, 0, arrow, RowHeight)); x += arrow;
        double width = Math.Max(0, Math.Min(viewport, finalSize.Width - arrow * 2 - AddWidth));
        InternalChildren[1].Arrange(new Rect(x, 0, width, RowHeight)); x += width;
        InternalChildren[2].Arrange(new Rect(x, 0, arrow, RowHeight)); x += arrow;
        InternalChildren[3].Arrange(new Rect(x, 0, AddWidth, RowHeight));
        return finalSize;
    }

    void Observe(ScrollViewer scroll)
    {
        if (ReferenceEquals(observed, scroll)) return;
        if (observed is not null) observed.ScrollChanged -= OnScrollChanged;
        observed = scroll;
        scroll.ScrollChanged += OnScrollChanged;
    }

    void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Added, removed or renamed headers change the extent without changing the scroller's
        // clamped desired size. Re-measure in the same layout cycle so the viewport follows.
        if (Math.Abs(e.ExtentWidthChange) > .1 || Math.Abs(extent - e.ExtentWidth) > .5) InvalidateMeasure();
    }
}
