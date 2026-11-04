using System.Windows;
using System.Windows.Controls;

namespace SSHExplorer.App;

/// <summary>Tabs take only their actual extent; overflow reserves equal, stable arrow areas.</summary>
public sealed class SessionHeaderPanel : Panel
{
    double extent, viewport;
    bool overflow;
    protected override Size MeasureOverride(Size available)
    {
        if (InternalChildren.Count != 4) return new();
        var left = InternalChildren[0]; var scroll = (ScrollViewer)InternalChildren[1]; var right = InternalChildren[2]; var add = InternalChildren[3];
        add.Measure(new Size(36, 44)); scroll.Measure(new Size(double.PositiveInfinity, 44));
        extent = Math.Max(scroll.DesiredSize.Width, scroll.ExtentWidth);
        double width = double.IsFinite(available.Width) ? available.Width : extent + 36;
        overflow = extent + 36 > width + .5;
        left.Visibility = right.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
        left.Measure(new Size(28, 44)); right.Measure(new Size(28, 44));
        viewport = Math.Max(0, Math.Min(extent, width - 36 - (overflow ? 56 : 0)));
        scroll.Measure(new Size(viewport, 44));
        return new Size(Math.Min(width, viewport + 36 + (overflow ? 56 : 0)), 44);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count != 4) return finalSize;
        double arrow = overflow ? 28 : 0, x = 0;
        InternalChildren[0].Arrange(new Rect(x, 0, arrow, 44)); x += arrow;
        double width = Math.Max(0, Math.Min(viewport, finalSize.Width - arrow * 2 - 36));
        InternalChildren[1].Arrange(new Rect(x, 0, width, 44)); x += width;
        InternalChildren[2].Arrange(new Rect(x, 0, arrow, 44)); x += arrow;
        InternalChildren[3].Arrange(new Rect(x, 0, 36, 44)); return finalSize;
    }
}
