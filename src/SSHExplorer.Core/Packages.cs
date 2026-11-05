using System.Globalization;

namespace SSHExplorer.Core;

public sealed record PackageUpdate(string Name, string Version, string? Current, string Source, bool Security);

/// <summary>Pending package updates as the server's package manager sees them (no download, no lock taken).</summary>
public sealed record PackageReport(string Manager, IReadOnlyList<PackageUpdate> Updates, double? ListsAgeHours, bool RebootRequired, string? Problem)
{
    public int SecurityCount => Updates.Count(u => u.Security);
    public string Summary => Problem ?? (Updates.Count == 0 ? "Обновлений нет" : $"Обновлений: {Updates.Count}" + (SecurityCount > 0 ? $", безопасности: {SecurityCount}" : "")) + (RebootRequired ? " · требуется перезагрузка" : "");
}

/// <summary>
/// Reading pending updates for apt (Debian, Ubuntu) and dnf/yum (Fedora, RHEL and relatives) without root: from the
/// package lists already on the server. Installing is done in the terminal by the user, so sudo asks its password
/// there, never in this app; the commands are offered ready to run.
/// </summary>
public static class Packages
{
    public const string ReadCommand =
        "LC_ALL=C; export LC_ALL\n" +
        "if command -v apt-get >/dev/null 2>&1; then echo '#mgr apt'; echo \"#age $(( $(date +%s) - $(stat -c %Y /var/lib/apt/lists/partial 2>/dev/null || stat -c %Y /var/lib/apt/lists 2>/dev/null || date +%s) ))\"; " +
        "apt list --upgradable 2>/dev/null | grep -v '^Listing' | head -n 3000; " +
        "elif command -v dnf >/dev/null 2>&1; then echo '#mgr dnf'; dnf -q -C check-update 2>/dev/null | head -n 3000; echo '#security'; dnf -q -C updateinfo list --security 2>/dev/null | head -n 3000; " +
        "elif command -v yum >/dev/null 2>&1; then echo '#mgr yum'; yum -q -C check-update 2>/dev/null | head -n 3000; echo '#security'; yum -q -C updateinfo list security 2>/dev/null | head -n 3000; " +
        "else echo '#mgr none'; fi\n" +
        "if [ -f /var/run/reboot-required ] || [ -f /run/reboot-required ]; then echo '#reboot'; fi; echo '#end'";

    public static PackageReport Parse(string output)
    {
        var lines = output.Replace("\r", "").Split('\n');
        string manager = ""; double? age = null; bool reboot = false, security = false;
        var updates = new List<PackageUpdate>(); var securityNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("#mgr ", StringComparison.Ordinal)) { manager = line[5..]; continue; }
            if (line.StartsWith("#age ", StringComparison.Ordinal)) { if (double.TryParse(line[5..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)) age = s / 3600; continue; }
            if (line == "#security") { security = true; continue; }
            if (line == "#reboot") { reboot = true; continue; }
            if (line == "#end") break;
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (manager == "apt")
            {
                // openssl/jammy-updates,jammy-security 3.0.2-0ubuntu1.15 amd64 [upgradable from: 3.0.2-0ubuntu1.14]
                var slash = line.IndexOf('/'); if (slash <= 0) continue;
                var parts = line[(slash + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries); if (parts.Length < 2) continue;
                var from = line.IndexOf("[upgradable from: ", StringComparison.Ordinal);
                var current = from > 0 ? line[(from + 18)..].TrimEnd(']') : null;
                updates.Add(new PackageUpdate(line[..slash], parts[1], current, parts[0], parts[0].Contains("-security", StringComparison.Ordinal)));
            }
            else if (manager is "dnf" or "yum")
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (security)
                {
                    // FEDORA-2026-abc  Important/Sec.  openssl-3.1.4-2.fc39.x86_64
                    if (parts.Length >= 3 && parts[1].Contains("Sec", StringComparison.OrdinalIgnoreCase)) securityNames.Add(parts[^1]);
                    continue;
                }
                if (parts.Length != 3 || parts[0].StartsWith("Obsoleting", StringComparison.Ordinal) || !parts[0].Contains('.')) continue;
                updates.Add(new PackageUpdate(parts[0][..parts[0].LastIndexOf('.')], parts[1], null, parts[2], false));
            }
        }
        if (securityNames.Count > 0)
            updates = updates.Select(u => securityNames.Any(n => n.StartsWith(u.Name + "-" + u.Version.Split(':')[^1], StringComparison.Ordinal) || n.StartsWith(u.Name + "-", StringComparison.Ordinal) && n.Contains(u.Version.Split(':')[^1], StringComparison.Ordinal)) ? u with { Security = true } : u).ToList();
        string? problem = manager switch { "" => "Не удалось определить менеджер пакетов.", "none" => "Менеджер пакетов apt, dnf или yum не найден.", _ => null };
        return new PackageReport(manager, updates.OrderByDescending(u => u.Security).ThenBy(u => u.Name, StringComparer.Ordinal).ToList(), age, reboot, problem);
    }

    /// <summary>The command to type in the terminal; the user reads it, runs it and answers sudo there.</summary>
    public static string UpgradeCommand(PackageReport report, bool securityOnly) => report.Manager switch
    {
        "apt" when securityOnly && report.SecurityCount > 0 => "sudo apt-get update && sudo apt-get install --only-upgrade " + string.Join(' ', report.Updates.Where(u => u.Security).Select(u => PosixPath.Quote(u.Name))),
        "apt" => "sudo apt-get update && sudo apt-get upgrade",
        "dnf" => securityOnly ? "sudo dnf upgrade --security" : "sudo dnf upgrade",
        "yum" => securityOnly ? "sudo yum update --security" : "sudo yum update",
        _ => "",
    };

    public static string RefreshCommand(PackageReport report) => report.Manager switch { "apt" => "sudo apt-get update", "dnf" => "sudo dnf makecache", "yum" => "sudo yum makecache", _ => "" };
}
