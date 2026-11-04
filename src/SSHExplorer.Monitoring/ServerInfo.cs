using System.Globalization;

namespace SSHExplorer.Monitoring;

public sealed record NetworkAddress(string Interface, string Family, string Address);

/// <summary>How a power action would be sent. Exactly one method is chosen; nothing is tried blindly.</summary>
public enum PowerMethod { None, SystemctlAsRoot, SystemctlViaLogind, SystemctlViaSudo, SysVAsRoot, SysVViaSudo }
public enum PowerAction { Reboot, PowerOff }

public sealed record PowerCapability(PowerAction Action, PowerMethod Method, string Command, string Explanation)
{
    public bool Available => Method != PowerMethod.None;
}

/// <summary>Read-only facts about a server. Null/empty means "not available", never zero.</summary>
public sealed record ServerInfo
{
    public string? Hostname { get; init; }
    public string? OsName { get; init; }
    public string? OsVersion { get; init; }
    public string? OsId { get; init; }
    public string? Kernel { get; init; }
    public string? Architecture { get; init; }
    public string? CpuModel { get; init; }
    public int? CpuCount { get; init; }
    public long? MemoryTotalBytes { get; init; }
    public long? MemoryAvailableBytes { get; init; }
    public long? SwapTotalBytes { get; init; }
    public long? SwapUsedBytes { get; init; }
    public TimeSpan? Uptime { get; init; }
    public double? Load1 { get; init; }
    public double? Load5 { get; init; }
    public double? Load15 { get; init; }
    public string? BootId { get; init; }
    public string? User { get; init; }
    public uint? UserId { get; init; }
    public bool Systemd { get; init; }
    public IReadOnlyList<NetworkAddress> Addresses { get; init; } = [];
    public IReadOnlyList<StorageMetric> Storage { get; init; } = [];
    public IReadOnlyList<string> Tools { get; init; } = [];
    public PowerCapability Reboot { get; init; } = new(PowerAction.Reboot, PowerMethod.None, "", "Не определено.");
    public PowerCapability PowerOff { get; init; } = new(PowerAction.PowerOff, PowerMethod.None, "", "Не определено.");
    public IReadOnlyList<string> Limitations { get; init; } = [];
    public DateTimeOffset CollectedAt { get; init; }
    public bool IsRoot => UserId == 0;
}

/// <summary>
/// One read-only SSH exec: /etc/os-release, /proc and existing commands (uname, id, ip, df,
/// busctl, sudo -n -l). Nothing is installed, configured or executed with elevated rights;
/// <c>sudo -n -l CMD</c> only asks sudo whether CMD would be allowed without a password.
/// </summary>
public static class ServerInfoCollector
{
    public const string Header = "SSHXINFO1";

    public static string Command => """
        LC_ALL=C; export LC_ALL
        if printf x | base64 -w0 >/dev/null 2>&1; then encode() { base64 -w0; }; else encode() { base64 | tr -d '\n'; }; fi
        printf 'SSHXINFO1\n'
        frame() { printf '%s\t' "$1"; if [ -r "$2" ]; then dd if="$2" bs="$3" count=1 2>/dev/null | encode; else printf '!'; fi; printf '\n'; }
        meta() { printf '%s\t' "$1"; printf '%s' "$2" | encode; printf '\n'; }
        run() { k=$1; shift; printf '%s\t' "$k"; if command -v "$1" >/dev/null 2>&1; then "$@" 2>/dev/null | dd bs=65536 count=1 2>/dev/null | encode; else printf '!'; fi; printf '\n'; }
        if [ -r /etc/os-release ]; then frame os /etc/os-release 16384; else frame os /usr/lib/os-release 16384; fi
        frame hostname /proc/sys/kernel/hostname 256
        frame kernel /proc/sys/kernel/osrelease 256
        frame cpuinfo /proc/cpuinfo 262144
        frame meminfo /proc/meminfo 16384
        frame uptime /proc/uptime 256
        frame loadavg /proc/loadavg 256
        frame boot_id /proc/sys/kernel/random/boot_id 128
        meta arch "$(uname -m 2>/dev/null)"
        meta user "$(id -un 2>/dev/null)"
        meta uid "$(id -u 2>/dev/null)"
        if [ -d /run/systemd/system ]; then meta systemd 1; else meta systemd 0; fi
        tools=''
        for t in systemctl busctl shutdown reboot poweroff sudo ip df; do command -v "$t" >/dev/null 2>&1 && tools="$tools $t"; done
        meta tools "$tools"
        run addr ip -o addr show
        run df df -Pk
        if [ -d /run/systemd/system ] && command -v busctl >/dev/null 2>&1; then
          meta can_reboot "$(busctl call org.freedesktop.login1 /org/freedesktop/login1 org.freedesktop.login1.Manager CanReboot 2>/dev/null)"
          meta can_poweroff "$(busctl call org.freedesktop.login1 /org/freedesktop/login1 org.freedesktop.login1.Manager CanPowerOff 2>/dev/null)"
        fi
        if [ "$(id -u 2>/dev/null)" != 0 ] && command -v sudo >/dev/null 2>&1; then
          s=$(command -v systemctl 2>/dev/null); r=$(command -v shutdown 2>/dev/null)
          if [ -n "$s" ]; then
            sudo -n -l "$s" reboot >/dev/null 2>&1 && meta sudo_reboot 1 || meta sudo_reboot 0
            sudo -n -l "$s" poweroff >/dev/null 2>&1 && meta sudo_poweroff 1 || meta sudo_poweroff 0
          elif [ -n "$r" ]; then
            sudo -n -l "$r" -r now >/dev/null 2>&1 && meta sudo_reboot 1 || meta sudo_reboot 0
            sudo -n -l "$r" -h now >/dev/null 2>&1 && meta sudo_poweroff 1 || meta sudo_poweroff 0
          fi
        fi
        printf 'END\n'
        """;

