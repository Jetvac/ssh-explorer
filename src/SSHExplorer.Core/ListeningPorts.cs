using System.Globalization;
using System.Text.RegularExpressions;

namespace SSHExplorer.Core;

/// <summary>One listening socket and who holds it (null when the server does not tell this account).</summary>
public sealed record ListeningPort(string Protocol, string State, string Address, int Port, int? Pid, string? Process, string? User, string? Interface)
{
    public bool AllInterfaces => Address is "0.0.0.0" or "::" or "*";
    public string Endpoint => (Address.Contains(':') ? $"[{Address}]" : Address) + ":" + Port.ToString(CultureInfo.InvariantCulture);
    /// <summary>Text the search box matches: protocol, address, port, process, pid and user.</summary>
    public string SearchText => $"{Protocol} {State} {Endpoint} {Port} {Process} {Pid} {User} {Interface}";
}

public sealed record PortsSnapshot(IReadOnlyList<ListeningPort> Ports, string Tool, bool IsRoot, bool ProcessesHidden, string? Error);

/// <summary>
/// Read-only listing of listening TCP/UDP sockets via <c>ss -H -tulpn</c> (or <c>netstat -tulpn</c>). Process names come
/// from the socket tool; owners from <c>ps</c>. Without root, sockets of other users have no process — reported as hidden.
/// </summary>
public static partial class ListeningPorts
{
    public const string Command =
        "LC_ALL=C; export LC_ALL; " +
        "if command -v ss >/dev/null 2>&1; then echo '#tool ss'; ss -H -tulpn 2>/dev/null; " +
        "elif command -v netstat >/dev/null 2>&1; then echo '#tool netstat'; netstat -tulpn 2>/dev/null; " +
        "else echo '#tool none'; fi; echo '#ps'; ps -eo pid=,user= 2>/dev/null; echo '#uid'; id -u";

    public static PortsSnapshot Parse(string output)
    {
        string tool = "none", section = ""; bool root = false;
        var socketLines = new List<string>(); var owners = new Dictionary<int, string>();
        foreach (var raw in output.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("#tool ", StringComparison.Ordinal)) { tool = line[6..]; section = "sockets"; continue; }
            if (line == "#ps") { section = "ps"; continue; }
            if (line == "#uid") { section = "uid"; continue; }
            if (line.Length == 0) continue;
            switch (section)
            {
                case "sockets": socketLines.Add(line); break;
                case "ps":
                    var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2 && int.TryParse(parts[0], out var pid)) owners[pid] = parts[1].Trim();
                    break;
                case "uid": root = line.Trim() == "0"; break;
            }
        }
        if (tool == "none") return new PortsSnapshot([], tool, root, false, "На сервере нет ни ss, ни netstat: список портов недоступен.");
        var ports = new List<ListeningPort>();
        foreach (var line in socketLines)
            foreach (var port in tool == "ss" ? ParseSs(line) : ParseNetstat(line))
                ports.Add(port with { User = port.Pid is { } p && owners.TryGetValue(p, out var user) ? user : null });
        var ordered = ports.Distinct().OrderBy(p => p.Port).ThenBy(p => p.Protocol, StringComparer.Ordinal).ThenBy(p => p.Address, StringComparer.Ordinal).ToArray();
        return new PortsSnapshot(ordered, tool, root, !root && ordered.Any(p => p.Pid is null), null);
    }

    // ss -H -tulpn: Netid State Recv-Q Send-Q Local Peer [users:(("name",pid=1,fd=3),...)]
    static IEnumerable<ListeningPort> ParseSs(string line)
    {
        var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 5 || fields[0] is not ("tcp" or "udp")) yield break;
        if (!TrySplitEndpoint(fields[4], out var address, out var port, out var iface)) yield break;
        var state = fields[1];
        var users = UsersRegex().Matches(line);
        if (users.Count == 0) { yield return new ListeningPort(fields[0], state, address, port, null, null, null, iface); yield break; }
        foreach (var pair in users.Select(m => (Name: m.Groups[1].Value, Pid: int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))).DistinctBy(u => u.Pid))
            yield return new ListeningPort(fields[0], state, address, port, pair.Pid, pair.Name, null, iface);
    }

    // netstat -tulpn: Proto Recv-Q Send-Q Local Foreign [State] PID/Program
    static IEnumerable<ListeningPort> ParseNetstat(string line)
    {
        var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 5 || !(fields[0].StartsWith("tcp", StringComparison.Ordinal) || fields[0].StartsWith("udp", StringComparison.Ordinal))) yield break;
        var protocol = fields[0].StartsWith("tcp", StringComparison.Ordinal) ? "tcp" : "udp";
        if (!TrySplitEndpoint(fields[3], out var address, out var port, out var iface)) yield break;
        string state = protocol == "tcp" && fields.Length >= 6 ? fields[5] : "UNCONN";
        int? pid = null; string? process = null;
        // "PID/Program name" is the 7th column for tcp (after State) and the 6th for udp; the name may contain spaces.
        int column = protocol == "tcp" ? 6 : 5;
        if (fields.Length > column)
        {
            var owner = fields[column]; int slash = owner.IndexOf('/');
            if (slash > 0 && int.TryParse(owner[..slash], out var p)) { pid = p; process = owner[(slash + 1)..].TrimEnd(':'); if (process.Length == 0) process = null; }
        }
        yield return new ListeningPort(protocol, state, address, port, pid, process, null, iface);
    }

    /// <summary>"0.0.0.0:22", "[::]:80", "127.0.0.53%lo:53", "*:68", ":::22" (netstat IPv6).</summary>
    internal static bool TrySplitEndpoint(string text, out string address, out int port, out string? iface)
    {
        address = ""; port = 0; iface = null;
        int colon = text.LastIndexOf(':'); if (colon <= 0 || !int.TryParse(text[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out port)) return false;
        address = text[..colon];
        if (address.StartsWith('[') && address.EndsWith(']')) address = address[1..^1];
        int percent = address.IndexOf('%'); if (percent >= 0) { iface = address[(percent + 1)..]; address = address[..percent]; }
        if (address.Length == 0 || address == "::") address = "::";
        return port is > 0 and <= 65535;
    }

    [GeneratedRegex("\\(\"((?:[^\"\\\\]|\\\\.)*)\",pid=(\\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex UsersRegex();
}
