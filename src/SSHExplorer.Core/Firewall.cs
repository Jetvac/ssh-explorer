using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace SSHExplorer.Core;

public enum FirewallAction { Allow, Deny, Reject, Limit }
public enum FirewallDirection { In, Out }

/// <summary>
/// One ufw user rule as ufw itself prints it in <c>ufw show added</c> (the spec is kept verbatim: it is what
/// <c>ufw delete</c> takes). Parsed fields serve display and the access check.
/// </summary>
public sealed record UfwRule(string Spec, bool Route, FirewallAction Action, FirewallDirection Direction, string? Interface,
    string From, string? FromPort, string To, string? ToPort, string? Proto, string? App, string? Comment)
{
    public string ActionText => Action switch { FirewallAction.Allow => "Разрешить", FirewallAction.Deny => "Запретить", FirewallAction.Reject => "Отклонить", _ => "Ограничить частоту" };
    public string DirectionText => (Route ? "Пересылка, " : "") + (Direction == FirewallDirection.In ? "входящие" : "исходящие") + (Interface is null ? "" : " · " + Interface);
    public string FromText => From == "any" ? "Любой адрес" + (FromPort is null ? "" : " · порт " + FromPort) : From + (FromPort is null ? "" : " · порт " + FromPort);
    public string ToText => (App is not null ? "Приложение «" + App + "»" : ToPort is null ? "Любой порт" : "Порт " + ToPort) + (To == "any" ? "" : " на " + To);
    public string ProtoText => Proto ?? (App is not null ? "по профилю" : "tcp и udp");
    public string SearchText => $"{Spec} {ActionText} {DirectionText} {FromText} {ToText} {Comment}";
}

/// <summary>What ufw reports about itself plus what the session sees: the address and port this SSH connection uses.</summary>
public sealed record FirewallSnapshot(bool Installed, bool Active, string DefaultIncoming, string DefaultOutgoing, string DefaultRouted, string Logging,
    IReadOnlyList<UfwRule> Rules, string? ClientAddress, int? SshPort, bool FirewalldActive, bool DockerPresent, string? Problem);

/// <summary>Whether new SSH connections from an address would get through, and which rule or policy decides.</summary>
public sealed record AccessVerdict(bool? Allowed, string Reason);

/// <summary>A rule being written in the editor.</summary>
public sealed record RuleDraft
{
    public FirewallAction Action { get; init; } = FirewallAction.Allow;
    public FirewallDirection Direction { get; init; } = FirewallDirection.In;
    /// <summary>«any», an address or a subnet (CIDR).</summary>
    public string From { get; init; } = "any";
    /// <summary>Empty — every port; «22», «80,443» or «6000:6007».</summary>
    public string Port { get; init; } = "";
    /// <summary>«any», «tcp» or «udp».</summary>
    public string Proto { get; init; } = "tcp";
    public string Interface { get; init; } = "";
    public string Comment { get; init; } = "";
    /// <summary>Put the rule first (it is checked before the others) instead of last.</summary>
    public bool Prepend { get; init; }
}

public static partial class Ufw
{
    /// <summary>
    /// One read: whether ufw is there and active, defaults, the user rules (also when inactive), this connection's
    /// address and port as the server sees them (<c>SSH_CLIENT</c>), and neighbours that change the picture.
    /// Runs as root (<paramref name="prefix"/> is «» or «sudo -n »).
    /// </summary>
    public static string ReadCommand(string prefix) =>
        "PATH=$PATH:/usr/sbin:/sbin; export PATH; LC_ALL=C; export LC_ALL; " +
        "echo \"#client $SSH_CLIENT\"; " +
        "if ! command -v ufw >/dev/null 2>&1; then echo '#noufw'; exit 0; fi; " +
        $"echo '#status'; {prefix}ufw status verbose 2>&1; " +
        $"echo '#defaults'; {prefix}cat /etc/default/ufw 2>/dev/null | grep '^DEFAULT_' ; " +
        $"echo '#added'; {prefix}ufw show added 2>&1; " +
        "echo '#neighbours'; (systemctl is-active firewalld 2>/dev/null | grep -qx active && echo firewalld) ; (command -v docker >/dev/null 2>&1 && echo docker); true";

