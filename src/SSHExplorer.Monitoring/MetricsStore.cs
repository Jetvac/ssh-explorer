using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SSHExplorer.Monitoring;

/// <summary>Detail of stored points: every sample, five-minute and hourly roll-ups (average, minimum, maximum).</summary>
public enum MetricResolution { Raw, FiveMinutes, Hour }

/// <summary>One stored point. Raw points have <see cref="Count"/> 1 and equal average, minimum and maximum.</summary>
public readonly record struct MetricPoint(DateTimeOffset Time, double Average, double Min, double Max, int Count);

/// <summary>One value to store: a series name (see <see cref="MetricSeries"/>), the value and its unit.</summary>
public readonly record struct MetricValue(string Name, double Value, string Unit = "");

/// <summary>Something that happened on a server, drawn as a mark over the history graphs.</summary>
public sealed record MetricEvent(DateTimeOffset Time, string Kind, string Text);

public sealed record ProcessTopEntry(string Name, int Pid, string User, double? Cpu, long? Memory);
public sealed record ProcessTop(DateTimeOffset Time, IReadOnlyList<ProcessTopEntry> Processes);

/// <summary>How long each detail level is kept (days).</summary>
public sealed record MetricsRetention(int RawDays = 7, int FiveMinuteDays = 30, int HourDays = 365)
{
    public MetricsRetention Normalized() => new(Math.Clamp(RawDays, 1, 90), Math.Clamp(FiveMinuteDays, 1, 400), Math.Clamp(HourDays, 1, 3660));
}

/// <summary>
/// Metric history on this computer, without a database: a folder per server, a file per UTC day and detail level.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>raw/yyyyMMdd.bin</c> — 16-byte records: second of the day, series id, value.</item>
/// <item><c>m5/</c> and <c>h1/</c> — 32-byte roll-ups: second, series, count, average, minimum, maximum; written once the day is over.</item>
/// <item><c>series.txt</c> — series id, name and unit; <c>events/yyyyMM.jsonl</c>, <c>procs/yyyyMMdd.jsonl</c>.</item>
/// </list>
/// A file cut short by a crash loses only its torn last record. Only numbers, names and event texts are stored:
/// never passwords or command output.
/// </remarks>
public sealed class MetricsStore
{
    const int RawSize = 16, RollSize = 32;
    readonly object gate = new();
    readonly Dictionary<Guid, Dictionary<string, (ushort Id, string Unit)>> series = [];
    readonly Dictionary<Guid, Dictionary<string, (DateTimeOffset Time, double Value)>> latest = [];
    public string Root { get; }
    public MetricsStore(string root) { Root = root; Directory.CreateDirectory(root); }

    string ServerDir(Guid server) => Path.Combine(Root, server.ToString("N"));
    static string Day(DateTime utcDay) => utcDay.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
    static string Folder(MetricResolution r) => r switch { MetricResolution.Raw => "raw", MetricResolution.FiveMinutes => "m5", _ => "h1" };
    static int Step(MetricResolution r) => r switch { MetricResolution.Raw => 0, MetricResolution.FiveMinutes => 300, _ => 3600 };
    string DayFile(Guid server, MetricResolution r, DateTime utcDay) => Path.Combine(ServerDir(server), Folder(r), Day(utcDay) + ".bin");

