using System.Globalization;
using System.Text.RegularExpressions;

namespace SSHExplorer.Core;

/// <summary>One systemd service: runtime state from list-units, autostart state from list-unit-files.</summary>
public sealed record ServiceUnit(string Name, string Load, string Active, string Sub, string UnitFileState, string Description)
{
    public bool IsRunning => Active == "active";
    public bool IsFailed => Active == "failed";
    public string SearchText => $"{Name} {Description} {Active} {Sub} {UnitFileState}";
    /// <summary>A short Russian state for the table; the raw values stay in the tooltip.</summary>
    public string StateText => Active switch
    {
        "active" => Sub == "running" ? "Работает" : Sub == "exited" ? "Выполнена" : "Активна (" + Sub + ")",
        "failed" => "Сбой",
        "activating" => "Запускается",
        "deactivating" => "Останавливается",
        "reloading" => "Перечитывает настройки",
        _ => "Остановлена",
    };
    public string StartupText => UnitFileState switch
    {
        "enabled" or "enabled-runtime" => "Автозапуск",
        "disabled" => "Вручную",
        "static" => "Зависимость",
        "masked" or "masked-runtime" => "Заблокирована",
        "indirect" => "Косвенно",
        "generated" or "transient" => "Создана системой",
        "" => "—",
        _ => UnitFileState,
    };
}

public enum ServiceAction { Start, Stop, Restart, Reload, Enable, Disable }

public sealed record ServicesSnapshot(IReadOnlyList<ServiceUnit> Units, bool Available, bool IsRoot, bool Sudo, string? Error);

/// <summary>
/// systemd services through systemctl: read without elevation; actions run directly as root, through non-interactive
/// sudo when the account is allowed to run systemctl that way, otherwise directly with --no-ask-password (polkit decides).
/// A password is never asked for or sent.
/// </summary>
public static partial class Systemd
{
    public const string ListCommand =
        "LC_ALL=C; export LC_ALL; if ! command -v systemctl >/dev/null 2>&1 || [ ! -d /run/systemd/system ]; then echo '#none'; exit 0; fi; " +
        "echo \"#uid $(id -u)\"; if command -v sudo >/dev/null 2>&1 && sudo -n -l systemctl >/dev/null 2>&1; then echo '#sudo'; fi; " +
        "echo '#units'; systemctl list-units --type=service --all --no-legend --no-pager --plain 2>/dev/null; " +
        "echo '#files'; systemctl list-unit-files --type=service --no-legend --no-pager 2>/dev/null";

