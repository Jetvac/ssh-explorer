using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SSHExplorer.Monitoring;

public sealed record CpuCounters(ulong Total, ulong Idle, int CpuCount);
public sealed record ProcessStat(int Pid, string Name, string State, ulong CpuTicks,
    ulong StartTimeTicks, long ResidentPages);
public sealed record MemoryCounters(long? TotalBytes, long? AvailableBytes, long? UsedBytes,
    long? SwapTotalBytes, long? SwapUsedBytes, bool IsEstimate);

/// <summary>Parses only bounded protocol records. Base64 keeps NUL/newlines out of framing.</summary>
public static partial class ProcParser
{
    public const int MaxProtocolCharacters = 16 * 1024 * 1024;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static IReadOnlyDictionary<string, string?> DecodeFrames(string text) => DecodeFrames(text, "SSHXMON1", "SSHXMON2");

    /// <summary>Decodes a framed response whose first line is one of <paramref name="headers"/>.</summary>
    public static IReadOnlyDictionary<string, string?> DecodeFrames(string text, params string[] headers)
    {
        if (text.Length > MaxProtocolCharacters)
            throw new MonitoringUnavailableException("Ответ мониторинга превысил 16 МиБ. Уменьшите лимит процессов.");
        using var reader = new StringReader(text);
        var header = reader.ReadLine()?.TrimEnd('\r');
        if (header is null || !headers.Contains(header, StringComparer.Ordinal))
            throw new MonitoringUnavailableException("Сервер не вернул протокол мониторинга /proc.");
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        string? line;
        var ended = false;
        while ((line = reader.ReadLine()) is not null)
        {
            line = line.TrimEnd('\r');
            if (line == "END") { ended = true; break; }
            var tab = line.IndexOf('\t');
            if (tab is < 1 or > 100 || !KeyPattern().IsMatch(line[..tab]))
                throw new MonitoringUnavailableException("Повреждена граница записи мониторинга.");
            var key = line[..tab];
            var value = line[(tab + 1)..] == "!" ? null : Encoding.UTF8.GetString(Convert.FromBase64String(line[(tab + 1)..]));
            if (key == "procs") { ExpandProcessStream(value ?? "", result); continue; }
            if (!result.TryAdd(key, value))
                throw new MonitoringUnavailableException("Повторная запись в протоколе мониторинга.");
        }
        if (!ended) throw new MonitoringUnavailableException("Мониторинг прерван: неполный ответ сервера.");
        return result;
    }

