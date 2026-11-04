using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using SSHExplorer.Monitoring;

namespace SSHExplorer.App;

public enum GraphMetric { Cpu, Memory }

/// <summary>
/// Percent-over-time graph with a Task Manager layout: grid, 0/50/100 % axis labels, the time
/// window, the latest value and a hover inspector showing the value at a moment.
/// </summary>
/// <remarks>
/// Line continuity follows <see cref="HistoryPoint.ContinuesSeries"/> from <see cref="LinuxMonitor"/>;
/// the time threshold applies only to points without that flag. No fill is drawn before the first
/// real value, missing values are never drawn as zero and no smoothing or interpolation is added.
/// </remarks>
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
    public string WindowLabel => Window.TotalMinutes >= 1 && Window.TotalSeconds % 60 == 0 ? $"{Window.TotalMinutes:0} мин" : $"{Window.TotalSeconds:0} с";
    public event Action? IntervalChanged;
    /// <summary>The sample currently inspected with the pointer (null when not hovering).</summary>
    public HistoryPoint? Inspected { get; private set; }
    Point? pointer;
    const double LabelGutter = 40;

    public MetricGraph()
    {
        Focusable = false; SnapsToDevicePixels = true; Cursor = Cursors.Cross;
        MouseMove += (_, e) => { pointer = e.GetPosition(this); UpdateInspection(); };
        MouseLeave += (_, _) => { pointer = null; UpdateInspection(); };
        Theme.Changed += InvalidateVisual;
        Unloaded += (_, _) => Theme.Changed -= InvalidateVisual;
        Loaded += (_, _) => { Theme.Changed -= InvalidateVisual; Theme.Changed += InvalidateVisual; };
    }

    static void Changed(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var graph = (MetricGraph)owner; graph.IntervalChanged?.Invoke(); graph.UpdateInspection(); graph.UpdateAutomation();
    }

    Rect PlotArea => new(0, 0, Math.Max(0, ActualWidth - LabelGutter), ActualHeight);

    void UpdateInspection()
    {
        var area = PlotArea;
        HistoryPoint? found = null;
        if (pointer is { } at && area.Width > 0 && at.X <= area.Right)
        {
            var time = Now - Window + TimeSpan.FromTicks((long)(Window.Ticks * Math.Clamp(at.X / area.Width, 0, 1)));
            found = Nearest(History, Metric, time, Window, area.Width);
        }
        if (!Equals(found, Inspected)) { Inspected = found; InvalidateVisual(); }
        else if (pointer is not null) InvalidateVisual();
    }

    void UpdateAutomation()
    {
        var latest = History.LastOrDefault(p => !p.IsGap && Value(p, Metric) is not null);
        AutomationProperties.SetHelpText(this, latest is null ? "Нет измеренных значений в окне " + WindowLabel
            : $"Последнее значение {Format(Value(latest, Metric))} в {latest.Timestamp.LocalDateTime:HH:mm:ss}; окно {WindowLabel}");
    }

    internal static double? Value(HistoryPoint point, GraphMetric metric) => metric == GraphMetric.Cpu ? point.CpuPercent : point.MemoryPercent;
    static string Format(double? value) => value is { } v ? v.ToString("0", CultureInfo.CurrentCulture) + " %" : "—";

    /// <summary>The closest real sample within half a pixel column of the pointer time.</summary>
    internal static HistoryPoint? Nearest(IReadOnlyList<HistoryPoint> history, GraphMetric metric, DateTimeOffset time, TimeSpan window, double width)
    {
        HistoryPoint? best = null; double bestDistance = double.MaxValue;
        var tolerance = Math.Max(window.TotalSeconds / Math.Max(1, width) * 24, 0.5);
        foreach (var point in history)
        {
            if (point.IsGap || Value(point, metric) is not { } value || !double.IsFinite(value)) continue;
            var distance = Math.Abs((point.Timestamp - time).TotalSeconds);
            if (distance < bestDistance) { bestDistance = distance; best = point; }
        }
        return bestDistance <= tolerance ? best : null;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var area = PlotArea;
        Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;
        var edge = Res("Edge", Brushes.LightGray); var ink = Res("Accent", Brushes.SteelBlue); var muted = Res("Muted", Brushes.Gray); var paper = Res("Paper", Brushes.White); var text = Res("Ink", Brushes.Black);
        var gridPen = new Pen(edge, .5); gridPen.Freeze();
        dc.DrawRectangle(paper, new Pen(edge, 1), area);
        for (int i = 1; i < 4; i++) dc.DrawLine(gridPen, new Point(area.Left, area.Height * i / 4), new Point(area.Right, area.Height * i / 4));
        for (int i = 1; i < 8; i++) dc.DrawLine(gridPen, new Point(area.Width * i / 8, 0), new Point(area.Width * i / 8, area.Height));
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        FormattedText Label(string value, Brush brush, double size = 11) => new(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush, dpi);
        foreach (var (label, y) in new[] { ("100 %", 0d), ("50 %", area.Height / 2), ("0 %", area.Height) })
        {
            var t = Label(label, muted); dc.DrawText(t, new Point(area.Right + 6, Math.Clamp(y - t.Height / 2, 0, Math.Max(0, area.Height - t.Height))));
        }
        dc.PushClip(new RectangleGeometry(area));
        var segments = BuildSegments(History, Metric, Now, Window, GapThreshold, area.Size);
        bool contrast = SystemParameters.HighContrast;
        foreach (var segment in segments)
        {
            if (segment.Length == 1) { dc.DrawEllipse(ink, null, segment[0], 2, 2); continue; }
            var line = new StreamGeometry();
            using (var geometry = line.Open()) { geometry.BeginFigure(segment[0], false, false); geometry.PolyLineTo(segment.Skip(1).ToArray(), true, false); }
            line.Freeze();
            if (!contrast && ink is SolidColorBrush color)
            {
                var fill = new StreamGeometry();
                using (var geometry = fill.Open())
                {
                    geometry.BeginFigure(new Point(segment[0].X, area.Height), true, true);
                    geometry.PolyLineTo(segment, true, false); geometry.LineTo(new Point(segment[^1].X, area.Height), true, false);
                }
                fill.Freeze(); dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(28, color.Color.R, color.Color.G, color.Color.B)), null, fill);
            }
            dc.DrawGeometry(null, new Pen(ink, contrast ? 2 : 1.5) { LineJoin = PenLineJoin.Round }, line);
        }
        if (Inspected is { } inspected && Value(inspected, Metric) is { } inspectedValue && area.Width > 0)
        {
            var x = (inspected.Timestamp - (Now - Window)).TotalSeconds / Window.TotalSeconds * area.Width;
            var y = (1 - Math.Clamp(inspectedValue, 0, 100) / 100) * area.Height;
            var cursorPen = new Pen(text, 1) { DashStyle = DashStyles.Dash }; cursorPen.Freeze();
            dc.DrawLine(cursorPen, new Point(x, 0), new Point(x, area.Height));
            dc.DrawEllipse(paper, new Pen(ink, 2), new Point(x, y), 3.5, 3.5);
            var caption = Label($"{inspected.Timestamp.LocalDateTime:HH:mm:ss} · {Format(inspectedValue)}", text, 12);
            double boxWidth = caption.Width + 12, boxHeight = caption.Height + 6;
            double bx = x + 8 + boxWidth > area.Right ? x - 8 - boxWidth : x + 8, by = Math.Clamp(y - boxHeight - 6, 2, Math.Max(2, area.Height - boxHeight - 2));
            dc.DrawRoundedRectangle(paper, new Pen(edge, 1), new Rect(bx, by, boxWidth, boxHeight), 4, 4);
            dc.DrawText(caption, new Point(bx + 6, by + 3));
        }
        dc.Pop();
    }

    // The renderer and headless timestamp/gap tests share this calculation.
    // Missing values, explicit gaps and reversed time never receive a connecting line.
    // Monitor points carry ContinuesSeries; other sources fall back to the time threshold.
    internal static IReadOnlyList<Point[]> BuildSegments(IReadOnlyList<HistoryPoint> history, GraphMetric metric, DateTimeOffset now, TimeSpan window, TimeSpan gapThreshold, Size size)
    {
        if (window <= TimeSpan.Zero || gapThreshold <= TimeSpan.Zero || size.Width <= 0 || size.Height <= 0) return [];
        var segments = new List<Point[]>(); var current = new List<Point>(); DateTimeOffset? previous = null;
        void Break() { if (current.Count > 0) segments.Add(current.ToArray()); current.Clear(); previous = null; }
        var start = now - window;
        foreach (var sample in history)
        {
            var value = Value(sample, metric);
            if (sample.IsGap || value is null || !double.IsFinite(value.Value)) { Break(); continue; }
            bool joined = previous is { } prior && sample.Timestamp > prior && (sample.ContinuesSeries ?? sample.Timestamp - prior <= gapThreshold);
            if (previous is not null && !joined) Break();
            if (sample.Timestamp > now) { Break(); continue; }
            var point = new Point((sample.Timestamp - start).TotalSeconds / window.TotalSeconds * size.Width, (1 - Math.Clamp(value.Value, 0, 100) / 100) * size.Height);
            // Points before the window are kept only as the left anchor of a continuing line.
            if (sample.Timestamp < start) { current.Clear(); current.Add(point); previous = sample.Timestamp; continue; }
            current.Add(point); previous = sample.Timestamp;
        }
        Break();
        // A lone point left of the window (anchor without a successor) is not visible data.
        return segments.Where(segment => segment.Length > 1 || segment[0].X >= 0).ToArray();
    }
}
