using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SSHExplorer.Monitoring;

/// <summary>Conditions over recorded history: a value held beyond a threshold, a disk filling up.</summary>
public static class MetricAlerts
{
    /// <summary>
    /// True when every point of the last <paramref name="minutes"/> is beyond the threshold and the points cover that
    /// time (a gap or a fresh start is not enough to raise an alert).
    /// </summary>
    public static bool Sustained(IReadOnlyList<MetricPoint> points, DateTimeOffset now, bool above, double threshold, int minutes, TimeSpan interval)
    {
        if (minutes < 1 || interval <= TimeSpan.Zero) return false;
        var window = TimeSpan.FromMinutes(minutes);
        var recent = points.Where(p => p.Time > now - window - interval && p.Time <= now).OrderBy(p => p.Time).ToArray();
        if (recent.Length == 0 || now - recent[^1].Time > interval * 2.5) return false;
        if (recent[0].Time > now - window + interval * 1.5) return false; // history does not reach back far enough
        int expected = (int)Math.Max(1, window / interval);
        if (recent.Length < Math.Max(1, (int)(expected * 0.7))) return false;
        for (int i = 1; i < recent.Length; i++) if (recent[i].Time - recent[i - 1].Time > interval * 3) return false; // a hole
        return recent.All(p => above ? p.Average > threshold : p.Average < threshold);
    }

    /// <summary>
    /// Days until a percentage reaches 100 at the linear trend of the points (at least a day of history and 12 points);
    /// null when the value is not growing or history is too short.
    /// </summary>
    public static double? DaysUntilFull(IReadOnlyList<MetricPoint> points)
    {
        if (points.Count < 12) return null;
        var ordered = points.OrderBy(p => p.Time).ToArray();
        if (ordered[^1].Time - ordered[0].Time < TimeSpan.FromDays(1)) return null;
        double t0 = ordered[0].Time.ToUnixTimeSeconds();
        double n = ordered.Length, sx = 0, sy = 0, sxx = 0, sxy = 0;
        foreach (var p in ordered) { double x = (p.Time.ToUnixTimeSeconds() - t0) / 86400d, y = p.Average; sx += x; sy += y; sxx += x * x; sxy += x * y; }
        double denominator = n * sxx - sx * sx;
        if (Math.Abs(denominator) < 1e-12) return null;
        double slope = (n * sxy - sx * sy) / denominator; // percent per day
        if (slope < 0.01) return null;
        double current = ordered[^1].Average;
        return current >= 100 ? 0 : (100 - current) / slope;
    }

    /// <summary>Whether a rule's series pattern covers a series: an exact name or a prefix ending in «*» (fs:* — every disk).</summary>
    public static bool Matches(string pattern, string series) => pattern.EndsWith('*') ? series.StartsWith(pattern[..^1], StringComparison.Ordinal) : pattern == series;
}

