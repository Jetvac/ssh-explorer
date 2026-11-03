using System.Globalization;

namespace SSHExplorer.Monitoring;

/// <summary>Stateful delta calculator. A new instance means a new logical SSH session.</summary>
public sealed class MonitorCalculator
{
    private RawSample? _previous;
    private string? _resetReason;

    public void Reset(string reason = "База измерений сброшена; нужен следующий замер.")
    { if(_previous is not null) _resetReason = reason; _previous = null; }

    public MonitorSnapshot Calculate(string protocol, DateTimeOffset timestamp)
    {
        var frames = ProcParser.DecodeFrames(protocol);
        var current = RawSample.Parse(frames);
        var previous = _previous;
        string? reset = _resetReason; _resetReason = null;
        if (previous is not null && ((!string.IsNullOrEmpty(current.BootId) && !string.IsNullOrEmpty(previous.BootId) && current.BootId != previous.BootId) ||
            current.UptimeSeconds is { } uptime && previous.UptimeSeconds is { } oldUptime && uptime < oldUptime))
        { previous = null; reset = "Сервер перезапущен или счётчики uptime сброшены; накапливается новая база."; }
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
        var cpuStatus = current.Cpu is null ? Missing("CPU: /proc/stat недоступен или имеет неподдерживаемый формат.")
            : reset is not null ? new MetricStatus(MetricState.Reset, reset)
            : previous?.Cpu is null ? Waiting("Для CPU нужен следующий читаемый замер /proc/stat.")
            : cpu.HasValue ? MetricStatus.Measured
            : current.Cpu.Total == previous.Cpu.Total ? Missing("Счётчики /proc/stat не изменились; интервал CPU не определён.")
            : new MetricStatus(MetricState.Reset, "Счётчики CPU уменьшились или несогласованы; накапливается новая база.");

        var memory = ProcParser.ParseMemory(current.Frame("sys/meminfo"));
        var load = ProcParser.Words(current.Frame("sys/loadavg") ?? "");
        var system = new SystemMetric(cpu, current.Cpu?.CpuCount ?? 0, memory.TotalBytes,
            memory.AvailableBytes, memory.UsedBytes, memory.SwapTotalBytes, memory.SwapUsedBytes,
            current.UptimeSeconds is { } up && up <= TimeSpan.MaxValue.TotalSeconds ? TimeSpan.FromSeconds(up) : null,
            load.Length > 0 ? ProcParser.PositiveDouble(load[0]) : null,
            load.Length > 1 ? ProcParser.PositiveDouble(load[1]) : null,
            load.Length > 2 ? ProcParser.PositiveDouble(load[2]) : null,
            int.TryParse(current.Frame("meta/ticks"), out var ticks) && ticks > 0 ? ticks : null,
            memory.IsEstimate)
        { CpuStatus = cpuStatus, MemoryStatus = memory.TotalBytes is not > 0 || memory.UsedBytes is null ? Missing("Память: /proc/meminfo недоступен или неполон.") : MetricStatus.Measured };

        var processes = current.Processes.Values.OrderBy(x => x.Stat.Pid).Select(p =>
        {
            var old = previous?.Processes.GetValueOrDefault(p.Stat.Pid);
            if (old is not null && (old.Identity.Pid != p.Identity.Pid || old.Identity.StartTimeTicks != p.Identity.StartTimeTicks)) old = null;
            double? percent = totalDelta is > 0 && old is not null && p.Stat.CpuTicks >= old.Stat.CpuTicks
                ? Math.Clamp(100d * (p.Stat.CpuTicks - old.Stat.CpuTicks) / totalDelta.Value, 0, 100) : null;
            if (cpuStatus.State != MetricState.Available) percent = null;
            var processCpu = current.Cpu is null ? cpuStatus : reset is not null ? new MetricStatus(MetricState.Reset, reset)
                : previous is null ? Waiting("Первый замер: CPU процесса рассчитывается по следующей точке.")
                : old is null ? new MetricStatus(MetricState.NewProcess, "Новый процесс или повторно использованный PID: ещё нет предыдущей точки.")
                : cpuStatus.State != MetricState.Available ? cpuStatus
                : percent.HasValue ? MetricStatus.Measured : new MetricStatus(MetricState.Reset,"Счётчик CPU процесса уменьшился; нужна новая база.");
            return new ProcessMetric(p.Identity, p.Stat.Name,
                p.Identity.UserId == uint.MaxValue ? "Недоступно" : current.Users.GetValueOrDefault(p.Identity.UserId,
                    p.Identity.UserId.ToString(CultureInfo.InvariantCulture)), p.Stat.State, percent, p.MemoryBytes,
                Rate(p.ReadBytes, old?.ReadBytes, seconds), Rate(p.WriteBytes, old?.WriteBytes, seconds), p.CommandLine)
            {
                CpuStatus = processCpu,
                MemoryStatus = p.MemoryBytes.HasValue ? MetricStatus.Measured : Missing("Память процесса: /proc/PID/status недоступен и нет читаемого RSS с размером страницы."),
                ReadStatus = RateStatus(p.ReadBytes, old?.ReadBytes, seconds, current.UptimeSeconds, reset, previous is not null && old is null, "/proc/PID/io: read_bytes"),
                WriteStatus = RateStatus(p.WriteBytes, old?.WriteBytes, seconds, current.UptimeSeconds, reset, previous is not null && old is null, "/proc/PID/io: write_bytes")
            };
        }).ToArray();

        var networks = current.Networks.Select(pair =>
        {
            var old = previous?.Networks.GetValueOrDefault(pair.Key);
            return new NetworkMetric(pair.Key, pair.Value.Read, pair.Value.Write,
                Rate(pair.Value.Read, old?.Read, seconds), Rate(pair.Value.Write, old?.Write, seconds))
            { ReceiveStatus = RateStatus(pair.Value.Read, old?.Read, seconds, current.UptimeSeconds, reset, false, "/proc/net/dev"),
              SendStatus = RateStatus(pair.Value.Write, old?.Write, seconds, current.UptimeSeconds, reset, false, "/proc/net/dev") };
        }).OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
        var disks = current.Disks.Select(pair =>
        {
            var old = previous?.Disks.GetValueOrDefault(pair.Key);
            return new DiskMetric(pair.Key, pair.Value.Read, pair.Value.Write,
                Rate(pair.Value.Read, old?.Read, seconds), Rate(pair.Value.Write, old?.Write, seconds))
            { ReadStatus = RateStatus(pair.Value.Read, old?.Read, seconds, current.UptimeSeconds, reset, false, "/proc/diskstats"),
              WriteStatus = RateStatus(pair.Value.Write, old?.Write, seconds, current.UptimeSeconds, reset, false, "/proc/diskstats") };
        }).OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();

        var limitations = new List<string>();
        if (current.Frame("meta/limited") == "1")
            limitations.Add($"Достигнут лимит процессов: {current.Frame("meta/count")}. Показана ограниченная выборка.");
        if (cpuStatus.State != MetricState.Available) limitations.Add(cpuStatus.Reason);
        if (current.UptimeSeconds is null) limitations.Add("Скорости: /proc/uptime недоступен или повреждён; интервал сервера неизвестен. CPU по отношению счётчиков и память остаются независимыми.");
        else if (previous is not null && previous.UptimeSeconds is null) limitations.Add("Скорости: первая читаемая точка /proc/uptime, нужен следующий замер.");
        else if (seconds is <= 0) limitations.Add("Скорости: /proc/uptime не увеличился; корректный интервал недоступен.");
        if (memory.TotalBytes is null) limitations.Add("Память: /proc/meminfo недоступен.");
        if (memory.IsEstimate) limitations.Add("MemAvailable отсутствует: доступная память оценена как MemFree + Buffers + Cached.");
        if (system.ClockTicksPerSecond is null) limitations.Add("Clock ticks: getconf CLK_TCK недоступен; CPU вычисляется по отношению счётчиков /proc.");
        if (current.Networks.Count == 0) limitations.Add("Сеть: /proc/net/dev недоступен, пуст либо не содержит читаемых интерфейсов. Персональный сетевой трафик процессов не измеряется.");
        if (current.Disks.Count == 0) limitations.Add("Диск: /proc/diskstats недоступен, пуст либо не содержит читаемых устройств.");
        if (string.IsNullOrWhiteSpace(current.Frame("sys/df"))) limitations.Add("Свободное место: существующая команда df недоступна либо не вернула данные.");
        if (current.UnreadableProcesses > 0) limitations.Add($"Не удалось прочитать или согласовать {current.UnreadableProcesses} процессов: отказ доступа либо процесс завершился во время замера.");
        if (processes.Any(p => p.ReadStatus.State == MetricState.Unavailable || p.WriteStatus.State == MetricState.Unavailable))
            limitations.Add("I/O части процессов: нет читаемых счётчиков /proc/PID/io либо интервала uptime; проверьте доступность источников и права.");
        if (processes.Any(p => p.ReadStatus.State is MetricState.Initial or MetricState.NewProcess))
            limitations.Add("I/O: для новых или впервые читаемых процессов нужен следующий замер.");
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

    private static MetricStatus Missing(string reason) => new(MetricState.Unavailable, reason);
    private static MetricStatus Waiting(string reason) => new(MetricState.Initial, reason);
    private static MetricStatus RateStatus(ulong? value, ulong? previous, double? seconds, double? uptime, string? reset, bool newProcess, string source)
        => value is null ? Missing(source + ": источник недоступен, нет прав чтения или поле отсутствует.")
        : reset is not null ? new(MetricState.Reset, reset)
        : uptime is null ? Missing("Скорость не вычисляется: /proc/uptime недоступен или повреждён.")
        : newProcess ? new(MetricState.NewProcess,"Новый процесс: для скорости нужна следующая точка.")
        : previous is null ? Waiting(source + ": первый читаемый счётчик, нужен следующий замер.")
        : seconds is null ? Waiting("Первая читаемая точка /proc/uptime: для скорости нужен следующий замер.")
        : seconds is not > 0 ? Missing("Скорость не вычисляется: нет двух увеличивающихся значений /proc/uptime.")
        : value < previous ? new(MetricState.Reset, source + ": счётчик сброшен, нужна новая база.") : MetricStatus.Measured;

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