    public static ServicesSnapshot Parse(string output)
    {
        string section = ""; bool root = false, sudo = false;
        var units = new Dictionary<string, ServiceUnit>(StringComparer.Ordinal); var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in output.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line == "#none") return new ServicesSnapshot([], false, false, false, "На сервере нет systemd (systemctl не найден или systemd не запущен).");
            if (line.StartsWith("#uid ", StringComparison.Ordinal)) { root = line[5..].Trim() == "0"; continue; }
            if (line == "#sudo") { sudo = true; continue; }
            if (line is "#units" or "#files") { section = line; continue; }
            var parts = line.TrimStart('●', '*', ' ').Split((char[]?)null, section == "#units" ? 5 : 3, StringSplitOptions.RemoveEmptyEntries);
            if (section == "#units" && parts.Length >= 4 && parts[0].EndsWith(".service", StringComparison.Ordinal))
                units[parts[0]] = new ServiceUnit(parts[0], parts[1], parts[2], parts[3], "", parts.Length > 4 ? parts[4].Trim() : "");
            else if (section == "#files" && parts.Length >= 2 && parts[0].EndsWith(".service", StringComparison.Ordinal)) files[parts[0]] = parts[1];
        }
        foreach (var (name, state) in files)
        {
            if (units.TryGetValue(name, out var unit)) units[name] = unit with { UnitFileState = state };
            // Templates (name@.service) need an instance and cannot be started as they are.
            else if (!name.EndsWith("@.service", StringComparison.Ordinal)) units[name] = new ServiceUnit(name, state == "masked" ? "masked" : "loaded", "inactive", "dead", state, "");
        }
        var ordered = units.Values.OrderBy(u => u.IsFailed ? 0 : u.IsRunning ? 1 : 2).ThenBy(u => u.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        return new ServicesSnapshot(ordered, true, root, sudo, null);
    }

    public static bool IsValidUnit(string name) => UnitRegex().IsMatch(name);

    static string Verb(ServiceAction action) => action switch
    {
        ServiceAction.Start => "start", ServiceAction.Stop => "stop", ServiceAction.Restart => "restart", ServiceAction.Reload => "reload",
        ServiceAction.Enable => "enable", _ => "disable",
    };

    public static string Label(ServiceAction action) => action switch
    {
        ServiceAction.Start => "Запустить", ServiceAction.Stop => "Остановить", ServiceAction.Restart => "Перезапустить", ServiceAction.Reload => "Перечитать настройки",
        ServiceAction.Enable => "Включить автозапуск", _ => "Отключить автозапуск",
    };

    static string Prefix(bool root, bool sudo) => root ? "" : sudo ? "sudo -n " : "";

    public static string ActionCommand(string unit, ServiceAction action, bool root, bool sudo)
    {
        if (!IsValidUnit(unit)) throw new ArgumentException("Недопустимое имя службы: " + unit, nameof(unit));
        return $"LC_ALL=C {Prefix(root, sudo)}systemctl --no-ask-password {Verb(action)} -- {PosixPath.Quote(unit)} 2>&1";
    }

    /// <summary>Properties for the details pane, as key=value lines.</summary>
    public static string ShowCommand(string unit)
    {
        if (!IsValidUnit(unit)) throw new ArgumentException("Недопустимое имя службы: " + unit, nameof(unit));
        return "LC_ALL=C systemctl show --no-pager -p Id,Description,LoadState,ActiveState,SubState,UnitFileState,MainPID,ExecMainStartTimestamp,ActiveEnterTimestamp,FragmentPath,MemoryCurrent,Restart,User,NRestarts -- " + PosixPath.Quote(unit);
    }

    public static IReadOnlyDictionary<string, string> ParseShow(string output)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries)) { int eq = line.IndexOf('='); if (eq > 0) map[line[..eq]] = line[(eq + 1)..]; }
        return map;
    }

    /// <summary>The service's journal: the last <paramref name="lines"/> entries, and with <paramref name="follow"/> new ones as they come.</summary>
    public static string JournalCommand(string unit, int lines, bool follow, bool root, bool sudo)
    {
        if (!IsValidUnit(unit)) throw new ArgumentException("Недопустимое имя службы: " + unit, nameof(unit));
        return $"{Prefix(root, sudo)}journalctl --no-pager -o short-iso -n {Math.Clamp(lines, 1, 100000).ToString(CultureInfo.InvariantCulture)}{(follow ? " -f" : "")} -u {PosixPath.Quote(unit)} 2>&1";
    }

    /// <summary>A file's last lines and, with <paramref name="follow"/>, what is appended (tail -F also survives log rotation).</summary>
    public static string TailCommand(string path, int lines, bool follow) =>
        $"tail -n {Math.Clamp(lines, 1, 100000).ToString(CultureInfo.InvariantCulture)}{(follow ? " -F" : "")} -- {PosixPath.Quote(path)} 2>&1";

    /// <summary>Stopping or restarting the SSH daemon may cut this very connection: the confirmation says so.</summary>
    public static bool MayDropConnection(string unit, ServiceAction action) =>
        action is ServiceAction.Stop or ServiceAction.Restart && unit is "ssh.service" or "sshd.service" or "openssh-server.service" or "dropbear.service";

    [GeneratedRegex("^[A-Za-z0-9:_.\\\\@-]{1,200}\\.service$", RegexOptions.CultureInvariant)] private static partial Regex UnitRegex();
}
