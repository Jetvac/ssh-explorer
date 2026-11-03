using System.Windows;
using System.Windows.Controls;

namespace SSHExplorer.App;

/// <summary>Dialog footer: one right-aligned row, or a deliberate vertical stack when text will not fit.</summary>
public sealed class DialogActionPanel : Panel
{
    bool vertical;
    protected override Size MeasureOverride(Size available)
    {
        foreach (UIElement child in InternalChildren) child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double total = InternalChildren.Cast<UIElement>().Sum(c=>c.DesiredSize.Width);
        vertical = !double.IsInfinity(available.Width) && total > available.Width;
        if (vertical)
        {
            foreach (UIElement child in InternalChildren) child.Measure(new Size(available.Width, double.PositiveInfinity));
            return new Size(available.Width,InternalChildren.Cast<UIElement>().Sum(c=>c.DesiredSize.Height));
        }
        return new Size(total,InternalChildren.Count==0?0:InternalChildren.Cast<UIElement>().Max(c=>c.DesiredSize.Height));
    }
    protected override Size ArrangeOverride(Size final)
    {
        double position=vertical?0:Math.Max(0,final.Width-InternalChildren.Cast<UIElement>().Sum(c=>c.DesiredSize.Width));
        foreach (UIElement child in InternalChildren)
        {
            if(vertical){child.Arrange(new Rect(0,position,final.Width,child.DesiredSize.Height));position+=child.DesiredSize.Height;}
            else{child.Arrange(new Rect(position,0,child.DesiredSize.Width,final.Height));position+=child.DesiredSize.Width;}
        }
        return final;
    }
}
