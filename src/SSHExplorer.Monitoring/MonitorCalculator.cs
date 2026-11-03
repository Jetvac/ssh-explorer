using System.Globalization;

namespace SSHExplorer.Monitoring;

/// <summary>Stateful delta calculator. A new instance means a new logical SSH session.</summary>
public sealed class MonitorCalculator
{
    private RawSample? _previous;

    public void Reset() => _previous = null;

    public MonitorSnapshot Calculate(string protocol, DateTimeOffset timestamp)
    {
        var frames = ProcParser.DecodeFrames(protocol);
        var current = RawSample.Parse(frames);
        var previous = _previous;
        if (previous is not null && (current.BootId != previous.BootId ||
            current.UptimeSeconds is null || previous.UptimeSeconds is null ||
            current.UptimeSeconds <= previous.UptimeSeconds)) previous = null;
        var seconds = previous is not null ? current.UptimeSeconds - previous.UptimeSeconds : null;
        var totalDelta = current.Cpu is not null && previous?.Cpu is not null && current.Cpu.Total > previous.Cpu.Total
            ? current.Cpu.Total - previous.Cpu.Total : (ulong?)null;
        double? cpu = null;
        if (totalDelta is > 0 && current.Cpu is not null && previous?.Cpu is not null &&
            current.Cpu.Idle >= previous.Cpu.Idle)
        {
            var idleDelta = current.Cpu.Idle - previous.Cpu.Idle;
            if (idleDelta <= totalDelta) cpu = 100d * (totalDelta.Value - idleDelta) / totalDelta.Value;
        }

        var memory = ProcParser.ParseMemory(current.Frame("sys/meminfo"));
        var load = ProcParser.Words(current.Frame("sys/loadavg") ?? "");
        var system = new SystemMetric(cpu, current.Cpu?.CpuCount ?? 0, memory.TotalBytes,
            memory.AvailableBytes, memory.UsedBytes, memory.SwapTotalBytes, memory.SwapUsedBytes,
            current.UptimeSeconds is { } up && up <= TimeSpan.MaxValue.TotalSeconds ? TimeSpan.FromSeconds(up) : null,
            load.Length > 0 ? ProcParser.PositiveDouble(load[0]) : null,
            load.Length > 1 ? ProcParser.PositiveDouble(load[1]) : null,
            load.Length > 2 ? ProcParser.PositiveDouble(load[2]) : null,
            int.TryParse(current.Frame("meta/ticks"), out var ticks) && ticks > 0 ? ticks : null,
            memory.IsEstimate);

        var processes = current.Processes.Values.OrderBy(x => x.Stat.Pid).Select(p =>
        {
            var old = previous?.Processes.GetValueOrDefault(p.Stat.Pid);
            if (old?.Identity != p.Identity) old = null;
            double? percent = totalDelta is > 0 && old is not null && p.Stat.CpuTicks >= old.Stat.CpuTicks
                ? Math.Clamp(100d * (p.Stat.CpuTicks - old.Stat.CpuTicks) / totalDelta.Value, 0, 100) : null;
            return new ProcessMetric(p.Identity, p.Stat.Name,
                p.Identity.UserId == uint.MaxValue ? "Недоступно" : current.Users.GetValueOrDefault(p.Identity.UserId,
                    p.Identity.UserId.ToString(CultureInfo.InvariantCulture)), p.Stat.State, percent, p.MemoryBytes,
                Rate(p.ReadBytes, old?.ReadBytes, seconds), Rate(p.WriteBytes, old?.WriteBytes, seconds), p.CommandLine);
        }).ToArray();

        var networks = current.Networks.Select(pair =>
        {
            var old = previous?.Networks.GetValueOrDefault(pair.Key);
            return new NetworkMetric(pair.Key, pair.Value.Read, pair.Value.Write,
                Rate(pair.Value.Read, old?.Read, seconds), Rate(pair.Value.Write, old?.Write, seconds));
        }).OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
        var disks = current.Disks.Select(pair =>
        {
            var old = previous?.Disks.GetValueOrDefault(pair.Key);
            return new DiskMetric(pair.Key, pair.Value.Read, pair.Value.Write,
                Rate(pair.Value.Read, old?.Read, seconds), Rate(pair.Value.Write, old?.Write, seconds));
        }).OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();

        var limitations = new List<string>();
        if (current.Frame("meta/limited") == "1")
            limitations.Add($"Достигнут лимит процессов: {current.Frame("meta/count")}. Показана ограниченная выборка.");
        if (current.Cpu is null) limitations.Add("CPU: /proc/stat недоступен или имеет неподдерживаемый формат.");
        else if (previous is null || totalDelta is null) limitations.Add("CPU и скорости: ожидается следующий успешный замер; после перезапуска счётчики сбрасываются.");
        if (memory.TotalBytes is null) limitations.Add("Память: /proc/meminfo недоступен.");
        if (memory.IsEstimate) limitations.Add("MemAvailable отсутствует: доступная память оценена как MemFree + Buffers + Cached.");
        if (system.ClockTicksPerSecond is null) limitations.Add("Clock ticks: getconf CLK_TCK недоступен; CPU вычисляется по отношению счётчиков /proc.");
        if (current.Networks.Count == 0) limitations.Add("Сеть: /proc/net/dev недоступен, пуст либо не содержит читаемых интерфейсов. Персональный сетевой трафик процессов не измеряется.");
        if (current.Disks.Count == 0) limitations.Add("Диск: /proc/diskstats недоступен, пуст либо не содержит читаемых устройств.");
        if (string.IsNullOrWhiteSpace(current.Frame("sys/df"))) limitations.Add("Свободное место: существующая команда df недоступна либо не вернула данные.");
        if (current.UnreadableProcesses > 0) limitations.Add($"Не удалось прочитать или согласовать {current.UnreadableProcesses} процессов: отказ доступа либо процесс завершился во время замера.");
        if (processes.Any(p => p.ReadBytesPerSecond is null || p.WriteBytesPerSecond is null))
            limitations.Add("I/O процессов: недоступно без двух замеров и прав чтения /proc/PID/io.");
        if (current.Processes.Values.Any(p => p.CommandLineTruncated))
            limitations.Add("Некоторые командные строки ограничены первыми 4096 байтами.");
        if (current.Processes.Values.Any(p => p.Identity.UserId == uint.MaxValue))
            limitations.Add("У части процессов недоступен владелец; отправка сигналов для них отключена.");
        if (current.Frame("sys/passwd") is null) limitations.Add("Имена пользователей недоступны; показаны числовые UID.");
        foreach (var (key, bound) in new (string, int)[] { ("sys/stat", 65536), ("sys/net", 65536), ("sys/disks", 131072), ("sys/passwd", 131072), ("sys/df", 131072) })
            if (current.Frame(key) is { } value && System.Text.Encoding.UTF8.GetByteCount(value) >= bound)
                limitations.Add($"{key}: достигнут лимит чтения {bound} байт; список может быть неполным.");
        _previous = current;
        return new MonitorSnapshot(timestamp, system, processes, networks, disks,
            ProcParser.ParseStorage(current.Frame("sys/df")), limitations);
    }

