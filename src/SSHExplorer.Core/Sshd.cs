using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SSHExplorer.Core;

/// <summary>The SSH server's settings as this connection sees them.</summary>
public sealed record SshdSnapshot(string? Problem, IReadOnlyDictionary<string, IReadOnlyList<string>> Settings, bool Effective, bool IncludesDropIns,
    IReadOnlyDictionary<string, string> Ours, string User, bool IsRoot)
{
    public string Get(string key) => Settings.TryGetValue(key.ToLowerInvariant(), out var v) && v.Count > 0 ? v[0] : "";
    public IReadOnlyList<string> All(string key) => Settings.TryGetValue(key.ToLowerInvariant(), out var v) ? v : [];
}

public enum Severity { High, Medium, Low, Info }

/// <summary>One observation with an optional fix (setting → value) and, when the fix would lock you out, why it is blocked.</summary>
public sealed record SshdFinding(string Id, Severity Severity, string Title, string Detail, IReadOnlyDictionary<string, string>? Fix, string? Blocked)
{
    public string SeverityText => Severity switch { Severity.High => "Важно", Severity.Medium => "Желательно", Severity.Low => "Можно улучшить", _ => "Сведения" };
}

/// <summary>
/// Reading and hardening sshd. The effective settings come from <c>sshd -T</c> for this very user and address (so
/// Match blocks and drop-ins count); without root the main files are read as an approximation. Fixes go into
/// <c>/etc/ssh/sshd_config.d/00-sshexplorer.conf</c> — the first file read, so its values win — or, when the main file
/// has no Include, into a marked block at its top. Every change is checked by <c>sshd -t</c>, applied by a reload
/// (open sessions stay), checked by a brand-new connection and otherwise restored, like the firewall.
/// </summary>
public static partial class Sshd
{
    public const string DropIn = "/etc/ssh/sshd_config.d/00-sshexplorer.conf";
    const string Begin = "# SSH Explorer — begin", End = "# SSH Explorer — end";
    /// <summary>Settings the app may write, with what each accepts.</summary>
    static readonly Dictionary<string, Regex> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PasswordAuthentication"] = YesNo(), ["KbdInteractiveAuthentication"] = YesNo(), ["PermitEmptyPasswords"] = YesNo(), ["PubkeyAuthentication"] = YesNo(), ["X11Forwarding"] = YesNo(),
        ["PermitRootLogin"] = RootValue(), ["MaxAuthTries"] = Number(), ["LoginGraceTime"] = Number(),
    };

    public static string ReadCommand(string prefix) =>
        "PATH=$PATH:/usr/sbin:/sbin; export PATH; LC_ALL=C; export LC_ALL\n" +
        "echo \"#user $(id -un) $(id -u)\"\n" +
        "a=$(echo \"${SSH_CLIENT:-127.0.0.1}\" | cut -d' ' -f1); u=$(id -un); h=$(hostname 2>/dev/null || echo localhost)\n" +
        $"out=$({prefix}sshd -T -C \"user=$u,host=$h,addr=$a\" 2>&1); rc=$?\n" +
        "if [ $rc -eq 0 ]; then echo '#effective'; printf '%s\\n' \"$out\" | head -n 600; else echo \"#noeffective $(printf '%s' \"$out\" | head -n 1)\"; echo '#files'; " +
        "cat /etc/ssh/sshd_config 2>/dev/null | head -n 600; for f in /etc/ssh/sshd_config.d/*.conf; do [ -r \"$f\" ] && cat \"$f\"; done 2>/dev/null | head -n 600; fi\n" +
        "echo '#include'; if grep -Eiq '^[[:space:]]*Include[[:space:]]+/etc/ssh/sshd_config\\.d/' /etc/ssh/sshd_config 2>/dev/null; then echo yes; else echo no; fi\n" +
        $"echo '#ours'; if [ -f '{DropIn}' ]; then cat '{DropIn}'; else sed -n '/^{Begin}$/,/^{End}$/p' /etc/ssh/sshd_config 2>/dev/null; fi\n" +
        "echo '#end'";

    public static SshdSnapshot Parse(string output)
    {
        var lines = output.Replace("\r", "").Split('\n');
        string user = "", section = ""; bool root = false, effective = false, include = false; string? problem = null;
        var settings = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase); var ours = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool inMatch = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("#user ", StringComparison.Ordinal)) { var p = line.Split(' '); user = p.Length > 1 ? p[1] : ""; root = p.Length > 2 && p[2] == "0"; continue; }
            if (line == "#effective") { section = "settings"; effective = true; continue; }
            if (line.StartsWith("#noeffective", StringComparison.Ordinal)) { problem = line.Length > 13 ? line[13..] : ""; continue; }
            if (line == "#files") { section = "settings"; inMatch = false; continue; }
            if (line == "#include") { section = "include"; continue; }
            if (line == "#ours") { section = "ours"; continue; }
            if (line == "#end") break;
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split((char[])[' ', '\t', '='], 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (section == "include") { include = line == "yes"; continue; }
            if (parts.Length < 2) continue;
            var key = parts[0].ToLowerInvariant(); var value = parts[1].Trim();
            if (section == "ours") { if (Allowed.ContainsKey(key)) ours[Canonical(key)] = value; continue; }
            if (section != "settings") continue;
            // Read from files: a Match block applies to someone else; the first value outside it wins, like sshd.
            if (!effective) { if (key == "match") { inMatch = true; continue; } if (inMatch) continue; }
            if (!settings.TryGetValue(key, out var list)) settings[key] = list = [];
            if (effective || key is "port" or "listenaddress" or "allowusers" or "allowgroups" or "denyusers" or "denygroups" || list.Count == 0) list.Add(key is "allowusers" or "allowgroups" or "denyusers" or "denygroups" ? value : value.ToLowerInvariant());
        }
        var failure = settings.Count == 0 ? "Не удалось прочитать настройки SSH-сервера" + (problem is { Length: > 0 } ? ": " + problem : ".") : null;
        return new SshdSnapshot(failure, settings.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value, StringComparer.OrdinalIgnoreCase), effective, include, ours, user, root);
    }

    static string Canonical(string key) => Allowed.Keys.First(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));

    static int Int(string value, int fallback) => int.TryParse(value.TrimEnd('s'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;

    /// <summary>
    /// What to look at, most important first. A fix that would end in lock-out for this profile (disabling passwords
    /// while it logs in by password, or root login for a root password login) is offered but blocked with the reason.
    /// </summary>
    public static IReadOnlyList<SshdFinding> Audit(SshdSnapshot s, AuthenticationKind login, int authorizedKeys)
    {
        var list = new List<SshdFinding>();
        bool passwordLogin = login is AuthenticationKind.Password or AuthenticationKind.KeyboardInteractive;
        string? keyFirst = passwordLogin ? "Этот профиль входит по паролю: сначала перейдите на вход по ключу («Вход по ключу…»), иначе после изменения войти не получится." : authorizedKeys == 0 ? "На сервере нет ни одного ключа для этого пользователя: сначала добавьте ключ." : null;
        // Defaults when a value is absent (reading files without sshd -T): OpenSSH's own defaults.
        string Value(string key, string fallback) { var v = s.Get(key); return v.Length > 0 ? v : fallback; }
        var password = Value("passwordauthentication", "yes"); var kbd = s.Get("kbdinteractiveauthentication") is { Length: > 0 } k ? k : Value("challengeresponseauthentication", "yes");
        var pam = Value("usepam", "no");
        if (password == "yes")
            list.Add(new("password", Severity.High, "Вход по паролю разрешён", "Пароли подбирают автоматически, круглосуточно. С ключами вход надёжнее; после отключения паролей войти можно только по ключу.", new Dictionary<string, string> { ["PasswordAuthentication"] = "no", ["KbdInteractiveAuthentication"] = "no" }, keyFirst));
        else if (kbd == "yes" && pam == "yes")
            list.Add(new("kbd", Severity.Medium, "Пароль всё ещё возможен через keyboard-interactive (PAM)", "PasswordAuthentication выключен, но PAM может спросить пароль другим способом.", new Dictionary<string, string> { ["KbdInteractiveAuthentication"] = "no" }, keyFirst));
        var rootLogin = Value("permitrootlogin", "prohibit-password");
        if (rootLogin == "yes")
        {
            bool selfRoot = s.IsRoot || s.User == "root";
            var target = selfRoot ? "prohibit-password" : "no";
            list.Add(new("root", Severity.High, "Root может входить по паролю", selfRoot ? "Вы подключены как root: будет оставлен вход root только по ключу." : "Входите обычным пользователем и повышайте права через sudo. Root по SSH будет запрещён.",
                new Dictionary<string, string> { ["PermitRootLogin"] = target }, selfRoot && passwordLogin ? "Вы входите как root по паролю: сначала перейдите на вход по ключу." : null));
        }
        if (Value("permitemptypasswords", "no") == "yes")
            list.Add(new("empty", Severity.High, "Разрешены пустые пароли", "Учётная запись без пароля пускает кого угодно.", new Dictionary<string, string> { ["PermitEmptyPasswords"] = "no" }, null));
        if (Value("pubkeyauthentication", "yes") == "no")
            list.Add(new("pubkey", Severity.Medium, "Вход по ключу выключен", "Ключи надёжнее паролей; включение ничего не ломает.", new Dictionary<string, string> { ["PubkeyAuthentication"] = "yes" }, null));
        if (Int(Value("maxauthtries", "6"), 6) > 4)
            list.Add(new("tries", Severity.Low, $"Попыток входа за подключение: {Value("maxauthtries", "6")}", "Меньше попыток — медленнее перебор. 4 хватает и при нескольких ключах в агенте.", new Dictionary<string, string> { ["MaxAuthTries"] = "4" }, null));
        if (Int(Value("logingracetime", "120"), 120) > 60)
            list.Add(new("grace", Severity.Low, $"На вход отводится {Value("logingracetime", "120")} с", "Незавершённые входы занимают слоты сервера; 30 секунд достаточно.", new Dictionary<string, string> { ["LoginGraceTime"] = "30" }, null));
        if (Value("x11forwarding", "no") == "yes")
            list.Add(new("x11", Severity.Low, "Включена пересылка X11", "Нужна только для графических программ по SSH; иначе её лучше выключить.", new Dictionary<string, string> { ["X11Forwarding"] = "no" }, null));
        var ports = s.All("port"); if (ports.Count == 0) ports = ["22"];
        if (ports.All(p => p == "22"))
            list.Add(new("port", Severity.Info, "SSH на стандартном порту 22", "Смена порта не защищает, но убирает большую часть автоматических попыток. Надёжнее — вход только по ключу и «Файервол» → «Доступ только через сервер». Порт меняется вручную вместе с правилом файервола и профилем.", null, null));
        if (s.All("allowusers").Count == 0 && s.All("allowgroups").Count == 0)
            list.Add(new("allow", Severity.Info, "Входить может любой пользователь сервера", "Список AllowUsers ограничивает вход нужными учётными записями. Его меняют вручную: ошибка в нём отрезает доступ.", null, null));
        return list;
    }

    public static string? ValidateSetting(string key, string value) =>
        !Allowed.TryGetValue(key, out var pattern) ? $"Параметр {key} приложение не меняет." : !pattern.IsMatch(value) ? $"Недопустимое значение «{value}» для {key}." : null;

    /// <summary>The text of our block: earlier values of ours kept, the new ones on top.</summary>
    public static string Block(IReadOnlyDictionary<string, string> ours, IReadOnlyDictionary<string, string> changes)
    {
        var merged = new Dictionary<string, string>(ours, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in changes) { if (ValidateSetting(k, v) is { } e) throw new ArgumentException(e); merged[Canonical(k)] = v; }
        var sb = new StringBuilder("# Written by SSH Explorer. These values are read first and win over the rest of sshd_config.\n");
        foreach (var (k, v) in merged.OrderBy(p => p.Key, StringComparer.Ordinal)) sb.Append(k).Append(' ').Append(v).Append('\n');
        return sb.ToString();
    }

    public static string Directory(string id) => "/var/tmp/sshx-sshd-" + Check(id);

    /// <summary>Saves the files, schedules the restore, writes the block, checks it with <c>sshd -t</c> and reloads sshd.</summary>
    public static string SafeApply(string id, string block, bool dropIn, int seconds)
    {
        if (block.Contains("SSHX_EOF", StringComparison.Ordinal) || block.Contains('`') || block.Contains('$')) throw new ArgumentException("Недопустимый текст настроек.");
        var d = Directory(id); var unit = "sshx-sshd-rollback-" + id; var sec = seconds.ToString(CultureInfo.InvariantCulture);
        // A running daemon is reloaded (open sessions stay); a socket-activated one reads the file on the next connection.
        const string reload = "{ r=1; for u in ssh sshd; do if systemctl is-active --quiet \"$u.service\" 2>/dev/null; then systemctl reload \"$u.service\" >/dev/null 2>&1 && r=0; fi; done; " +
            "[ $r -eq 0 ] || { [ -f /run/sshd.pid ] && kill -HUP \"$(cat /run/sshd.pid)\" 2>/dev/null && r=0; }; " +
            "[ $r -eq 0 ] || { command -v service >/dev/null 2>&1 && { service ssh reload || service sshd reload; } >/dev/null 2>&1 && r=0; }; " +
            "[ $r -eq 0 ] || { systemctl is-active --quiet ssh.socket 2>/dev/null && r=0; }; [ $r -eq 0 ]; }";
        var sb = new StringBuilder();
        sb.Append("PATH=$PATH:/usr/sbin:/sbin; export PATH; LC_ALL=C; export LC_ALL; umask 022\n");
        sb.Append($"B='{d}'\nmkdir -m 700 \"$B\" || exit 90\n");
        sb.Append("cp -p /etc/ssh/sshd_config \"$B/sshd_config\" || exit 90\n");
        sb.Append($"if [ -f '{DropIn}' ]; then cp -p '{DropIn}' \"$B/dropin\"; fi\n");
        sb.Append("cat > \"$B/rollback.sh\" <<'SSHX_EOF'\nPATH=$PATH:/usr/sbin:/sbin; export PATH\n");
        sb.Append($"B='{d}'\n[ -d \"$B\" ] || exit 0\ncp -p \"$B/sshd_config\" /etc/ssh/sshd_config\n");
        sb.Append($"if [ -f \"$B/dropin\" ]; then cp -p \"$B/dropin\" '{DropIn}'; else rm -f '{DropIn}'; fi\n");
        sb.Append(reload).Append("\nrm -rf \"$B\"\nSSHX_EOF\n");
        sb.Append($"if command -v systemd-run >/dev/null 2>&1 && [ -d /run/systemd/system ] && systemd-run --quiet --unit='{unit}' --on-active={sec} /bin/sh \"$B/rollback.sh\" >/dev/null 2>&1; then echo '#timer systemd'; \n");
        sb.Append($"else (setsid nohup sh -c 'sleep {sec}; sh \"$0\"' \"$B/rollback.sh\" >/dev/null 2>&1 &) ; echo '#timer sleep'; fi\n");
        if (dropIn)
            sb.Append($"mkdir -p /etc/ssh/sshd_config.d && cat > '{DropIn}' <<'SSHX_EOF'\n{block}SSHX_EOF\nchmod 644 '{DropIn}'\n");
        else
            sb.Append($"{{ printf '%s\\n' '{Begin}'; cat <<'SSHX_EOF'\n{block}SSHX_EOF\nprintf '%s\\n' '{End}'; sed '/^{Begin}$/,/^{End}$/d' \"$B/sshd_config\"; }} > /etc/ssh/sshd_config.sshx && cat /etc/ssh/sshd_config.sshx > /etc/ssh/sshd_config && rm -f /etc/ssh/sshd_config.sshx\n");
        sb.Append("sshd -t 2>&1 || { echo '#invalid'; sh \"$B/rollback.sh\"; exit 92; }\n");
        sb.Append(reload).Append(" || { echo '#reload-failed'; sh \"$B/rollback.sh\"; exit 93; }\n");
        sb.Append("echo '#applied'\n");
        return sb.ToString();
    }

    public static string Confirm(string id) => $"systemctl stop 'sshx-sshd-rollback-{Check(id)}.timer' >/dev/null 2>&1; rm -rf '{Directory(id)}'; echo '#confirmed'";
    public static string RollbackNow(string id) => $"PATH=$PATH:/usr/sbin:/sbin; systemctl stop 'sshx-sshd-rollback-{Check(id)}.timer' >/dev/null 2>&1; [ -f '{Directory(id)}/rollback.sh' ] && sh '{Directory(id)}/rollback.sh'; echo '#rolledback'";

    static string Check(string id) => id.Length is > 0 and <= 32 && id.All(char.IsAsciiLetterOrDigit) ? id : throw new ArgumentException("Недопустимый идентификатор.", nameof(id));

    [GeneratedRegex("^(yes|no)$")] private static partial Regex YesNo();
    [GeneratedRegex("^(yes|no|prohibit-password|without-password|forced-commands-only)$")] private static partial Regex RootValue();
    [GeneratedRegex("^[0-9]{1,4}$")] private static partial Regex Number();
}