    public static ServerInfo Parse(string output, DateTimeOffset collectedAt)
    {
        var frames = ProcParser.DecodeFrames(output, Header);
        string? F(string key) => frames.GetValueOrDefault(key);
        var limitations = new List<string>();
        var os = ParseOsRelease(F("os"));
        if (os.Count == 0) limitations.Add("/etc/os-release недоступен: название ОС не определено.");
        var memory = ProcParser.ParseMemory(F("meminfo"));
        var load = ProcParser.Words(F("loadavg") ?? "");
        var up = ProcParser.Words(F("uptime") ?? "");
        var cpu = F("cpuinfo") ?? "";
        string? model = null; int processors = 0;
        foreach (var line in cpu.Split('\n'))
        {
            var colon = line.IndexOf(':'); if (colon < 0) continue;
            var key = line[..colon].Trim(); var value = line[(colon + 1)..].Trim();
            if (key == "processor") processors++;
            else if (model is null && key is "model name" or "Hardware" or "cpu model" or "uarch" && value.Length > 0) model = value;
        }
        var tools = (F("tools") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        uint? uid = uint.TryParse(F("uid")?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var u) ? u : null;
        bool systemd = F("systemd")?.Trim() == "1";
        var info = new ServerInfo
        {
            Hostname = Clean(F("hostname")),
            OsName = os.GetValueOrDefault("PRETTY_NAME") ?? os.GetValueOrDefault("NAME"),
            OsVersion = os.GetValueOrDefault("VERSION") ?? os.GetValueOrDefault("VERSION_ID"),
            OsId = os.GetValueOrDefault("ID"),
            Kernel = Clean(F("kernel")),
            Architecture = Clean(F("arch")),
            CpuModel = model,
            CpuCount = processors > 0 ? processors : null,
            MemoryTotalBytes = memory.TotalBytes, MemoryAvailableBytes = memory.AvailableBytes,
            SwapTotalBytes = memory.SwapTotalBytes, SwapUsedBytes = memory.SwapUsedBytes,
            Uptime = up.Length > 0 && ProcParser.PositiveDouble(up[0]) is { } seconds && seconds < TimeSpan.MaxValue.TotalSeconds ? TimeSpan.FromSeconds(seconds) : null,
            Load1 = load.Length > 0 ? ProcParser.PositiveDouble(load[0]) : null,
            Load5 = load.Length > 1 ? ProcParser.PositiveDouble(load[1]) : null,
            Load15 = load.Length > 2 ? ProcParser.PositiveDouble(load[2]) : null,
            BootId = Clean(F("boot_id")),
            User = Clean(F("user")), UserId = uid, Systemd = systemd,
            Addresses = ParseAddresses(F("addr")),
            Storage = ProcParser.ParseStorage(F("df")),
            Tools = tools,
            CollectedAt = collectedAt,
        };
        if (info.Addresses.Count == 0) limitations.Add(tools.Contains("ip") ? "Адреса интерфейсов не получены." : "Команда ip отсутствует: адреса интерфейсов не показываются.");
        if (info.Uptime is null) limitations.Add("/proc/uptime недоступен.");
        if (info.BootId is null) limitations.Add("boot_id недоступен: перезагрузку можно подтвердить только по uptime.");
        return info with
        {
            Reboot = Capability(PowerAction.Reboot, info, F("can_reboot"), F("sudo_reboot")),
            PowerOff = Capability(PowerAction.PowerOff, info, F("can_poweroff"), F("sudo_poweroff")),
            Limitations = limitations,
        };
    }

    static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>/etc/os-release is shell-like data. It is parsed, never sourced or executed.</summary>
    public static Dictionary<string, string> ParseOsRelease(string? text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim(); if (line.Length == 0 || line[0] == '#') continue;
            var eq = line.IndexOf('='); if (eq <= 0) continue;
            var key = line[..eq]; if (!key.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_')) continue;
            var value = line[(eq + 1)..];
            if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\''))
            {
                bool single = value[0] == '\''; value = value[1..^1];
                if (!single) value = value.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\$", "$", StringComparison.Ordinal).Replace("\\`", "`", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
            }
            result[key] = value;
        }
        return result;
    }

    public static IReadOnlyList<NetworkAddress> ParseAddresses(string? text)
    {
        var list = new List<NetworkAddress>();
        foreach (var line in (text ?? "").Split('\n'))
        {
            // "2: eth0    inet 10.0.0.5/24 brd ... scope global eth0"
            var words = ProcParser.Words(line);
            if (words.Length < 4) continue;
            var family = words[2]; if (family is not ("inet" or "inet6")) continue;
            var name = words[1].TrimEnd(':'); var address = words[3];
            if (name == "lo") continue;
            list.Add(new NetworkAddress(name, family == "inet" ? "IPv4" : "IPv6", address));
        }
        return list;
    }

    static PowerCapability Capability(PowerAction action, ServerInfo info, string? logind, string? sudo)
    {
        string verb = action == PowerAction.Reboot ? "reboot" : "poweroff";
        bool systemctl = info.Tools.Contains("systemctl") && info.Systemd;
        bool shutdown = info.Tools.Contains("shutdown");
        string sysV = action == PowerAction.Reboot ? "shutdown -r now" : "shutdown -h now";
        if (info.IsRoot)
        {
            if (systemctl) return new(action, PowerMethod.SystemctlAsRoot, "systemctl " + verb, "Учётная запись root: команда systemctl " + verb + ".");
            if (shutdown) return new(action, PowerMethod.SysVAsRoot, sysV, "Учётная запись root, система без systemd: " + sysV + ".");
            return new(action, PowerMethod.None, "", "На сервере нет systemctl или shutdown.");
        }
        if (info.UserId is null) return new(action, PowerMethod.None, "", "Не удалось определить пользователя SSH (id -u).");
        if (systemctl && logind?.Trim() is { } answer && answer.EndsWith("\"yes\"", StringComparison.Ordinal))
            return new(action, PowerMethod.SystemctlViaLogind, "systemctl " + verb, "systemd-logind разрешает этому пользователю " + (action == PowerAction.Reboot ? "перезагрузку" : "выключение") + " без пароля.");
        if (sudo?.Trim() == "1")
        {
            if (systemctl) return new(action, PowerMethod.SystemctlViaSudo, "sudo -n systemctl " + verb, "sudo разрешает systemctl " + verb + " без пароля (sudo -n -l).");
            if (shutdown) return new(action, PowerMethod.SysVViaSudo, "sudo -n " + sysV, "sudo разрешает " + sysV + " без пароля (sudo -n -l).");
        }
        var reason = info.User + " не root; ";
        reason += logind?.Trim() is { Length: > 0 } l && l.Contains("challenge", StringComparison.Ordinal) ? "systemd-logind требует интерактивного подтверждения, " : "";
        reason += info.Tools.Contains("sudo") ? "sudo требует пароль либо не разрешает команду. " : "sudo отсутствует. ";
        reason += "Пароль SSH не используется как пароль sudo, настройки сервера не меняются.";
        return new(action, PowerMethod.None, "", reason);
    }
}
