using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SSHExplorer.Monitoring;

/// <summary>Names, labels and units of recorded series; what one monitor sample turns into.</summary>
public static class MetricSeries
{
    public const string Cpu = "cpu", Memory = "mem", MemoryUsed = "mem.used", Swap = "swap", Load1 = "load1",
        NetReceive = "net.rx", NetSend = "net.tx", DiskRead = "disk.read", DiskWrite = "disk.write",
        FileSystemPrefix = "fs:", CustomPrefix = "custom:";
    public const string Percent = "%", Bytes = "B", BytesPerSecond = "B/s";
    /// <summary>Upper limit of file systems recorded per server (containers can mount hundreds).</summary>
    public const int MaxFileSystems = 32;

    public static readonly IReadOnlyList<string> System = [Cpu, Memory, MemoryUsed, Swap, Load1, NetReceive, NetSend, DiskRead, DiskWrite];

    public static string FileSystem(string mount) => FileSystemPrefix + mount;
    public static string Custom(string metric, string? key = null) => CustomPrefix + metric + (string.IsNullOrEmpty(key) ? "" : "." + key);
    public static bool IsFileSystem(string name) => name.StartsWith(FileSystemPrefix, StringComparison.Ordinal);
    public static bool IsCustom(string name) => name.StartsWith(CustomPrefix, StringComparison.Ordinal);

    public static string Label(string name) => name switch
    {
        Cpu => "ЦП",
        Memory => "Память",
        MemoryUsed => "Память, занято",
        Swap => "Файл подкачки",
        Load1 => "Средняя нагрузка (1 мин)",
        NetReceive => "Сеть: получение",
        NetSend => "Сеть: отправка",
        DiskRead => "Диски: чтение",
        DiskWrite => "Диски: запись",
        _ when IsFileSystem(name) => "Диск " + name[FileSystemPrefix.Length..],
        _ when IsCustom(name) => name[CustomPrefix.Length..],
        _ => name,
    };

    public static string UnitOf(string name) => name switch
    {
        Cpu or Memory or Swap => Percent,
        MemoryUsed => Bytes,
        NetReceive or NetSend or DiskRead or DiskWrite => BytesPerSecond,
        _ when IsFileSystem(name) => Percent,
        _ => "",
    };

    /// <summary>A value with its unit for labels and the inspector: 42,5 %, 1,2 ГБ, 3,4 МБ/с.</summary>
    public static string Format(double value, string unit, IFormatProvider? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        return unit switch
        {
            Percent => value.ToString(value >= 10 ? "0" : "0.#", culture) + " %",
            Bytes => Size(value, culture),
            BytesPerSecond => Size(value, culture) + "/с",
            "" => Number(value, culture),
            _ => Number(value, culture) + " " + unit,
        };
    }

    static string Number(double value, IFormatProvider culture) => Math.Abs(value) >= 1000 ? value.ToString("N0", culture) : value.ToString("0.##", culture);