    /// <summary>Servers that have any stored history.</summary>
    public IReadOnlyList<Guid> Servers()
    {
        if (!Directory.Exists(Root)) return [];
        return Directory.EnumerateDirectories(Root).Select(d => Guid.TryParseExact(Path.GetFileName(d), "N", out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToArray();
    }

    Dictionary<string, (ushort Id, string Unit)> Registry(Guid server)
    {
        if (series.TryGetValue(server, out var map)) return map;
        map = new(StringComparer.Ordinal);
        var path = Path.Combine(ServerDir(server), "series.txt");
        if (File.Exists(path))
            foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
            {
                var parts = line.Split('\t');
                if (parts.Length >= 2 && ushort.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id) && parts[1].Length > 0)
                    map[parts[1]] = (id, parts.Length > 2 ? parts[2] : "");
            }
        series[server] = map;
        return map;
    }

    static string Clean(string text) => new(text.Where(c => c is not ('\t' or '\r' or '\n' or '\0')).Take(200).ToArray());

    ushort SeriesId(Guid server, string name, string unit)
    {
        var map = Registry(server);
        if (map.TryGetValue(name, out var known)) return known.Id;
        if (map.Count >= ushort.MaxValue) throw new InvalidOperationException("Слишком много рядов метрик у одного сервера.");
        ushort id = (ushort)(map.Count == 0 ? 1 : map.Values.Max(v => v.Id) + 1);
        Directory.CreateDirectory(ServerDir(server));
        File.AppendAllText(Path.Combine(ServerDir(server), "series.txt"), id.ToString(CultureInfo.InvariantCulture) + "\t" + name + "\t" + Clean(unit) + "\n", Encoding.UTF8);
        map[name] = (id, unit);
        return id;
    }

    /// <summary>Series stored for a server with their units.</summary>
    public IReadOnlyDictionary<string, string> SeriesOf(Guid server)
    {
        lock (gate) return Registry(server).ToDictionary(p => p.Key, p => p.Value.Unit, StringComparer.Ordinal);
    }

    /// <summary>Appends one sample. Values that are not finite numbers are skipped (a gap, never a zero).</summary>
    public void Append(Guid server, DateTimeOffset time, IEnumerable<MetricValue> values)
    {
        var utc = time.UtcDateTime;
        uint second = (uint)(utc - utc.Date).TotalSeconds;
        lock (gate)
        {
            var buffer = new List<byte>();
            var record = new byte[RawSize];
            var last = latest.TryGetValue(server, out var l) ? l : latest[server] = new(StringComparer.Ordinal);
            foreach (var v in values)
            {
                if (!double.IsFinite(v.Value) || string.IsNullOrWhiteSpace(v.Name)) continue;
                var name = Clean(v.Name);
                ushort id = SeriesId(server, name, v.Unit);
                Array.Clear(record);
                BinaryPrimitives.WriteUInt32LittleEndian(record, second);
                BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), id);
                BinaryPrimitives.WriteDoubleLittleEndian(record.AsSpan(8), v.Value);
                buffer.AddRange(record);
                last[name] = (time, v.Value);
            }
            if (buffer.Count == 0) return;
            AppendRecords(DayFile(server, MetricResolution.Raw, utc.Date), buffer.ToArray(), RawSize);
        }
    }

    static void AppendRecords(string path, byte[] data, int size)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        long whole = stream.Length - stream.Length % size; // a torn record from a crash is dropped
        if (whole != stream.Length) stream.SetLength(whole);
        stream.Seek(0, SeekOrigin.End);
        stream.Write(data);
    }

    /// <summary>The newest value of each series (in memory; after a restart, from today's file).</summary>
    public IReadOnlyDictionary<string, (DateTimeOffset Time, double Value)> Latest(Guid server, DateTimeOffset now)
    {
        lock (gate)
        {
            if (!latest.TryGetValue(server, out var map))
            {
                map = new(StringComparer.Ordinal);
                var names = Registry(server).ToDictionary(p => p.Value.Id, p => p.Key);
                foreach (var day in new[] { now.UtcDateTime.Date.AddDays(-1), now.UtcDateTime.Date })
                    foreach (var (id, point) in ReadFile(DayFile(server, MetricResolution.Raw, day), day, RawSize, null))
                        if (names.TryGetValue(id, out var name)) map[name] = (point.Time, point.Average);
                latest[server] = map;
            }
            return new Dictionary<string, (DateTimeOffset, double)>(map, StringComparer.Ordinal);
        }
    }

    static List<(ushort Id, MetricPoint Point)> ReadFile(string path, DateTime utcDay, int size, HashSet<ushort>? wanted)
    {
        var result = new List<(ushort, MetricPoint)>();
        if (!File.Exists(path)) return result;
        byte[] data;
        try { using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); data = new byte[s.Length - s.Length % size]; s.ReadExactly(data); }
        catch (IOException) { return result; }
        var day = new DateTimeOffset(utcDay, TimeSpan.Zero);
        for (int i = 0; i + size <= data.Length; i += size)
        {
            var r = data.AsSpan(i, size);
            uint second = BinaryPrimitives.ReadUInt32LittleEndian(r);
            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(r[4..]);
            if (second >= 86400 || wanted is not null && !wanted.Contains(id)) continue;
            var time = day.AddSeconds(second);
            if (size == RawSize)
            {
                double v = BinaryPrimitives.ReadDoubleLittleEndian(r[8..]);
                if (double.IsFinite(v)) result.Add((id, new MetricPoint(time, v, v, v, 1)));
            }
            else
            {
                int count = BinaryPrimitives.ReadUInt16LittleEndian(r[6..]);
                double avg = BinaryPrimitives.ReadDoubleLittleEndian(r[8..]), min = BinaryPrimitives.ReadDoubleLittleEndian(r[16..]), max = BinaryPrimitives.ReadDoubleLittleEndian(r[24..]);
                if (count > 0 && double.IsFinite(avg)) result.Add((id, new MetricPoint(time, avg, min, max, count)));
            }
        }
        return result;
    }

    /// <summary>Groups points into buckets of <paramref name="seconds"/>: count-weighted average, overall minimum and maximum.</summary>
    public static IReadOnlyList<MetricPoint> Aggregate(IEnumerable<MetricPoint> points, int seconds)
    {
        if (seconds <= 0) return points.OrderBy(p => p.Time).ToArray();
        return points.GroupBy(p => p.Time.ToUnixTimeSeconds() / seconds).OrderBy(g => g.Key).Select(g =>
        {
            int count = g.Sum(p => p.Count);
            double avg = g.Sum(p => p.Average * p.Count) / Math.Max(1, count);
            return new MetricPoint(DateTimeOffset.FromUnixTimeSeconds(g.Key * seconds), avg, g.Min(p => p.Min), g.Max(p => p.Max), Math.Min(count, ushort.MaxValue));
        }).ToArray();
    }

    /// <summary>Detail to show for a time span: every sample up to 2 days, five-minute points up to 14 days, hourly beyond.</summary>
    public static MetricResolution ResolutionFor(TimeSpan span) => span <= TimeSpan.FromDays(2) ? MetricResolution.Raw : span <= TimeSpan.FromDays(14) ? MetricResolution.FiveMinutes : MetricResolution.Hour;

    /// <summary>
    /// Points of several series between <paramref name="from"/> and <paramref name="to"/>. A day without the requested
    /// level is built from finer files, or, when those are already deleted, taken from the coarser level.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<MetricPoint>> Read(Guid server, IReadOnlyCollection<string> names, DateTimeOffset from, DateTimeOffset to, MetricResolution resolution)
    {
        Dictionary<string, (ushort Id, string Unit)> map;
        lock (gate) map = new(Registry(server), StringComparer.Ordinal);
        var ids = names.Distinct().Where(map.ContainsKey).ToDictionary(n => map[n].Id, n => n);
        var result = names.Distinct().ToDictionary(n => n, _ => new List<MetricPoint>(), StringComparer.Ordinal);
        if (ids.Count == 0 || to < from) return result.ToDictionary(p => p.Key, p => (IReadOnlyList<MetricPoint>)p.Value, StringComparer.Ordinal);
        var wanted = ids.Keys.ToHashSet();
        for (var day = from.UtcDateTime.Date; day <= to.UtcDateTime.Date; day = day.AddDays(1))
        {
            foreach (var group in ReadDay(server, day, resolution, wanted).GroupBy(x => x.Id))
            {
                var points = group.Select(x => x.Point).Where(p => p.Time >= from && p.Time <= to);
                result[ids[group.Key]].AddRange(points);
            }
        }
        return result.ToDictionary(p => p.Key, p => (IReadOnlyList<MetricPoint>)p.Value.OrderBy(x => x.Time).ToArray(), StringComparer.Ordinal);
    }

    IEnumerable<(ushort Id, MetricPoint Point)> ReadDay(Guid server, DateTime day, MetricResolution resolution, HashSet<ushort> wanted)
    {
        // Exact level first, then finer files rolled up on the fly, then coarser files as they are.
        var order = resolution switch
        {
            MetricResolution.Raw => new[] { MetricResolution.Raw, MetricResolution.FiveMinutes, MetricResolution.Hour },
            MetricResolution.FiveMinutes => new[] { MetricResolution.FiveMinutes, MetricResolution.Raw, MetricResolution.Hour },
            _ => new[] { MetricResolution.Hour, MetricResolution.FiveMinutes, MetricResolution.Raw },
        };
        foreach (var level in order)
        {
            var path = DayFile(server, level, day);
            if (!File.Exists(path)) continue;
            var points = ReadFile(path, day, level == MetricResolution.Raw ? RawSize : RollSize, wanted);
            if (Step(level) >= Step(resolution)) return points;
            return points.GroupBy(p => p.Id).SelectMany(g => Aggregate(g.Select(x => x.Point), Step(resolution)).Select(p => (g.Key, p))).ToArray();
        }
        return [];
    }

    /// <summary>
    /// Rolls finished days up into five-minute and hourly files, then deletes what is older than the retention.
    /// Safe to call often: a day is rolled up once.
    /// </summary>
    public void Compact(DateTimeOffset now, MetricsRetention retention)
    {
        retention = retention.Normalized();
        var today = now.UtcDateTime.Date;
        foreach (var server in Servers())
        {
            lock (gate)
            {
                RollUp(server, MetricResolution.Raw, MetricResolution.FiveMinutes, today);
                RollUp(server, MetricResolution.FiveMinutes, MetricResolution.Hour, today);
                Expire(Path.Combine(ServerDir(server), "raw"), today.AddDays(-retention.RawDays));
                Expire(Path.Combine(ServerDir(server), "procs"), today.AddDays(-retention.RawDays));
                Expire(Path.Combine(ServerDir(server), "m5"), today.AddDays(-retention.FiveMinuteDays));
                Expire(Path.Combine(ServerDir(server), "h1"), today.AddDays(-retention.HourDays));
                ExpireEvents(server, today.AddDays(-retention.HourDays));
            }
        }
    }

    void RollUp(Guid server, MetricResolution source, MetricResolution target, DateTime today)
    {
        var dir = Path.Combine(ServerDir(server), Folder(source));
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.EnumerateFiles(dir, "*.bin"))
        {
            if (!DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var day) || day >= today) continue;
            var output = DayFile(server, target, day);
            if (File.Exists(output)) continue;
            var points = ReadFile(file, day, source == MetricResolution.Raw ? RawSize : RollSize, null);
            var data = new List<byte>(points.Count * 4);
            var record = new byte[RollSize];
            foreach (var group in points.GroupBy(p => p.Id).OrderBy(g => g.Key))
                foreach (var p in Aggregate(group.Select(x => x.Point), Step(target)))
                {
                    Array.Clear(record);
                    BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)(p.Time.UtcDateTime - day).TotalSeconds);
                    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), group.Key);
                    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), (ushort)Math.Clamp(p.Count, 1, ushort.MaxValue));
                    BinaryPrimitives.WriteDoubleLittleEndian(record.AsSpan(8), p.Average);
                    BinaryPrimitives.WriteDoubleLittleEndian(record.AsSpan(16), p.Min);
                    BinaryPrimitives.WriteDoubleLittleEndian(record.AsSpan(24), p.Max);
                    data.AddRange(record);
                }
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var temp = output + ".tmp";
            File.WriteAllBytes(temp, data.ToArray());
            File.Move(temp, output, true);
        }
    }

    static void Expire(string dir, DateTime before)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.EnumerateFiles(dir))
            if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var day) && day < before)
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    void ExpireEvents(Guid server, DateTime before)
    {
        var dir = Path.Combine(ServerDir(server), "events");
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl"))
            if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyyMM", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var month) && month.AddMonths(1) < before)
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Records an event (connection, reboot, alert, change made from the app, or a user's own mark).</summary>
    public void AddEvent(Guid server, MetricEvent e)
    {
        var line = JsonSerializer.Serialize(new { t = e.Time.ToUnixTimeSeconds(), k = Clean(e.Kind), x = Clean(e.Text) }) + "\n";
        lock (gate)
        {
            var dir = Path.Combine(ServerDir(server), "events"); Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, e.Time.UtcDateTime.ToString("yyyyMM", CultureInfo.InvariantCulture) + ".jsonl"), line, Encoding.UTF8);
        }
    }

    public IReadOnlyList<MetricEvent> Events(Guid server, DateTimeOffset from, DateTimeOffset to)
    {
        var result = new List<MetricEvent>();
        var dir = Path.Combine(ServerDir(server), "events");
        if (!Directory.Exists(dir)) return result;
        var month = new DateTime(from.UtcDateTime.Year, from.UtcDateTime.Month, 1);
        for (; month <= to.UtcDateTime; month = month.AddMonths(1))
            foreach (var line in ReadLines(Path.Combine(dir, month.ToString("yyyyMM", CultureInfo.InvariantCulture) + ".jsonl")))
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var r = doc.RootElement;
                    var time = DateTimeOffset.FromUnixTimeSeconds(r.GetProperty("t").GetInt64());
                    if (time >= from && time <= to) result.Add(new MetricEvent(time, r.GetProperty("k").GetString() ?? "", r.GetProperty("x").GetString() ?? ""));
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { } // a torn line
            }
        return result.OrderBy(e => e.Time).ToArray();
    }

    static IEnumerable<string> ReadLines(string path)
    {
        if (!File.Exists(path)) return [];
        try { using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); using var reader = new StreamReader(s, Encoding.UTF8); return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries); }
        catch (IOException) { return []; }
    }

    /// <summary>Stores the busiest processes of one sample.</summary>
    public void AppendProcesses(Guid server, DateTimeOffset time, IReadOnlyList<ProcessTopEntry> processes)
    {
        if (processes.Count == 0) return;
        var line = JsonSerializer.Serialize(new { t = time.ToUnixTimeSeconds(), p = processes.Select(p => new { n = Clean(p.Name), i = p.Pid, u = Clean(p.User), c = p.Cpu is { } c ? Math.Round(c, 2) : (double?)null, m = p.Memory }) }) + "\n";
        lock (gate)
        {
            var dir = Path.Combine(ServerDir(server), "procs"); Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, Day(time.UtcDateTime.Date) + ".jsonl"), line, Encoding.UTF8);
        }
    }

    /// <summary>The busiest processes recorded nearest to <paramref name="time"/> (within <paramref name="tolerance"/>), or null.</summary>
    public ProcessTop? ProcessesAt(Guid server, DateTimeOffset time, TimeSpan tolerance)
    {
        ProcessTop? best = null; TimeSpan bestDistance = TimeSpan.MaxValue;
        foreach (var day in new[] { (time - tolerance).UtcDateTime.Date, (time + tolerance).UtcDateTime.Date }.Distinct())
            foreach (var line in ReadLines(Path.Combine(ServerDir(server), "procs", Day(day) + ".jsonl")))
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var r = doc.RootElement;
                    var t = DateTimeOffset.FromUnixTimeSeconds(r.GetProperty("t").GetInt64());
                    var distance = (t - time).Duration();
                    if (distance > tolerance || distance >= bestDistance) continue;
                    var list = r.GetProperty("p").EnumerateArray().Select(p => new ProcessTopEntry(
                        p.GetProperty("n").GetString() ?? "", p.GetProperty("i").GetInt32(), p.GetProperty("u").GetString() ?? "",
                        p.GetProperty("c").ValueKind == JsonValueKind.Number ? p.GetProperty("c").GetDouble() : null,
                        p.GetProperty("m").ValueKind == JsonValueKind.Number ? p.GetProperty("m").GetInt64() : null)).ToArray();
                    best = new ProcessTop(t, list); bestDistance = distance;
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { }
            }
        return best;
    }

    /// <summary>Deletes all history of a server (when its profile is deleted and the user agrees).</summary>
    public void DeleteServer(Guid server)
    {
        lock (gate)
        {
            series.Remove(server); latest.Remove(server);
            var dir = ServerDir(server);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    /// <summary>Bytes the history of a server takes on disk.</summary>
    public long SizeOf(Guid server)
    {
        var dir = ServerDir(server);
        return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => { try { return new FileInfo(f).Length; } catch (IOException) { return 0L; } }) : 0;
    }
}
