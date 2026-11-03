using System.Windows;
using System.Windows.Media;

namespace SSHExplorer.App;

// An unbranded process symbol. It does not infer a Windows app or service type.
internal sealed class ProcessIcon : FrameworkElement
{
    static readonly DependencyProperty InkProperty = DependencyProperty.Register(nameof(Ink), typeof(Brush), typeof(ProcessIcon), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Ink { get => (Brush)GetValue(InkProperty); set => SetValue(InkProperty, value); }
    public ProcessIcon() { Width = 18; Height = 18; IsHitTestVisible = false; SetResourceReference(InkProperty, "Muted"); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); var pen = new Pen(Ink, 1);
        dc.DrawRoundedRectangle(null, pen, new Rect(1, 2, 16, 14), 1.5, 1.5);
        dc.DrawLine(pen, new Point(1, 6), new Point(17, 6));
        dc.DrawRectangle(Ink, null, new Rect(4, 9, 3, 4));
        dc.DrawLine(pen, new Point(9, 10), new Point(14, 10)); dc.DrawLine(pen, new Point(9, 13), new Point(13, 13));
    }
}