    static string Size(double value, IFormatProvider culture)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        double size = Math.Abs(value); int i = 0;
        while (size >= 1024 && i < units.Length - 1) { size /= 1024; i++; }
        return (value < 0 ? "-" : "") + size.ToString(i == 0 ? "0" : "0.#", culture) + " " + units[i];
    }

    /// <summary>The values of one sample: system series and the fullness of each file system. Unknown values are left out.</summary>
    public static IReadOnlyList<MetricValue> FromSnapshot(MonitorSnapshot snapshot)
    {
        var list = new List<MetricValue>();
        void Add(string name, double? value) { if (value is { } v && double.IsFinite(v)) list.Add(new MetricValue(name, v, UnitOf(name))); }
        var s = snapshot.System;
        Add(Cpu, s.CpuPercent);
        Add(Memory, s.MemoryPercent);
        Add(MemoryUsed, s.MemoryUsedBytes);
        Add(Swap, s.SwapTotalBytes is > 0 && s.SwapUsedBytes is { } used ? used * 100d / s.SwapTotalBytes.Value : null);
        Add(Load1, s.Load1);
        Add(NetReceive, Sum(snapshot.Networks.Where(n => !IsLoopback(n.Name)).Select(n => n.ReceiveBytesPerSecond)));
        Add(NetSend, Sum(snapshot.Networks.Where(n => !IsLoopback(n.Name)).Select(n => n.SendBytesPerSecond)));
        Add(DiskRead, Sum(snapshot.Disks.Select(d => d.ReadBytesPerSecond)));
        Add(DiskWrite, Sum(snapshot.Disks.Select(d => d.WriteBytesPerSecond)));
        foreach (var fs in snapshot.Storage.Where(f => f.TotalBytes > 0).GroupBy(f => f.MountPoint).Select(g => g.First()).Take(MaxFileSystems))
            Add(FileSystem(fs.MountPoint), fs.UsedBytes * 100d / fs.TotalBytes);
        return list;
    }

    static bool IsLoopback(string name) => name == "lo" || name.Contains("Loopback", StringComparison.OrdinalIgnoreCase);

    static double? Sum(IEnumerable<double?> values)
    {
        double total = 0; bool any = false;
        foreach (var v in values) if (v is { } x && double.IsFinite(x)) { total += x; any = true; }
        return any ? total : null;
    }

    /// <summary>The busiest processes of a sample: the top <paramref name="count"/> by CPU and by memory together.</summary>
    public static IReadOnlyList<ProcessTopEntry> TopProcesses(MonitorSnapshot snapshot, int count = 5)
    {
        var byCpu = snapshot.Processes.Where(p => p.CpuPercent is not null).OrderByDescending(p => p.CpuPercent).Take(count);
        var byMemory = snapshot.Processes.Where(p => p.MemoryBytes is not null).OrderByDescending(p => p.MemoryBytes).Take(count);
        return byCpu.Concat(byMemory).DistinctBy(p => p.Pid)
            .Select(p => new ProcessTopEntry(p.Name, p.Pid, p.User, p.CpuPercent, p.MemoryBytes)).ToArray();
    }
}

/// <summary>Result of one run of a custom metric command.</summary>
public sealed record CustomMetricResult(IReadOnlyList<MetricValue> Values, string? Error);

/// <summary>
/// Output of a custom metric command: a single number, or lines <c>name=value</c>. Anything else is an error with its
/// text, never a zero.
/// </summary>
public static partial class CustomMetricParser
{
    public const int MaxValues = 20;
    [GeneratedRegex("^[A-Za-z0-9_.-]{1,40}$")] private static partial Regex KeyPattern();

    public static bool TryNumber(string text, out double value)
    {
        text = text.Trim().TrimEnd('%').Trim();
        if (text.Contains(',') && !text.Contains('.')) text = text.Replace(',', '.');
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    public static CustomMetricResult Parse(string output, string metric, string unit)
    {
        var lines = output.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        if (lines.Length == 0) return new([], "Команда ничего не вывела.");
        if (lines.Length == 1 && !lines[0].Contains('=') && TryNumber(lines[0], out var single))
            return new([new MetricValue(MetricSeries.Custom(metric), single, unit)], null);
        var values = new List<MetricValue>();
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i]; int eq = line.IndexOf('=');
            if (eq <= 0) return new([], $"Строка {i + 1}: ожидается число или имя=значение: «{Short(line)}».");
            var key = line[..eq].Trim(); var text = line[(eq + 1)..];
            if (!KeyPattern().IsMatch(key)) return new([], $"Строка {i + 1}: имя «{Short(key)}» — только латиница, цифры, _ . - (до 40 знаков).");
            if (!TryNumber(text, out var v)) return new([], $"Строка {i + 1}: «{Short(text.Trim())}» — не число.");
            if (values.Count >= MaxValues) return new([], $"Больше {MaxValues} значений в одном выводе.");
            values.Add(new MetricValue(MetricSeries.Custom(metric, key), v, unit));
        }
        return new(values, null);
    }

    static string Short(string text) => text.Length > 60 ? text[..60] + "…" : text;
}

/// <summary>A ready custom metric: commands for Linux (sh) and Windows (PowerShell) with one optional parameter.</summary>
public sealed record CustomMetricTemplate(string Key, string Title, string Name, string Unit, string Linux, string Windows, string? Parameter = null, string? DefaultValue = null)
{
    /// <summary>The command with the parameter quoted for the target shell.</summary>
    public string Command(bool windows, string? value)
    {
        var command = windows ? Windows : Linux;
        if (Parameter is null) return command;
        value ??= DefaultValue ?? "";
        var quoted = windows ? "'" + value.Replace("'", "''") + "'" : "'" + value.Replace("'", "'\\''") + "'";
        return command.Replace("{p}", quoted, StringComparison.Ordinal);
    }
}