    /// <summary>
    /// Protocol 2 process stream: five-line records (P pid / stat / uid / "read write" / stat again),
    /// "X pid" for a process that vanished before its stat was read and a final "C count limited".
    /// Expanded into the protocol 1 per-process keys so the calculator has a single input model.
    /// </summary>
    static void ExpandProcessStream(string text, Dictionary<string, string?> result)
    {
        var lines = text.Split('\n');
        bool counted = false;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0) continue;
            if (line.StartsWith("X ", StringComparison.Ordinal) && int.TryParse(line.AsSpan(2), NumberStyles.None, Invariant, out var gone))
            { result.TryAdd($"p/{gone}/stat", null); continue; }
            if (line.StartsWith("C ", StringComparison.Ordinal))
            {
                var words = Words(line[2..]);
                if (words.Length != 2 || !int.TryParse(words[0], NumberStyles.None, Invariant, out _)) throw new MonitoringUnavailableException("Повреждён итог списка процессов.");
                result["meta/count"] = words[0]; result["meta/limited"] = words[1] == "1" ? "1" : "0"; counted = true; continue;
            }
            if (!line.StartsWith("P ", StringComparison.Ordinal) || !int.TryParse(line.AsSpan(2), NumberStyles.None, Invariant, out var pid) || pid <= 0 || i + 4 >= lines.Length)
                throw new MonitoringUnavailableException("Повреждена запись процесса в протоколе мониторинга.");
            string stat = lines[i + 1], uid = lines[i + 2], io = lines[i + 3], end = lines[i + 4];
            i += 4;
            var prefix = $"p/{pid}/";
            if (!result.TryAdd(prefix + "stat", stat.Length == 0 ? null : stat + "\n"))
                throw new MonitoringUnavailableException("Повторная запись процесса в протоколе мониторинга.");
            result[prefix + "stat_end"] = end.Length == 0 ? null : end + "\n";
            var owner = uid.Split(' ');
            result[prefix + "status"] = owner[0].Length == 0 ? null : "Uid:\t" + owner[0] + "\n" + (owner.Length > 1 && owner[1].Length > 0 ? "VmRSS:\t" + owner[1] + " kB\n" : "");
            var counters = io.Split(' ');
            result[prefix + "io"] = counters.Length == 2 && counters[0].Length > 0 && counters[1].Length > 0
                ? "read_bytes: " + counters[0] + "\nwrite_bytes: " + counters[1] + "\n" : null;
        }
        if (!counted) throw new MonitoringUnavailableException("Мониторинг прерван: список процессов неполон.");
    }

    public static CpuCounters? ParseCpu(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var line = lines.FirstOrDefault(x => x.StartsWith("cpu ", StringComparison.Ordinal));
        if (line is null) return null;
        var p = Words(line);
        if (p.Length < 5) return null;
        ulong total = 0;
        try
        {
            // guest and guest_nice are already included in user/nice; never add twice.
            for (var i = 1; i < Math.Min(p.Length, 9); i++)
            {
                if (!ulong.TryParse(p[i], Invariant, out var n)) return null;
                total = checked(total + n);
            }
            if (!ulong.TryParse(p[4], Invariant, out var idle)) return null;
            if (p.Length > 5)
            {
                if (!ulong.TryParse(p[5], Invariant, out var wait)) return null;
                idle = checked(idle + wait);
            }
            var count = lines.Count(x => x.Length > 3 && x.StartsWith("cpu", StringComparison.Ordinal)
                && char.IsAsciiDigit(x[3]));
            return new CpuCounters(total, idle, count);
        }
        catch (OverflowException) { return null; }
    }

    public static MemoryCounters ParseMemory(string? text)
    {
        var fields = ParseColonFields(text);
        var total = KibiBytes(fields.GetValueOrDefault("MemTotal"));
        var available = KibiBytes(fields.GetValueOrDefault("MemAvailable"));
        var estimate = false;
        if (available is null)
        {
            var free = KibiBytes(fields.GetValueOrDefault("MemFree"));
            var buffers = KibiBytes(fields.GetValueOrDefault("Buffers"));
            var cached = KibiBytes(fields.GetValueOrDefault("Cached"));
            if (free is not null && buffers is not null && cached is not null)
            {
                available = SumSafe(free.Value, buffers.Value, cached.Value);
                estimate = true;
            }
        }
        if (available is not null && total is not null)
            available = Math.Clamp(available.Value, 0, total.Value);
        var swapTotal = KibiBytes(fields.GetValueOrDefault("SwapTotal"));
        var swapFree = KibiBytes(fields.GetValueOrDefault("SwapFree"));
        return new MemoryCounters(total, available,
            total is not null && available is not null ? total - available : null,
            swapTotal, swapTotal is not null && swapFree is not null
                ? Math.Max(0, swapTotal.Value - swapFree.Value) : null, estimate);
    }

    public static ProcessStat? ParseProcessStat(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var open = text.IndexOf('(');
        var close = text.LastIndexOf(") ", StringComparison.Ordinal);
        if (open < 1 || close < open || !int.TryParse(text[..open].Trim(), Invariant, out var pid) || pid < 1)
            return null;
        // comm may contain spaces, ')', and newlines. The final ") " is the actual terminator.
        var p = Words(text[(close + 2)..]);
        if (p.Length < 22 || p[0].Length != 1 ||
            !ulong.TryParse(p[11], Invariant, out var user) ||
            !ulong.TryParse(p[12], Invariant, out var kernel) ||
            !ulong.TryParse(p[19], Invariant, out var start) ||
            !long.TryParse(p[21], Invariant, out var rss)) return null;
        try { return new ProcessStat(pid, text[(open + 1)..close], MetricFormatter.ProcessState(p[0]), checked(user + kernel), start, rss); }
        catch (OverflowException) { return null; }
    }

    public static IReadOnlyList<StorageMetric> ParseStorage(string? text)
    {
        var rows = new List<StorageMetric>();
        if (text is null) return rows;
        foreach (var line in text.Split('\n'))
        {
            var m = DfPattern().Match(line);
            if (!m.Success || !TryKiB(m.Groups[2].Value, out var total) ||
                !TryKiB(m.Groups[3].Value, out var used) || !TryKiB(m.Groups[4].Value, out var available)) continue;
            rows.Add(new StorageMetric(m.Groups[1].Value, m.Groups[6].Value, total, used, available));
        }
        return rows;
    }

    public static IReadOnlyDictionary<string, string> ParseColonFields(string? text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (text is null) return result;
        foreach (var line in text.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) result[line[..colon]] = line[(colon + 1)..].Trim();
        }
        return result;
    }

    internal static string[] Words(string value) => value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    internal static long? KibiBytes(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return TryKiB(Words(value)[0], out var bytes) ? bytes : null;
    }
    internal static double? PositiveDouble(string? value) => double.TryParse(value, NumberStyles.Float,
        Invariant, out var n) && double.IsFinite(n) && n >= 0 ? n : null;
    internal static ulong? Unsigned(string? value) => ulong.TryParse(value, Invariant, out var n) ? n : null;
    private static bool TryKiB(string text, out long bytes)
    {
        bytes = 0;
        if (!long.TryParse(text, Invariant, out var n) || n < 0 || n > long.MaxValue / 1024) return false;
        bytes = n * 1024;
        return true;
    }
    private static long? SumSafe(params long[] values)
    {
        try { long total = 0; foreach (var value in values) total = checked(total + value); return total; }
        catch (OverflowException) { return null; }
    }
    [GeneratedRegex("^[a-z0-9_/-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
    [GeneratedRegex("^([^\\s]+)\\s+([0-9]+)\\s+([0-9]+)\\s+([0-9]+)\\s+([0-9]+)%\\s+(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex DfPattern();
}
