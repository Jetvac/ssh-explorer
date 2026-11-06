using System.Globalization;
using System.Windows;
using System.Windows.Media;
using SSHExplorer.Monitoring;

namespace SSHExplorer.App;

/// <summary>
/// Load of recorded servers for «Главная»: CPU over the last 24 hours (half-hour averages) and the latest CPU, memory
/// and fullest disk. Read in the background at most once a minute; the list reads only this cache.
/// </summary>
internal static class HomeMetrics
{
    public sealed record Summary(double[] Cpu, double? CpuNow, double? MemoryNow, double? DiskMax, string Text);
    static Dictionary<Guid, Summary> cache = [];
    static readonly object sync = new();
    static DateTimeOffset computed = DateTimeOffset.MinValue;
    static bool busy;
    public static event Action? Changed;

    public static Summary? Get(Guid server) { lock (sync) return cache.TryGetValue(server, out var s) ? s : null; }

    /// <summary>Recomputes in the background unless that happened during the last minute (or <paramref name="force"/>).</summary>
    public static void Refresh(bool force = false)
    {
        var now = DateTimeOffset.UtcNow;
        if (busy || !force && now - computed < TimeSpan.FromMinutes(1)) return;
        busy = true; computed = now;
        MetricsStore store;
        try { store = MetricsHub.Store; } catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { busy = false; return; }
        _ = Task.Run(() =>
        {
            var result = new Dictionary<Guid, Summary>();
            try
            {
                foreach (var id in store.Servers())
                {
                    var series = store.SeriesOf(id);
                    if (series.Count == 0) continue;
                    var cpu = store.Read(id, [MetricSeries.Cpu], now - TimeSpan.FromHours(24), now, MetricResolution.FiveMinutes)[MetricSeries.Cpu];
                    var buckets = new double[48];
                    for (int i = 0; i < 48; i++)
                    {
                        var start = now - TimeSpan.FromHours(24) + TimeSpan.FromMinutes(30 * i);
                        var inside = cpu.Where(p => p.Time >= start && p.Time < start + TimeSpan.FromMinutes(30)).ToArray();
                        buckets[i] = inside.Length > 0 ? inside.Average(p => p.Average) : double.NaN;
                    }
                    var latest = store.Latest(id, now);
                    double? Fresh(string name) => latest.TryGetValue(name, out var v) && now - v.Time < TimeSpan.FromMinutes(5) ? v.Value : null;
                    double? diskMax = latest.Where(p => MetricSeries.IsFileSystem(p.Key) && now - p.Value.Time < TimeSpan.FromMinutes(5)).Select(p => (double?)p.Value.Value).Max();
                    double? cpuNow = Fresh(MetricSeries.Cpu), memoryNow = Fresh(MetricSeries.Memory);
                    var parts = new List<string>();
                    if (cpuNow is { } c) parts.Add("ЦП " + c.ToString("0", CultureInfo.CurrentCulture) + " %");
                    if (memoryNow is { } m) parts.Add("пам. " + m.ToString("0", CultureInfo.CurrentCulture) + " %");
                    if (diskMax is { } d) parts.Add("диск " + d.ToString("0", CultureInfo.CurrentCulture) + " %");
                    result[id] = new Summary(buckets, cpuNow, memoryNow, diskMax, parts.Count > 0 ? string.Join(" · ", parts) : "нет свежих данных");
                }
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException) { }
            lock (sync) cache = result;
            busy = false;
            if (Application.Current?.Dispatcher is { } d2 && !d2.HasShutdownStarted) d2.BeginInvoke(() => Changed?.Invoke());
        });
    }
}

/// <summary>A tiny CPU line for the Home list (NaN — not recorded, drawn as a gap).</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(nameof(Values), typeof(double[]), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public double[]? Values { get => (double[]?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public Sparkline() { Width = 72; Height = 18; Margin = new Thickness(0, 0, 8, 0); SnapsToDevicePixels = true; }

    protected override void OnRender(DrawingContext dc)
    {
        if (Values is not { Length: > 1 } values) return;
        var brush = TryFindResource("Accent") as Brush ?? Brushes.SteelBlue;
        var edge = TryFindResource("Edge") as Brush ?? Brushes.LightGray;
        var pen = new Pen(brush, 1.2); pen.Freeze();
        var basePen = new Pen(edge, 1); basePen.Freeze();
        double w = ActualWidth, h = ActualHeight;
        dc.DrawLine(basePen, new Point(0, h - .5), new Point(w, h - .5));
        Point? previous = null;
        for (int i = 0; i < values.Length; i++)
        {
            if (!double.IsFinite(values[i])) { previous = null; continue; }
            var p = new Point(i * w / (values.Length - 1), h - 1 - Math.Clamp(values[i], 0, 100) / 100 * (h - 2));
            if (previous is { } q) dc.DrawLine(pen, q, p); else dc.DrawEllipse(brush, null, p, .8, .8);
            previous = p;
        }
    }
}