    public static double? Rate(ulong? value, ulong? previous, double? seconds)
        => value is not null && previous is not null && value >= previous && seconds is > 0
            ? (value.Value - previous.Value) / seconds.Value : null;

    private sealed record CounterPair(ulong Read, ulong Write);
    private sealed record RawProcess(ProcessIdentity Identity, ProcessStat Stat, long? MemoryBytes,
        ulong? ReadBytes, ulong? WriteBytes, string? CommandLine, bool CommandLineTruncated);

    private sealed class RawSample
    {
        private IReadOnlyDictionary<string, string?> _frames = null!;
        public CpuCounters? Cpu { get; private init; }
        public double? UptimeSeconds { get; private init; }
        public string? BootId { get; private init; }
        public Dictionary<int, RawProcess> Processes { get; } = [];
        public Dictionary<string, CounterPair> Networks { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, CounterPair> Disks { get; } = new(StringComparer.Ordinal);
        public Dictionary<uint, string> Users { get; } = [];
        public int UnreadableProcesses { get; private set; }
        public string? Frame(string key) => _frames.GetValueOrDefault(key);

        public static RawSample Parse(IReadOnlyDictionary<string, string?> frames)
        {
            var upText = ProcParser.Words(frames.GetValueOrDefault("sys/uptime") ?? "");
            var sample = new RawSample
            {
                _frames = frames, Cpu = ProcParser.ParseCpu(frames.GetValueOrDefault("sys/stat")),
                UptimeSeconds = upText.Length > 0 ? ProcParser.PositiveDouble(upText[0]) : null,
                BootId = frames.GetValueOrDefault("sys/boot_id")?.Trim()
            };
            foreach (var row in (sample.Frame("sys/passwd") ?? "").Split('\n'))
            {
                var p = row.Split(':');
                if (p.Length >= 4 && uint.TryParse(p[2], out var uid)) sample.Users.TryAdd(uid, p[0]);
            }
            var pageSize = long.TryParse(sample.Frame("meta/pages"), out var page) && page > 0 ? page : (long?)null;
            foreach (var key in frames.Keys.Where(k => k.StartsWith("p/", StringComparison.Ordinal) && k.EndsWith("/stat", StringComparison.Ordinal)))
            {
                var prefix = key[..^4];
                var stat = ProcParser.ParseProcessStat(sample.Frame(key));
                var end = ProcParser.ParseProcessStat(sample.Frame(prefix + "stat_end"));
                if (stat is null || end is null || stat.Pid != end.Pid || stat.StartTimeTicks != end.StartTimeTicks)
                { sample.UnreadableProcesses++; continue; }
                var status = ProcParser.ParseColonFields(sample.Frame(prefix + "status"));
                var uidWords = ProcParser.Words(status.GetValueOrDefault("Uid") ?? "");
                var uid = uidWords.Length > 0 && uint.TryParse(uidWords[0], out var u) ? u : uint.MaxValue;
                var rss = ProcParser.KibiBytes(status.GetValueOrDefault("VmRSS"));
                if (rss is null && pageSize is not null && stat.ResidentPages >= 0 && stat.ResidentPages <= long.MaxValue / pageSize)
                    rss = stat.ResidentPages * pageSize;
                var io = ProcParser.ParseColonFields(sample.Frame(prefix + "io"));
                var cmdline = sample.Frame(prefix + "cmdline");
                var truncated = cmdline is not null && System.Text.Encoding.UTF8.GetByteCount(cmdline) >= 4096;
                var display = cmdline is null ? null : string.Join(' ', cmdline.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(DisplayArgument));
                sample.Processes[stat.Pid] = new RawProcess(new ProcessIdentity(stat.Pid, stat.StartTimeTicks, uid),
                    stat, rss, ProcParser.Unsigned(io.GetValueOrDefault("read_bytes")),
                    ProcParser.Unsigned(io.GetValueOrDefault("write_bytes")), display, truncated);
            }
            foreach (var row in (sample.Frame("sys/net") ?? "").Split('\n'))
            {
                var colon = row.LastIndexOf(':');
                if (colon < 1) continue;
                var p = ProcParser.Words(row[(colon + 1)..]);
                if (p.Length >= 16 && ulong.TryParse(p[0], out var rx) && ulong.TryParse(p[8], out var tx))
                    sample.Networks[row[..colon].Trim()] = new CounterPair(rx, tx);
            }
            foreach (var row in (sample.Frame("sys/disks") ?? "").Split('\n'))
            {
                var p = ProcParser.Words(row);
                if (p.Length >= 14 && ulong.TryParse(p[5], out var read) && ulong.TryParse(p[9], out var written)
                    && read <= ulong.MaxValue / 512 && written <= ulong.MaxValue / 512)
                    sample.Disks[p[2]] = new CounterPair(read * 512, written * 512);
            }
            return sample;
        }

        private static string DisplayArgument(string arg)
        {
            arg = arg.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
            return arg.Any(char.IsWhiteSpace) || arg.Contains('\'')
                ? "'" + arg.Replace("'", "'\\''", StringComparison.Ordinal) + "'" : arg;
        }
    }
}