public static class CustomMetricTemplates
{
    public static readonly IReadOnlyList<CustomMetricTemplate> All =
    [
        new("nginx", "nginx: соединения (stub_status)", "nginx", "",
            "curl -fsS --max-time 10 {p} | awk '/^Active connections/{print \"active=\"$3} /^Reading/{print \"reading=\"$2; print \"writing=\"$4; print \"waiting=\"$6}'",
            "$t=(Invoke-WebRequest -UseBasicParsing -TimeoutSec 10 {p}).Content;if($t -match 'Active connections:\\s*(\\d+)'){'active='+$Matches[1]};if($t -match 'Reading:\\s*(\\d+)\\s*Writing:\\s*(\\d+)\\s*Waiting:\\s*(\\d+)'){'reading='+$Matches[1];'writing='+$Matches[2];'waiting='+$Matches[3]}",
            "Адрес stub_status", "http://127.0.0.1/nginx_status"),
        new("postgres", "PostgreSQL: подключения и размер БД", "postgres", "",
            "psql -XAtq -d {p} -c \"select 'connections='||count(*) from pg_stat_activity\" -c \"select 'size_mb='||pg_database_size(current_database())/1048576\"",
            "& psql -XAtq -d {p} -c \"select 'connections='||count(*) from pg_stat_activity\" -c \"select 'size_mb='||pg_database_size(current_database())/1048576\"",
            "База данных", "postgres"),
        new("docker-cpu", "Docker: ЦП контейнеров, %", "docker_cpu", "%",
            "docker stats --no-stream --format '{{.Name}}={{.CPUPerc}}' | tr -d '%'",
            "docker stats --no-stream --format '{{.Name}}={{.CPUPerc}}' | % { $_ -replace '%','' }"),
        new("docker-mem", "Docker: память контейнеров, %", "docker_mem", "%",
            "docker stats --no-stream --format '{{.Name}}={{.MemPerc}}' | tr -d '%'",
            "docker stats --no-stream --format '{{.Name}}={{.MemPerc}}' | % { $_ -replace '%','' }"),
        new("http", "Время ответа HTTP, мс", "http_ms", "мс",
            "curl -o /dev/null -fsS --max-time 20 -w '%{time_total}' {p} | awk '{printf \"%d\", $1*1000}'",
            "$s=[Diagnostics.Stopwatch]::StartNew();$null=Invoke-WebRequest -UseBasicParsing -TimeoutSec 20 {p};[int]$s.Elapsed.TotalMilliseconds",
            "Адрес", "https://127.0.0.1/"),
        new("tcp", "Установленные TCP-соединения", "tcp_established", "",
            "ss -Htan state established | wc -l",
            "@(Get-NetTCPConnection -State Established).Count"),
        new("service", "Служба работает (1 — да, 0 — нет)", "service", "",
            "systemctl is-active --quiet {p} && echo 1 || echo 0",
            "if((Get-Service -Name {p}).Status -eq 'Running'){1}else{0}",
            "Имя службы", "nginx"),
        new("files", "Файлов в папке", "files", "",
            "find {p} -maxdepth 1 -type f | wc -l",
            "@(Get-ChildItem -LiteralPath {p} -File).Count",
            "Папка", "/var/spool"),
        new("lines", "Строк в файле", "lines", "",
            "wc -l < {p}",
            "(Get-Content -LiteralPath {p} | Measure-Object -Line).Lines",
            "Файл", "/var/log/syslog"),
        new("folder-size", "Размер папки, МБ", "folder_mb", "МБ",
            "du -sm {p} | cut -f1",
            "[int]((Get-ChildItem -LiteralPath {p} -Recurse -File | Measure-Object Length -Sum).Sum/1MB)",
            "Папка", "/var/log"),
    ];

    /// <summary>A name that can be used in series names (latin letters, digits, _ . -).</summary>
    public static string SafeName(string name)
    {
        var b = new StringBuilder();
        foreach (var c in name.Trim()) b.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        var result = b.ToString().Trim('_');
        return result.Length == 0 ? "metric" : result.Length > 40 ? result[..40] : result;
    }
}
