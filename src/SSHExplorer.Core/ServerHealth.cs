using System.Globalization;

namespace SSHExplorer.Core;

/// <summary>Something worth a notification on a server: a nearly full disk or a failed service.</summary>
public sealed record HealthIssue(string Key, string Text);

/// <summary>A light check run every few minutes on connected servers (no root needed).</summary>
public static class ServerHealth
{
    public const string Command =
        "LC_ALL=C; export LC_ALL; df -P -x tmpfs -x devtmpfs -x squashfs -x overlay -x efivarfs 2>/dev/null | tail -n +2 | head -n 60; echo '#failed'; " +
        "if command -v systemctl >/dev/null 2>&1; then systemctl --failed --no-legend --plain --no-pager 2>/dev/null | head -n 40; fi; echo '#end'";

    public static IReadOnlyList<HealthIssue> Parse(string output, int diskPercent)
    {
        var issues = new List<HealthIssue>(); bool failed = false;
        foreach (var raw in output.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim(); if (line.Length == 0) continue;
            if (line == "#failed") { failed = true; continue; }
            if (line == "#end") break;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!failed)
            {
                // Filesystem 1024-blocks Used Available Capacity Mounted-on
                if (parts.Length < 6 || !parts[4].EndsWith('%') || !int.TryParse(parts[4].TrimEnd('%'), NumberStyles.None, CultureInfo.InvariantCulture, out var used)) continue;
                var mount = string.Join(' ', parts.Skip(5));
                if (used >= diskPercent) issues.Add(new HealthIssue("disk:" + mount, $"Диск {mount} заполнен на {used}%" + (long.TryParse(parts[3], out var free) ? $" (свободно {Size(free * 1024)})" : "")));
            }
            else
            {
                var unit = parts.FirstOrDefault(p => p.Contains('.'));
                if (unit is not null) issues.Add(new HealthIssue("unit:" + unit, $"Служба {unit} в состоянии failed"));
            }
        }
        return issues;
    }

    static string Size(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} ГБ" : $"{bytes / (double)(1L << 20):0} МБ";

    /// <summary>What appeared since the last check, and which keys went away (resolved).</summary>
    public static (IReadOnlyList<HealthIssue> New, IReadOnlyList<string> Resolved) Diff(IReadOnlyCollection<string> previous, IReadOnlyList<HealthIssue> now)
    {
        var keys = now.Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
        return (now.Where(i => !previous.Contains(i.Key)).ToArray(), previous.Where(k => !keys.Contains(k)).ToArray());
    }
}
