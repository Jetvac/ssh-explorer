using System.Windows;
using System.Windows.Media;
using SSHExplorer.Monitoring;

namespace SSHExplorer.App;

public enum GraphMetric { Cpu, Memory }

public sealed class MetricGraph : FrameworkElement
{
    public static readonly DependencyProperty HistoryProperty = DependencyProperty.Register(nameof(History), typeof(IReadOnlyList<HistoryPoint>), typeof(MetricGraph), new FrameworkPropertyMetadata(Array.Empty<HistoryPoint>(), FrameworkPropertyMetadataOptions.AffectsRender, Changed));
    public static readonly DependencyProperty MetricProperty = DependencyProperty.Register(nameof(Metric), typeof(GraphMetric), typeof(MetricGraph), new FrameworkPropertyMetadata(GraphMetric.Cpu, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty NowProperty = DependencyProperty.Register(nameof(Now), typeof(DateTimeOffset), typeof(MetricGraph), new FrameworkPropertyMetadata(DateTimeOffset.UtcNow, FrameworkPropertyMetadataOptions.AffectsRender, Changed));
    public static readonly DependencyProperty WindowProperty = DependencyProperty.Register(nameof(Window), typeof(TimeSpan), typeof(MetricGraph), new FrameworkPropertyMetadata(TimeSpan.FromMinutes(4), FrameworkPropertyMetadataOptions.AffectsRender, Changed));
    public static readonly DependencyProperty GapThresholdProperty = DependencyProperty.Register(nameof(GapThreshold), typeof(TimeSpan), typeof(MetricGraph), new FrameworkPropertyMetadata(TimeSpan.FromSeconds(5), FrameworkPropertyMetadataOptions.AffectsRender));
    public IReadOnlyList<HistoryPoint> History { get => (IReadOnlyList<HistoryPoint>)GetValue(HistoryProperty); set => SetValue(HistoryProperty, value); }
    public GraphMetric Metric { get => (GraphMetric)GetValue(MetricProperty); set => SetValue(MetricProperty, value); }
    public DateTimeOffset Now { get => (DateTimeOffset)GetValue(NowProperty); set => SetValue(NowProperty, value); }
    public TimeSpan Window { get => (TimeSpan)GetValue(WindowProperty); set => SetValue(WindowProperty, value); }
    public TimeSpan GapThreshold { get => (TimeSpan)GetValue(GapThresholdProperty); set => SetValue(GapThresholdProperty, value); }
    public string IntervalLabel => $"{(Now - Window).LocalDateTime:HH:mm:ss} — {Now.LocalDateTime:HH:mm:ss}";
    public event Action? IntervalChanged;
    static void Changed(DependencyObject owner, DependencyPropertyChangedEventArgs args) => ((MetricGraph)owner).IntervalChanged?.Invoke();

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var area = new Rect(0, 0, ActualWidth, ActualHeight);
        var edge = (Brush)FindResource("Edge"); var ink = (Brush)FindResource("Accent");
        dc.DrawRectangle((Brush)FindResource("Paper"), new Pen(edge, 1), area);
        for (int i = 1; i < 5; i++) dc.DrawLine(new Pen(edge, .5), new Point(0, ActualHeight * i / 5), new Point(ActualWidth, ActualHeight * i / 5));
        for (int i = 1; i < 8; i++) dc.DrawLine(new Pen(edge, .5), new Point(ActualWidth * i / 8, 0), new Point(ActualWidth * i / 8, ActualHeight));
        dc.PushClip(new RectangleGeometry(area));
        foreach (var segment in BuildSegments(History, Metric, Now, Window, GapThreshold, RenderSize))
        {
            if (segment.Length == 1) { dc.DrawEllipse(ink, null, segment[0], 1.8, 1.8); continue; }
            var line = new StreamGeometry();
            using (var geometry = line.Open()) { geometry.BeginFigure(segment[0], false, false); geometry.PolyLineTo(segment.Skip(1).ToArray(), true, false); }
            line.Freeze();
            if (!SystemParameters.HighContrast && ink is SolidColorBrush color)
            {
                var fill = new StreamGeometry();
                using (var geometry = fill.Open())
                {
                    geometry.BeginFigure(new Point(segment[0].X, ActualHeight), true, true);
                    geometry.PolyLineTo(segment, true, false); geometry.LineTo(new Point(segment[^1].X, ActualHeight), true, false);
                }
                fill.Freeze(); dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(22, color.Color.R, color.Color.G, color.Color.B)), null, fill);
            }
            dc.DrawGeometry(null, new Pen(ink, SystemParameters.HighContrast ? 2 : 1.6), line);
        }
        dc.Pop();
    }

    // The renderer and headless timestamp/gap tests share this calculation.
    // Missing values, explicit gaps, reversed time and long intervals never
    // receive an interpolated line connecting them.
    internal static IReadOnlyList<Point[]> BuildSegments(IReadOnlyList<HistoryPoint> history, GraphMetric metric, DateTimeOffset now, TimeSpan window, TimeSpan gapThreshold, Size size)
    {
        if (window <= TimeSpan.Zero || gapThreshold <= TimeSpan.Zero || size.Width <= 0 || size.Height <= 0) return [];
        var segments = new List<Point[]>(); var current = new List<Point>(); DateTimeOffset? previous = null;
        void Break() { if (current.Count > 0) segments.Add(current.ToArray()); current.Clear(); previous = null; }
        var start = now - window;
        foreach (var sample in history)
        {
            var value = metric == GraphMetric.Cpu ? sample.CpuPercent : sample.MemoryPercent;
            if (sample.Timestamp < start || sample.Timestamp > now || sample.IsGap || value is null || !double.IsFinite(value.Value)) { Break(); continue; }
            if (previous is { } prior && (sample.Timestamp <= prior || sample.Timestamp - prior > gapThreshold)) Break();
            current.Add(new Point((sample.Timestamp - start).TotalSeconds / window.TotalSeconds * size.Width, (1 - Math.Clamp(value.Value, 0, 100) / 100) * size.Height));
            previous = sample.Timestamp;
        }
        Break(); return segments;
    }
}
