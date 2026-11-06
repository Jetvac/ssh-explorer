using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using SSHExplorer.Monitoring;

namespace SSHExplorer.App;

/// <summary>One line on a history chart. <see cref="Shift"/> moves it in time (the previous period drawn over this one).</summary>
public sealed record ChartSeries(string Name, string Label, string Unit, IReadOnlyList<MetricPoint> Points, Color Color, bool Dashed = false, TimeSpan Shift = default);

/// <summary>
/// History chart: lines with gaps where nothing was recorded (never a zero), a minimum–maximum band for rolled-up
/// points, event marks, a crosshair with exact values, drag to zoom and click to pick a moment.
/// </summary>
public sealed class HistoryChart : FrameworkElement
{
    IReadOnlyList<ChartSeries> series = [];
    IReadOnlyList<MetricEvent> events = [];
    DateTimeOffset from = DateTimeOffset.UtcNow.AddHours(-1), to = DateTimeOffset.UtcNow;
    TimeSpan step = TimeSpan.FromMinutes(1);
    Point? pointer, dragStart;
    const double Gutter = 70, Header = 22, Axis = 18, Marks = 10;

    public string Title { get; set; } = "";
    public event Action<DateTimeOffset, DateTimeOffset>? RangeSelected;
    public event Action<DateTimeOffset>? PointPicked;
    public event Action<DateTimeOffset?>? Hovered;
    public IReadOnlyList<ChartSeries> Series => series;
    /// <summary>The moment under the pointer (null when not hovering).</summary>
    public DateTimeOffset? HoverTime { get; private set; }

    public HistoryChart()
    {
        Height = 190; Focusable = false; SnapsToDevicePixels = true; Cursor = Cursors.Cross;
        MouseMove += (_, e) => { pointer = e.GetPosition(this); UpdateHover(); InvalidateVisual(); };
        MouseLeave += (_, _) => { pointer = null; if (dragStart is null) { HoverTime = null; Hovered?.Invoke(null); } InvalidateVisual(); };
        MouseLeftButtonDown += (_, e) => { dragStart = e.GetPosition(this); CaptureMouse(); e.Handled = true; };
        MouseLeftButtonUp += (_, e) =>
        {
            if (dragStart is not { } start) return;
            dragStart = null; ReleaseMouseCapture();
            var end = e.GetPosition(this);
            if (Math.Abs(end.X - start.X) >= 8) { var a = TimeAt(Math.Min(start.X, end.X)); var b = TimeAt(Math.Max(start.X, end.X)); if (b > a) RangeSelected?.Invoke(a, b); }
            else PointPicked?.Invoke(TimeAt(end.X));
            InvalidateVisual();
        };
        Theme.Changed += InvalidateVisual;
        Unloaded += (_, _) => Theme.Changed -= InvalidateVisual;
        Loaded += (_, _) => { Theme.Changed -= InvalidateVisual; Theme.Changed += InvalidateVisual; };
    }

    /// <summary>Shows new data. <paramref name="step"/> is the expected spacing of points: a longer silence is a gap.</summary>
    public void SetData(IReadOnlyList<ChartSeries> data, DateTimeOffset start, DateTimeOffset end, TimeSpan step, IReadOnlyList<MetricEvent> marks)
    {
        series = data; from = start; to = end > start ? end : start.AddMinutes(1); this.step = step > TimeSpan.Zero ? step : TimeSpan.FromMinutes(1); events = marks;
        var latest = data.Select(s => (s, Last: s.Points.Count > 0 ? s.Points[^1] : (MetricPoint?)null)).Where(x => x.Last is not null)
            .Select(x => $"{x.s.Label}: {MetricSeries.Format(x.Last!.Value.Average, x.s.Unit)}");
        AutomationProperties.SetName(this, Title);
        AutomationProperties.SetHelpText(this, data.All(s => s.Points.Count == 0) ? "Нет записанных значений за выбранный период" : "Последние значения — " + string.Join("; ", latest));
        UpdateHover(); InvalidateVisual();
    }

    Rect Plot => new(0, Header + Marks, Math.Max(0, ActualWidth - Gutter), Math.Max(0, ActualHeight - Header - Marks - Axis));
    TimeSpan Span => to - from;
    DateTimeOffset TimeAt(double x) => from + TimeSpan.FromTicks((long)(Span.Ticks * Math.Clamp(x / Math.Max(1, Plot.Width), 0, 1)));
    double XOf(DateTimeOffset time) => (time - from).TotalSeconds / Math.Max(1, Span.TotalSeconds) * Plot.Width;

    void UpdateHover()
    {
        DateTimeOffset? time = pointer is { } p && p.X <= Plot.Right ? TimeAt(p.X) : null;
        if (time != HoverTime) { HoverTime = time; Hovered?.Invoke(time); }
    }