/// <summary>CSV for spreadsheets and the Prometheus text format.</summary>
public static class MetricsExport
{
    /// <summary>
    /// One row per time, one column per series («;» separated, numbers in <paramref name="culture"/>). Rolled-up points
    /// also get minimum and maximum columns. Times are in <paramref name="zone"/>.
    /// </summary>
    public static string Csv(IReadOnlyDictionary<string, IReadOnlyList<MetricPoint>> data, TimeZoneInfo zone, CultureInfo culture)
    {
        var names = data.Keys.ToArray();
        bool rolled = data.Values.SelectMany(v => v).Any(p => p.Count > 1);
        var b = new StringBuilder();
        b.Append("Время");
        foreach (var n in names)
        {
            var label = Quote(MetricSeries.Label(n) + (MetricSeries.UnitOf(n) is { Length: > 0 } u ? ", " + u : ""));
            b.Append(';').Append(label);
            if (rolled) b.Append(';').Append(Quote(MetricSeries.Label(n) + ", мин")).Append(';').Append(Quote(MetricSeries.Label(n) + ", макс"));
        }
        b.Append("\r\n");
        var byTime = names.ToDictionary(n => n, n => data[n].GroupBy(p => p.Time).ToDictionary(g => g.Key, g => g.First()));
        foreach (var time in data.Values.SelectMany(v => v).Select(p => p.Time).Distinct().OrderBy(t => t))
        {
            b.Append(TimeZoneInfo.ConvertTime(time, zone).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            foreach (var n in names)
            {
                bool has = byTime[n].TryGetValue(time, out var p);
                b.Append(';'); if (has) b.Append(p.Average.ToString("0.###", culture));
                if (rolled) { b.Append(';'); if (has) b.Append(p.Min.ToString("0.###", culture)); b.Append(';'); if (has) b.Append(p.Max.ToString("0.###", culture)); }
            }
            b.Append("\r\n");
        }
        return b.ToString();
    }

    static string Quote(string text) => text.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;

    /// <summary>
    /// Latest values in the Prometheus text format. Only the profile name is a label (never an address or a user);
    /// values older than <paramref name="maxAge"/> are left out.
    /// </summary>
    public static string Prometheus(IEnumerable<(string Server, IReadOnlyDictionary<string, (DateTimeOffset Time, double Value)> Latest)> servers, DateTimeOffset now, TimeSpan maxAge)
    {
        var families = new SortedDictionary<string, (string Help, List<string> Lines)>(StringComparer.Ordinal);
        void Add(string family, string help, string labels, double value)
        {
            if (!families.TryGetValue(family, out var f)) families[family] = f = (help, new List<string>());
            f.Lines.Add(family + "{" + labels + "} " + value.ToString("R", CultureInfo.InvariantCulture));
        }
        foreach (var (server, latest) in servers)
        {
            var s = "server=\"" + Escape(server) + "\"";
            foreach (var (name, (time, value)) in latest)
            {
                if (now - time > maxAge || !double.IsFinite(value)) continue;
                switch (name)
                {
                    case MetricSeries.Cpu: Add("sshx_cpu_percent", "CPU usage, percent.", s, value); break;
                    case MetricSeries.Memory: Add("sshx_memory_percent", "Memory in use, percent.", s, value); break;
                    case MetricSeries.MemoryUsed: Add("sshx_memory_used_bytes", "Memory in use, bytes.", s, value); break;
                    case MetricSeries.Swap: Add("sshx_swap_percent", "Swap in use, percent.", s, value); break;
                    case MetricSeries.Load1: Add("sshx_load1", "Load average over 1 minute.", s, value); break;
                    case MetricSeries.NetReceive: Add("sshx_network_receive_bytes_per_second", "Network receive rate.", s, value); break;
                    case MetricSeries.NetSend: Add("sshx_network_send_bytes_per_second", "Network send rate.", s, value); break;
                    case MetricSeries.DiskRead: Add("sshx_disk_read_bytes_per_second", "Disk read rate.", s, value); break;
                    case MetricSeries.DiskWrite: Add("sshx_disk_write_bytes_per_second", "Disk write rate.", s, value); break;
                    default:
                        if (MetricSeries.IsFileSystem(name)) Add("sshx_filesystem_used_percent", "File system fullness, percent.", s + ",mount=\"" + Escape(name[MetricSeries.FileSystemPrefix.Length..]) + "\"", value);
                        else if (MetricSeries.IsCustom(name)) Add("sshx_custom", "Custom metric value.", s + ",metric=\"" + Escape(name[MetricSeries.CustomPrefix.Length..]) + "\"", value);
                        break;
                }
            }
        }
        var b = new StringBuilder();
        foreach (var (family, (help, lines)) in families)
        {
            b.Append("# HELP ").Append(family).Append(' ').Append(help).Append('\n');
            b.Append("# TYPE ").Append(family).Append(" gauge\n");
            foreach (var line in lines) b.Append(line).Append('\n');
        }
        return b.ToString();
    }

    static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
}

/// <summary>
/// A tiny HTTP endpoint for Prometheus: <c>GET /metrics</c> on 127.0.0.1 only. No http.sys registration and no
/// administrator rights are needed; every other request gets 404.
/// </summary>
public sealed class PrometheusEndpoint : IDisposable
{
    readonly TcpListener listener;
    readonly Func<string> body;
    readonly CancellationTokenSource stop = new();
    public int Port { get; }

    public PrometheusEndpoint(int port, Func<string> body)
    {
        this.body = body;
        listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = AcceptAsync();
    }

    async Task AcceptAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stop.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            _ = ServeAsync(client);
        }
    }

    async Task ServeAsync(TcpClient client)
    {
        using var owned = client;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        limit.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var stream = client.GetStream();
            var buffer = new byte[8192]; int read = 0;
            while (read < buffer.Length)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(read), limit.Token).ConfigureAwait(false);
                if (n == 0) break;
                read += n;
                if (Encoding.ASCII.GetString(buffer, 0, read).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
            }
            var request = Encoding.ASCII.GetString(buffer, 0, read);
            var line = request.Split("\r\n")[0].Split(' ');
            bool ok = line.Length >= 2 && line[0] == "GET" && (line[1] == "/metrics" || line[1].StartsWith("/metrics?", StringComparison.Ordinal));
            var content = ok ? body() : "Not found\n";
            var bytes = Encoding.UTF8.GetBytes(content);
            var header = $"HTTP/1.1 {(ok ? "200 OK" : "404 Not Found")}\r\nContent-Type: text/plain; version=0.0.4; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), limit.Token).ConfigureAwait(false);
            await stream.WriteAsync(bytes, limit.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException) { }
    }

    public void Dispose() { stop.Cancel(); listener.Stop(); }
}