    /// <summary>Finds how to run ufw: root, or sudo without a password. A password is never asked for.</summary>
    public const string RightsCommand = "if [ \"$(id -u)\" = 0 ]; then echo root; elif command -v sudo >/dev/null 2>&1 && sudo -n true 2>/dev/null; then echo sudo; else echo none; fi";

    public static FirewallSnapshot Parse(string output)
    {
        string section = ""; string? client = null; int? sshPort = null; bool installed = true, active = false, firewalld = false, docker = false;
        string incoming = "", outgoing = "", routed = "", logging = "", problem = "";
        var defaults = new Dictionary<string, string>(StringComparer.Ordinal); var rules = new List<UfwRule>();
        foreach (var raw in output.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("#client", StringComparison.Ordinal))
            {
                var parts = line[7..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3) { client = parts[0]; if (int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var p)) sshPort = p; }
                continue;
            }
            if (line == "#noufw") { installed = false; continue; }
            if (line is "#status" or "#defaults" or "#added" or "#neighbours") { section = line; continue; }
            if (line.Length == 0) continue;
            switch (section)
            {
                case "#status":
                    if (line.StartsWith("Status:", StringComparison.Ordinal)) active = line.Contains("active", StringComparison.Ordinal) && !line.Contains("inactive", StringComparison.Ordinal);
                    else if (line.StartsWith("Logging:", StringComparison.Ordinal)) logging = line[8..].Trim();
                    else if (line.StartsWith("Default:", StringComparison.Ordinal))
                        foreach (var part in line[8..].Split(',', StringSplitOptions.TrimEntries))
                        {
                            var m = DefaultRegex().Match(part); if (!m.Success) continue;
                            if (m.Groups[2].Value == "incoming") incoming = m.Groups[1].Value; else if (m.Groups[2].Value == "outgoing") outgoing = m.Groups[1].Value; else routed = m.Groups[1].Value;
                        }
                    else if (line.StartsWith("ERROR", StringComparison.Ordinal) || line.Contains("must be root", StringComparison.OrdinalIgnoreCase)) problem = line;
                    break;
                case "#defaults":
                    int eq = line.IndexOf('='); if (eq > 0) defaults[line[..eq]] = line[(eq + 1)..].Trim('"');
                    break;
                case "#added":
                    if (line.StartsWith("ufw ", StringComparison.Ordinal) && ParseSpec(line[4..]) is { } rule) rules.Add(rule);
                    else if (line.StartsWith("ERROR", StringComparison.Ordinal)) problem = line;
                    break;
                case "#neighbours":
                    if (line == "firewalld") firewalld = true; else if (line == "docker") docker = true;
                    break;
            }
        }
        // Inactive ufw prints no defaults: they come from /etc/default/ufw (DROP/REJECT/ACCEPT).
        static string Policy(string value) => value switch { "DROP" => "deny", "REJECT" => "reject", "ACCEPT" => "allow", _ => value.ToLowerInvariant() };
        if (incoming.Length == 0 && defaults.TryGetValue("DEFAULT_INPUT_POLICY", out var i)) incoming = Policy(i);
        if (outgoing.Length == 0 && defaults.TryGetValue("DEFAULT_OUTPUT_POLICY", out var o)) outgoing = Policy(o);
        if (routed.Length == 0 && defaults.TryGetValue("DEFAULT_FORWARD_POLICY", out var f)) routed = Policy(f) is "deny" ? "disabled" : Policy(f);
        return new FirewallSnapshot(installed, active, incoming, outgoing, routed, logging, rules, client, sshPort, firewalld, docker,
            !installed ? "На сервере нет ufw. Установите пакет ufw (например, apt install ufw) и обновите страницу." : problem.Length > 0 ? problem : null);
    }

    /// <summary>Splits a spec into words; single-quoted parts (comments, app names with spaces) stay whole.</summary>
    static List<string> Words(string spec)
    {
        var words = new List<string>(); var current = new StringBuilder(); bool quoted = false, any = false;
        foreach (var c in spec)
        {
            if (c == '\'') { quoted = !quoted; any = true; continue; }
            if (c == ' ' && !quoted) { if (any) { words.Add(current.ToString()); current.Clear(); any = false; } continue; }
            current.Append(c); any = true;
        }
        if (any) words.Add(current.ToString());
        return words;
    }

    /// <summary>Parses «allow from 10.0.0.0/8 to any port 22 proto tcp comment 'x'», «limit 22/tcp», «deny in on eth0 from 1.2.3.4», «allow OpenSSH».</summary>
    public static UfwRule? ParseSpec(string spec)
    {
        var w = Words(spec); int k = 0;
        bool route = false; if (k < w.Count && w[k] == "route") { route = true; k++; }
        if (k >= w.Count) return null;
        FirewallAction? action = w[k] switch { "allow" => FirewallAction.Allow, "deny" => FirewallAction.Deny, "reject" => FirewallAction.Reject, "limit" => FirewallAction.Limit, _ => null };
        if (action is null) return null; k++;
        var direction = FirewallDirection.In; string? iface = null;
        while (k < w.Count && w[k] is "in" or "out")
        {
            var d = w[k] == "in" ? FirewallDirection.In : FirewallDirection.Out; k++;
            if (k + 1 < w.Count && w[k] == "on") { iface ??= w[k + 1]; k += 2; }
            if (!route || iface is null) direction = d;
        }
        if (k < w.Count && w[k] is "log" or "log-all") k++;
        string from = "any", to = "any"; string? fromPort = null, toPort = null, proto = null, app = null, comment = null;
        bool full = k < w.Count && w[k] is "from" or "to" or "proto";
        if (!full && k < w.Count && w[k] != "comment")
        {
            var target = w[k++]; int slash = target.IndexOf('/');
            if (char.IsDigit(target[0])) { toPort = slash > 0 ? target[..slash] : target; proto = slash > 0 ? target[(slash + 1)..] : null; }
            else app = target;
        }
        while (k < w.Count)
        {
            var word = w[k++]; var next = k < w.Count ? w[k] : null;
            switch (word)
            {
                case "from" when next is not null: from = next; k++; if (k + 1 < w.Count && w[k] == "port") { fromPort = w[k + 1]; k += 2; } else if (k + 1 < w.Count && w[k] == "app") { k += 2; } break;
                case "to" when next is not null: to = next; k++; if (k + 1 < w.Count && w[k] == "port") { toPort = w[k + 1]; k += 2; } else if (k + 1 < w.Count && w[k] == "app") { app = w[k + 1]; k += 2; } break;
                case "proto" when next is not null: proto = next; k++; break;
                case "comment" when next is not null: comment = next; k++; break;
            }
        }
        return new UfwRule(spec, route, action.Value, direction, iface, from, fromPort, to, toPort, proto, app, comment);
    }

    // ── Building ────────────────────────────────────────────────────────────────

    public static bool IsAddress(string value) => value == "any" || IPAddress.TryParse(value, out _) || IPNetwork.TryParse(value, out _);

    static bool IsPortList(string value) => PortRegex().IsMatch(value) && value.Split(',').All(p => p.Split(':').All(n => int.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out var x) && x is >= 1 and <= 65535));

    /// <summary>Null when ufw will accept the rule; otherwise the reason in Russian.</summary>
    public static string? Validate(RuleDraft draft)
    {
        var from = draft.From.Trim();
        if (!IsAddress(from)) return "«Откуда»: укажите «любой», IP-адрес (1.2.3.4, 2001:db8::1) или подсеть (10.0.0.0/8).";
        var port = draft.Port.Trim();
        if (port.Length > 0 && !IsPortList(port)) return "Порт: число 1–65535, список через запятую (80,443) или диапазон через двоеточие (6000:6007).";
        if ((port.Contains(',') || port.Contains(':')) && draft.Proto == "any") return "Для списка или диапазона портов выберите протокол tcp или udp — так требует ufw.";
        if (port.Length > 0 && port.Split(',').Length > 15) return "ufw принимает не больше 15 портов в одном правиле.";
        if (draft.Proto is not ("any" or "tcp" or "udp")) return "Протокол: tcp, udp или любой.";
        if (draft.Interface.Length > 0 && !InterfaceRegex().IsMatch(draft.Interface)) return "Интерфейс: имя вроде eth0 или ens3.";
        if (draft.Comment.IndexOfAny(['\n', '\r', '\'', ';', '&', '|', '`', '$']) >= 0 || draft.Comment.Length > 200) return "Комментарий: одна строка до 200 символов, без апострофа и символов ; & | ` $.";
        if (draft.Action == FirewallAction.Limit && draft.Direction == FirewallDirection.Out) return "«Ограничить частоту» применяется к входящим подключениям.";
        if (from == "any" && port.Length == 0 && draft.Action == FirewallAction.Allow && draft.Direction == FirewallDirection.In) return "Правило «разрешить всё отовсюду» отключает защиту. Укажите порт или адрес.";
        return null;
    }

    static string ActionWord(FirewallAction action) => action switch { FirewallAction.Allow => "allow", FirewallAction.Deny => "deny", FirewallAction.Reject => "reject", _ => "limit" };

    /// <summary>The rule in ufw's full syntax (what follows «ufw»), every user value quoted.</summary>
    public static string Spec(RuleDraft draft)
    {
        if (Validate(draft) is { } problem) throw new ArgumentException(problem);
        var sb = new StringBuilder(ActionWord(draft.Action));
        sb.Append(draft.Direction == FirewallDirection.In ? " in" : " out");
        if (draft.Interface.Length > 0) sb.Append(" on ").Append(PosixPath.Quote(draft.Interface));
        if (draft.Proto != "any") sb.Append(" proto ").Append(draft.Proto);
        sb.Append(" from ").Append(PosixPath.Quote(draft.From.Trim())).Append(" to any");
        if (draft.Port.Trim().Length > 0) sb.Append(" port ").Append(PosixPath.Quote(draft.Port.Trim()));
        if (draft.Comment.Trim().Length > 0) sb.Append(" comment ").Append(PosixPath.Quote(draft.Comment.Trim()));
        return sb.ToString();
    }

    /// <summary>The add command; first position uses «ufw prepend», which ufw refuses on an empty rule list.</summary>
    public static string AddCommand(RuleDraft draft, int existingRules) => "ufw " + (draft.Prepend && existingRules > 0 ? "prepend " : "") + Spec(draft);

    /// <summary>Deletes exactly this rule (both IPv4 and IPv6 variants) by its spec; the comment is not part of matching.</summary>
    public static string DeleteCommand(UfwRule rule)
    {
        var spec = rule.Spec; int c = spec.IndexOf(" comment ", StringComparison.Ordinal); if (c > 0) spec = spec[..c];
        // The spec came from ufw itself; it is re-quoted word by word so nothing in it is shell syntax.
        return "ufw --force delete " + string.Join(" ", Words(spec).Select(PosixPath.Quote));
    }

    public static string DefaultCommand(string direction, string policy) =>
        direction is "incoming" or "outgoing" && policy is "allow" or "deny" or "reject" ? $"ufw default {policy} {direction}" : throw new ArgumentException("Недопустимая политика.");

    public const string EnableCommand = "ufw --force enable";
    public const string DisableCommand = "ufw disable";

    // ── Access check ────────────────────────────────────────────────────────────

    static bool AddressMatches(string rule, string? address)
    {
        if (rule == "any") return true;
        if (address is null || !IPAddress.TryParse(address, out var ip)) return false;
        if (IPAddress.TryParse(rule, out var exact)) return exact.Equals(ip);
        return IPNetwork.TryParse(rule, out var net) && net.BaseAddress.AddressFamily == ip.AddressFamily && net.Contains(ip);
    }

    static bool? PortMatches(UfwRule rule, int port)
    {
        if (rule.App is not null) return rule.App.Equals("OpenSSH", StringComparison.OrdinalIgnoreCase) ? port == 22 : null;
        if (rule.ToPort is null) return true;
        foreach (var part in rule.ToPort.Split(','))
        {
            var range = part.Split(':');
            if (range.Length == 1 && int.TryParse(range[0], out var single) && single == port) return true;
            if (range.Length == 2 && int.TryParse(range[0], out var lo) && int.TryParse(range[1], out var hi) && port >= lo && port <= hi) return true;
            if (!char.IsDigit(part[0])) return null; // a service name such as «ssh»
        }
        return false;
    }

    /// <summary>
    /// Would a new TCP connection from <paramref name="client"/> to <paramref name="port"/> pass? ufw checks user rules in
    /// order and the first match decides; otherwise the default incoming policy. Rules bound to an interface or a
    /// destination address cannot be judged from here and make the verdict «unknown» when they could match.
    /// Existing connections are not cut by ufw (it keeps established ones); this is about the next connection.
    /// </summary>
    public static AccessVerdict CheckAccess(IReadOnlyList<UfwRule> rules, bool active, string defaultIncoming, string? client, int port)
    {
        if (!active) return new AccessVerdict(true, "ufw выключен — входящие подключения не фильтруются им.");
        if (client is null) return new AccessVerdict(null, "Адрес этого подключения неизвестен (сервер не передал SSH_CLIENT).");
        foreach (var rule in rules)
        {
            if (rule.Route || rule.Direction != FirewallDirection.In) continue;
            if (rule.Proto is "udp") continue;
            if (!AddressMatches(rule.From, client)) continue;
            if (rule.FromPort is not null) continue; // source ports of an SSH client are random
            var portMatch = PortMatches(rule, port);
            if (portMatch == false) continue;
            if (portMatch is null || rule.Interface is not null || rule.To != "any")
                return new AccessVerdict(null, $"Решение зависит от правила «{rule.Spec}»: по нему нельзя точно сказать, пропустит ли оно подключение.");
            return rule.Action is FirewallAction.Allow or FirewallAction.Limit
                ? new AccessVerdict(true, $"Разрешено правилом «{rule.Spec}»" + (rule.Action == FirewallAction.Limit ? " (не больше 6 подключений за 30 секунд)." : "."))
                : new AccessVerdict(false, $"Будет заблокировано правилом «{rule.Spec}».");
        }
        return defaultIncoming == "allow" ? new AccessVerdict(true, "Ни одно правило не подходит; политика по умолчанию разрешает входящие.")
            : new AccessVerdict(false, $"Ни одно правило не разрешает порт {port} с адреса {client}, а политика по умолчанию — «{defaultIncoming}».");
    }

    // ── «SSH only through a jump server» ────────────────────────────────────────

    /// <summary>Rules that let SSH in from somewhere other than the allowed addresses: they must go, or they would win over the closing rule.</summary>
    public static IReadOnlyList<UfwRule> OpenSshRules(IReadOnlyList<UfwRule> rules, int sshPort, IReadOnlyCollection<string> allowed) =>
        rules.Where(r => !r.Route && r.Direction == FirewallDirection.In && r.Action is FirewallAction.Allow or FirewallAction.Limit && r.Proto != "udp"
            && PortMatches(r, sshPort) == true && !allowed.Any(a => SameAddress(r.From, a))).ToArray();

    static bool SameAddress(string rule, string address) => rule == address || IPAddress.TryParse(rule, out var a) && IPAddress.TryParse(address, out var b) && a.Equals(b);

    /// <summary>
    /// Commands that leave SSH open only from <paramref name="allowed"/> (the jump server as this server sees it, plus any
    /// extra addresses): allow rules for them go first, rules that open SSH to others are deleted (only those the user
    /// kept ticked in <paramref name="remove"/>), and a closing «deny» for the SSH port goes last — it works whatever
    /// the default policy is, and touches no other port. If ufw is off, <paramref name="keepPorts"/> (listening services)
    /// are allowed before turning it on, so enabling the firewall does not cut websites and other services.
    /// </summary>
    public static IReadOnlyList<string> BastionCommands(FirewallSnapshot snapshot, IReadOnlyList<string> allowed, int sshPort, IReadOnlyList<UfwRule> remove,
        IReadOnlyList<(int Port, string Proto)> keepPorts, string bastionName)
    {
        if (allowed.Count == 0) throw new ArgumentException("Нужен хотя бы один разрешённый адрес.");
        foreach (var a in allowed) if (a == "any" || !IsAddress(a)) throw new ArgumentException("Недопустимый адрес: " + a);
        var label = Regex.Replace(bastionName, "[^\\p{L}\\p{Nd} ._-]", "").Trim(); if (label.Length > 60) label = label[..60];
        var commands = new List<string>();
        int count = snapshot.Rules.Count;
        foreach (var a in allowed)
        {
            if (snapshot.Rules.Any(r => r.Action == FirewallAction.Allow && SameAddress(r.From, a) && PortMatches(r, sshPort) == true && r.Direction == FirewallDirection.In && !r.Route && r.Proto != "udp")) continue;
            commands.Add(AddCommand(new RuleDraft { From = a, Port = sshPort.ToString(CultureInfo.InvariantCulture), Proto = "tcp", Comment = "SSH Explorer: SSH через " + (label.Length > 0 ? label : "промежуточный сервер"), Prepend = true }, count));
            count++;
        }
        foreach (var r in remove) commands.Add(DeleteCommand(r));
        if (!snapshot.Rules.Any(r => r.Action is FirewallAction.Deny or FirewallAction.Reject && r.From == "any" && r.Direction == FirewallDirection.In && !r.Route && PortMatches(r, sshPort) == true))
            commands.Add(AddCommand(new RuleDraft { Action = FirewallAction.Deny, Port = sshPort.ToString(CultureInfo.InvariantCulture), Proto = "tcp", Comment = "SSH Explorer: прямой SSH закрыт" }, count));
        if (!snapshot.Active)
        {
            foreach (var (port, proto) in keepPorts.Distinct())
                if (port != sshPort) commands.Add(AddCommand(new RuleDraft { Port = port.ToString(CultureInfo.InvariantCulture), Proto = proto, Comment = "SSH Explorer: был открыт до включения ufw" }, count++));
            commands.Add(EnableCommand);
        }
        return commands;
    }

    /// <summary>What the server's rules look like after <see cref="BastionCommands"/>, for the access check before applying.</summary>
    public static IReadOnlyList<UfwRule> Simulate(IReadOnlyList<UfwRule> rules, IReadOnlyList<string> commands)
    {
        var result = rules.ToList();
        foreach (var c in commands)
        {
            if (c.StartsWith("ufw --force delete ", StringComparison.Ordinal))
            {
                result.RemoveAll(r => Words(StripComment(r.Spec)).SequenceEqual(Words(c["ufw --force delete ".Length..])));
            }
            else if (c.StartsWith("ufw prepend ", StringComparison.Ordinal) && ParseSpec(Unquote(c["ufw prepend ".Length..])) is { } first) result.Insert(0, first);
            else if (c.StartsWith("ufw ", StringComparison.Ordinal) && !c.StartsWith("ufw --force", StringComparison.Ordinal) && !c.StartsWith("ufw default", StringComparison.Ordinal) && ParseSpec(Unquote(c[4..])) is { } last) result.Add(last);
        }
        return result;
    }

    /// <summary>The state after <paramref name="commands"/>: rules, on/off and the default incoming policy — for the access check before applying.</summary>
    public static (IReadOnlyList<UfwRule> Rules, bool Active, string DefaultIncoming) Predict(FirewallSnapshot snapshot, IReadOnlyList<string> commands)
    {
        bool active = snapshot.Active; var incoming = snapshot.DefaultIncoming; var rules = snapshot.Rules;
        foreach (var c in commands)
        {
            if (c == EnableCommand) active = true;
            else if (c == DisableCommand) active = false;
            else if (c.StartsWith("ufw default ", StringComparison.Ordinal) && c.EndsWith(" incoming", StringComparison.Ordinal)) incoming = c.Split(' ')[2];
            else rules = Simulate(rules, [c]);
        }
        return (rules, active, incoming);
    }

    /// <summary>The verdict for this session's own address after the commands.</summary>
    public static AccessVerdict CheckAfter(FirewallSnapshot snapshot, IReadOnlyList<string> commands, string? client = null)
    {
        var (rules, active, incoming) = Predict(snapshot, commands);
        return CheckAccess(rules, active, incoming, client ?? snapshot.ClientAddress, snapshot.SshPort ?? 22);
    }

    static string StripComment(string spec) { int c = spec.IndexOf(" comment ", StringComparison.Ordinal); return c > 0 ? spec[..c] : spec; }
    static string Unquote(string text) => string.Join(" ", Words(text).Select(w => w.Contains(' ') ? "'" + w + "'" : w));

    [GeneratedRegex("^(allow|deny|reject|disabled) \\((incoming|outgoing|routed)\\)$")] private static partial Regex DefaultRegex();
    [GeneratedRegex("^[0-9]+([:,][0-9]+)*$")] private static partial Regex PortRegex();
    [GeneratedRegex("^[A-Za-z0-9_.:+-]{1,15}$")] private static partial Regex InterfaceRegex();
}

/// <summary>A named list of firewall rules saved on this computer and applied to any server with the safe apply.</summary>
public sealed class FirewallRuleSet
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<RuleDraft> Rules { get; set; } = [];
    /// <summary>Optional default policy for incoming traffic («allow», «deny», «reject»); null — unchanged.</summary>
    public string? DefaultIncoming { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public FirewallRuleSet Clone() => new() { Id = Id, Name = Name, Description = Description, Rules = [.. Rules], DefaultIncoming = DefaultIncoming, UpdatedUtc = UpdatedUtc };
}
