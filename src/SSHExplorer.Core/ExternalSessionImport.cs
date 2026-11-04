namespace SSHExplorer.Core;

/// <summary>A stored session of another program: its (still URL-encoded) key name and values.</summary>
public sealed record ExternalSession(string KeyName, IReadOnlyDictionary<string, object?> Values);

/// <summary>
/// Maps PuTTY and WinSCP saved sessions to profile candidates. Read-only: nothing in those programs is changed.
/// Passwords (WinSCP stores an obfuscated one) are never imported; non-SSH sessions are skipped with a reason.
/// </summary>
public static class ExternalSessionImport
{
    public static (IReadOnlyList<ImportCandidate> Candidates, IReadOnlyList<string> Skipped) FromPutty(IEnumerable<ExternalSession> sessions, string localUser,
        IReadOnlyCollection<ServerProfile> existing, Func<string, bool>? fileExists = null)
    {
        var candidates = new List<ImportCandidate>(); var skipped = new List<string>();
        foreach (var session in sessions)
        {
            var name = Decode(session.KeyName);
            if (name.Equals("Default Settings", StringComparison.OrdinalIgnoreCase)) continue;
            var protocol = Text(session, "Protocol") ?? "ssh";
            if (!protocol.Equals("ssh", StringComparison.OrdinalIgnoreCase)) { skipped.Add($"{name}: протокол {protocol} — не SSH"); continue; }
            var notes = new List<string>();
            if (Number(session, "ProxyMethod") is > 0) notes.Add("Прокси PuTTY не переносится.");
            if (Text(session, "PortForwardings") is { Length: > 0 }) notes.Add("Переадресация портов PuTTY не переносится.");
            candidates.Add(Map(name, null, Text(session, "HostName"), Number(session, "PortNumber"), Text(session, "UserName"), Text(session, "PublicKeyFile"), localUser, existing, fileExists, notes));
        }
        return (candidates, skipped);
    }

    public static (IReadOnlyList<ImportCandidate> Candidates, IReadOnlyList<string> Skipped) FromWinScp(IEnumerable<ExternalSession> sessions, string localUser,
        IReadOnlyCollection<ServerProfile> existing, Func<string, bool>? fileExists = null)
    {
        var candidates = new List<ImportCandidate>(); var skipped = new List<string>();
        foreach (var session in sessions)
        {
            var path = Decode(session.KeyName);
            if (path.Equals("Default Settings", StringComparison.OrdinalIgnoreCase)) continue;
            // WinSCP folders are part of the session name ("Прод/web"): the folder becomes the profile group.
            int slash = path.LastIndexOf('/');
            var group = slash > 0 ? path[..slash] : null; var name = slash >= 0 ? path[(slash + 1)..] : path;
            // FSProtocol: 0 SCP, 1 SFTP with SCP fallback, 2 SFTP; 5 FTP, 6 WebDAV, 7 S3.
            var fs = Number(session, "FSProtocol") ?? 2;
            if (fs is not (0 or 1 or 2)) { skipped.Add($"{path}: протокол {(fs switch { 5 => "FTP", 6 => "WebDAV", 7 => "S3", _ => fs.ToString() })} — не SSH"); continue; }
            var notes = new List<string>();
            if (Text(session, "Password") is { Length: > 0 }) notes.Add("Сохранённый в WinSCP пароль не переносится.");
            if (Text(session, "TunnelHostName") is { Length: > 0 } tunnel) notes.Add($"Туннель WinSCP через {tunnel} не переносится: укажите его в «Промежуточных узлах».");
            var candidate = Map(name, group, Text(session, "HostName"), Number(session, "PortNumber"), Text(session, "UserName"), Text(session, "PublicKeyFile"), localUser, existing, fileExists, notes);
            if (Text(session, "RemoteDirectory") is { Length: > 0 } remote && remote.StartsWith('/')) candidate.Profile.InitialPath = remote;
            candidates.Add(candidate);
        }
        return (candidates, skipped);
    }

    /// <summary>Reads WinSCP.ini: <c>[Sessions\name]</c> sections with <c>Key=Value</c> lines.</summary>
    public static IReadOnlyList<ExternalSession> ParseWinScpIni(string text)
    {
        var result = new List<ExternalSession>(); Dictionary<string, object?>? current = null;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var section = line[1..^1];
                if (section.StartsWith(@"Sessions\", StringComparison.OrdinalIgnoreCase)) { current = new(StringComparer.OrdinalIgnoreCase); result.Add(new ExternalSession(section[9..], current)); }
                else current = null;
                continue;
            }
            int eq = line.IndexOf('=');
            if (current is null || eq <= 0 || line.StartsWith(';')) continue;
            current[line[..eq]] = line[(eq + 1)..];
        }
        return result;
    }

    static ImportCandidate Map(string name, string? group, string? host, int? port, string? user, string? keyFile, string localUser,
        IReadOnlyCollection<ServerProfile> existing, Func<string, bool>? fileExists, List<string> notes)
    {
        fileExists ??= File.Exists;
        host = host?.Trim() ?? "";
        // PuTTY allows "user@host" in HostName.
        if (host.Contains('@') && string.IsNullOrEmpty(user)) { var at = host.LastIndexOf('@'); user = host[..at]; host = host[(at + 1)..]; }
        var profile = new ServerProfile { Name = name, Host = host, Port = port is >= 1 and <= 65535 ? port.Value : 22, Username = string.IsNullOrWhiteSpace(user) ? localUser : user.Trim(), Group = group ?? "" };
        if (string.IsNullOrWhiteSpace(user)) notes.Add($"Пользователь не указан: используется имя Windows «{localUser}».");
        if (!string.IsNullOrWhiteSpace(keyFile))
        {
            var key = Environment.ExpandEnvironmentVariables(keyFile.Trim());
            if (fileExists(key)) { profile.Authentication = AuthenticationKind.PrivateKey; profile.PrivateKeyPath = key; }
            else notes.Add($"Ключ {key} не найден. Выбран вход по паролю.");
        }
        var duplicate = existing.FirstOrDefault(p => string.Equals(p.Host, profile.Host, StringComparison.OrdinalIgnoreCase) && p.Port == profile.Port && p.Username == profile.Username)?.Name;
        try { profile.Validate(); }
        catch (Exception ex) when (ex is ArgumentException or FileNotFoundException) { notes.Add("Требует исправления: " + ex.Message); }
        return new ImportCandidate(profile, notes, duplicate);
    }

    static string Decode(string keyName) { try { return Uri.UnescapeDataString(keyName); } catch (UriFormatException) { return keyName; } }
    static string? Text(ExternalSession s, string key) => s.Values.TryGetValue(key, out var v) && v is not null ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : null;
    static int? Number(ExternalSession s, string key) => s.Values.TryGetValue(key, out var v) ? v switch { int i => i, long l when l is >= int.MinValue and <= int.MaxValue => (int)l, string t when int.TryParse(t, out var p) => p, _ => null } : null;
}