    /// <summary>The y range: 0–100 when every line is a percentage, otherwise a rounded range around the data.</summary>
    internal static (double Min, double Max) Scale(IReadOnlyList<ChartSeries> data)
    {
        var values = data.SelectMany(s => s.Points.SelectMany(p => new[] { p.Min, p.Max, p.Average })).Where(double.IsFinite).ToArray();
        if (data.Count > 0 && data.All(s => s.Unit == MetricSeries.Percent) && (values.Length == 0 || values.Max() <= 100)) return (0, 100);
        if (values.Length == 0) return (0, 1);
        double low = Math.Min(0, values.Min()), high = values.Max();
        if (high <= low) high = low + 1;
        return (low < 0 ? -Nice(-low) : 0, Nice(high * 1.08));
    }

    internal static double Nice(double value)
    {
        if (value <= 0 || !double.IsFinite(value)) return 1;
        double power = Math.Pow(10, Math.Floor(Math.Log10(value)));
        foreach (var f in new[] { 1d, 2, 2.5, 5, 10 }) if (f * power >= value) return f * power;
        return 10 * power;
    }

    /// <summary>Line pieces: a new piece starts after a silence longer than 2.5 steps.</summary>
    internal static IReadOnlyList<MetricPoint[]> Segments(IReadOnlyList<MetricPoint> points, TimeSpan step)
    {
        var result = new List<MetricPoint[]>(); var current = new List<MetricPoint>();
        foreach (var p in points.OrderBy(p => p.Time))
        {
            if (current.Count > 0 && p.Time - current[^1].Time > step * 2.5) { result.Add(current.ToArray()); current.Clear(); }
            current.Add(p);
        }
        if (current.Count > 0) result.Add(current.ToArray());
        return result;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var plot = Plot;
        if (plot.Width <= 4 || plot.Height <= 4) return;
        Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;
        var edge = Res("Edge", Brushes.LightGray); var muted = Res("Muted", Brushes.Gray); var paper = Res("Paper", Brushes.White); var ink = Res("Ink", Brushes.Black); var danger = Res("DangerFill", Brushes.Firebrick); var accent = Res("Accent", Brushes.SteelBlue);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var bold = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        FormattedText Label(string value, Brush brush, double size = 11, Typeface? face = null) => new(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face ?? typeface, size, brush, dpi);

        // Title and legend.
        double x0 = 0;
        var title = Label(Title, ink, 12, bold); dc.DrawText(title, new Point(0, 2)); x0 = title.Width + 14;
        foreach (var s in series)
        {
            var brush = new SolidColorBrush(s.Color); brush.Freeze();
            var text = Label(s.Label, muted);
            if (x0 + 18 + text.Width > ActualWidth) break;
            var pen = new Pen(brush, 2) { DashStyle = s.Dashed ? DashStyles.Dash : DashStyles.Solid }; pen.Freeze();
            dc.DrawLine(pen, new Point(x0, 10), new Point(x0 + 12, 10));
            dc.DrawText(text, new Point(x0 + 16, 3)); x0 += 16 + text.Width + 14;
        }

        var gridPen = new Pen(edge, .5); gridPen.Freeze();
        dc.DrawRectangle(paper, new Pen(edge, 1), plot);
        var (low, high) = Scale(series);
        string unit = series.Select(s => s.Unit).Distinct().Count() == 1 ? series[0].Unit : "";
        for (int i = 0; i <= 4; i++)
        {
            double y = plot.Top + plot.Height * i / 4;
            if (i is > 0 and < 4) dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var t = Label(MetricSeries.Format(high - (high - low) * i / 4, unit), muted);
            dc.DrawText(t, new Point(plot.Right + 6, Math.Clamp(y - t.Height / 2, plot.Top - 4, plot.Bottom - t.Height + 4)));
        }
        string format = Span <= TimeSpan.FromDays(2) ? (from.ToLocalTime().Date == to.ToLocalTime().Date ? "HH:mm" : "dd.MM HH:mm") : "dd.MM";
        for (int i = 0; i <= 4; i++)
        {
            double x = plot.Width * i / 4;
            if (i is > 0 and < 4) dc.DrawLine(gridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            var t = Label(TimeAt(x).ToLocalTime().ToString(format, CultureInfo.CurrentCulture), muted);
            dc.DrawText(t, new Point(Math.Clamp(x - t.Width / 2, 0, plot.Right - t.Width), plot.Bottom + 2));
        }

        double Y(double v) => plot.Bottom - (Math.Clamp(v, low, high) - low) / (high - low) * plot.Height;
        dc.PushClip(new RectangleGeometry(plot));
        bool contrast = SystemParameters.HighContrast;
        foreach (var s in series)
        {
            var brush = new SolidColorBrush(s.Color); brush.Freeze();
            var pen = new Pen(brush, contrast ? 2 : 1.5) { LineJoin = PenLineJoin.Round, DashStyle = s.Dashed ? DashStyles.Dash : DashStyles.Solid }; pen.Freeze();
            var band = new SolidColorBrush(Color.FromArgb(40, s.Color.R, s.Color.G, s.Color.B)); band.Freeze();
            foreach (var segment in Segments(s.Points, step))
            {
                var pts = segment.Select(p => new Point(XOf(p.Time + s.Shift), Y(p.Average))).ToArray();
                if (!contrast && segment.Any(p => p.Count > 1 && p.Max > p.Min))
                {
                    var g = new StreamGeometry();
                    using (var c = g.Open())
                    {
                        c.BeginFigure(new Point(XOf(segment[0].Time + s.Shift), Y(segment[0].Max)), true, true);
                        c.PolyLineTo(segment.Skip(1).Select(p => new Point(XOf(p.Time + s.Shift), Y(p.Max))).Concat(Enumerable.Reverse(segment).Select(p => new Point(XOf(p.Time + s.Shift), Y(p.Min)))).ToArray(), true, false);
                    }
                    g.Freeze(); dc.DrawGeometry(band, null, g);
                }
                if (pts.Length == 1) { dc.DrawEllipse(brush, null, pts[0], 2, 2); continue; }
                var line = new StreamGeometry();
                using (var c = line.Open()) { c.BeginFigure(pts[0], false, false); c.PolyLineTo(pts.Skip(1).ToArray(), true, false); }
                line.Freeze(); dc.DrawGeometry(null, pen, line);
            }
        }
        dc.Pop();

        // Event marks above the plot.
        foreach (var e in events)
        {
            double x = XOf(e.Time); if (x < 0 || x > plot.Width) continue;
            var brush = e.Kind is "alert" or "offline" ? danger : e.Kind is "reboot" ? accent : muted;
            var mark = new StreamGeometry();
            using (var c = mark.Open()) { c.BeginFigure(new Point(x - 4, Header + 1), true, true); c.LineTo(new Point(x + 4, Header + 1), true, false); c.LineTo(new Point(x, Header + Marks - 1), true, false); }
            mark.Freeze(); dc.DrawGeometry(brush, null, mark);
            var pen = new Pen(brush, .6) { DashStyle = DashStyles.Dot }; pen.Freeze();
            dc.DrawLine(pen, new Point(x, plot.Top), new Point(x, plot.Bottom));
        }

        if (dragStart is { } ds && pointer is { } dp)
        {
            var sel = new SolidColorBrush(Color.FromArgb(50, 0, 120, 215)); sel.Freeze();
            dc.DrawRectangle(sel, null, new Rect(new Point(Math.Clamp(Math.Min(ds.X, dp.X), 0, plot.Right), plot.Top), new Point(Math.Clamp(Math.Max(ds.X, dp.X), 0, plot.Right), plot.Bottom)));
        }
        else if (HoverTime is { } hover)
        {
            double x = XOf(hover);
            var cursor = new Pen(ink, 1) { DashStyle = DashStyles.Dash }; cursor.Freeze();
            dc.DrawLine(cursor, new Point(x, plot.Top), new Point(x, plot.Bottom));
            var lines = new List<string> { hover.ToLocalTime().ToString(Span <= TimeSpan.FromDays(2) ? "dd.MM HH:mm" : "dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture) };
            foreach (var s in series)
                if (Nearest(s.Points, hover - s.Shift, step) is { } p)
                {
                    dc.DrawEllipse(paper, new Pen(new SolidColorBrush(s.Color), 2), new Point(XOf(p.Time + s.Shift), Y(p.Average)), 3, 3);
                    lines.Add($"{s.Label}: {MetricSeries.Format(p.Average, s.Unit)}" + (p.Count > 1 && p.Max > p.Min ? $" (мин {MetricSeries.Format(p.Min, s.Unit)}, макс {MetricSeries.Format(p.Max, s.Unit)})" : ""));
                }
                else lines.Add($"{s.Label}: нет данных");
            double tolerance = Span.TotalSeconds / Math.Max(1, plot.Width) * 6;
            foreach (var e in events.Where(e => Math.Abs((e.Time - hover).TotalSeconds) <= tolerance)) lines.Add("▼ " + e.Text);
            var caption = Label(string.Join("\n", lines), ink, 12);
            double w = caption.Width + 12, h = caption.Height + 8;
            double bx = x + 10 + w > plot.Right ? Math.Max(0, x - 10 - w) : x + 10, by = plot.Top + 4;
            dc.DrawRoundedRectangle(paper, new Pen(edge, 1), new Rect(bx, by, w, h), 4, 4);
            dc.DrawText(caption, new Point(bx + 6, by + 4));
        }
    }

    /// <summary>The recorded point nearest to a moment, if one is within 1.5 steps.</summary>
    internal static MetricPoint? Nearest(IReadOnlyList<MetricPoint> points, DateTimeOffset time, TimeSpan step)
    {
        MetricPoint? best = null; double distance = double.MaxValue;
        foreach (var p in points) { var d = Math.Abs((p.Time - time).TotalSeconds); if (d < distance) { distance = d; best = p; } }
        return distance <= Math.Max(30, step.TotalSeconds * 1.5) ? best : null;
    }
}
