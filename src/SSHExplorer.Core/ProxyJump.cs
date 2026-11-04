namespace SSHExplorer.Core;

/// <summary>One jump host as written: a profile name or <c>[user@]host[:port]</c> (IPv6 as <c>[addr]:port</c>).</summary>
public sealed record ProxyJumpHop(string Text, string? User, string Host, int? Port);

/// <summary>A resolved jump host: the profile to connect with and the secret it uses (never the target's password).</summary>
public sealed record ResolvedJump(ServerProfile Profile, bool FromSavedProfile, bool NeedsSavedSecret);

public static class ProxyJumpSpec
{
    public const int MaxHops = 3;

    public static IReadOnlyList<ProxyJumpHop> Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)) return [];
        var hops = new List<ProxyJumpHop>();
        foreach (var raw in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.Contains("://", StringComparison.Ordinal)) throw new ArgumentException($"ProxyJump «{raw}»: URI-форма не поддерживается, используйте [пользователь@]узел[:порт].");
            string? user = null; var rest = raw;
            int at = rest.LastIndexOf('@');
            if (at >= 0) { user = rest[..at]; rest = rest[(at + 1)..]; if (user.Length == 0) throw new ArgumentException($"ProxyJump «{raw}»: пустое имя пользователя."); }
            string host; int? port = null;
            if (rest.StartsWith('['))
            {
                int close = rest.IndexOf(']'); if (close < 0) throw new ArgumentException($"ProxyJump «{raw}»: незакрытая скобка IPv6.");
                host = rest[1..close]; var tail = rest[(close + 1)..];
                if (tail.Length > 0) port = ParsePort(tail.StartsWith(':') ? tail[1..] : throw new ArgumentException($"ProxyJump «{raw}»: после адреса ожидается :порт."), raw);
            }
            else if (rest.Count(c => c == ':') == 1) { var parts = rest.Split(':'); host = parts[0]; port = ParsePort(parts[1], raw); }
            else host = rest; // bare IPv6 without brackets has no port
            // A bare item may be a profile name, which can contain spaces; an address form (user@ or :port) cannot.
            bool nameOnly = user is null && port is null;
            if (host.Length == 0 || (!nameOnly && host.Any(char.IsWhiteSpace)) || host.IndexOfAny(['/', '\\']) >= 0) throw new ArgumentException($"ProxyJump «{raw}»: недопустимый адрес.");
            hops.Add(new ProxyJumpHop(raw, user, host, port));
        }
        if (hops.Count > MaxHops) throw new ArgumentException($"ProxyJump: не более {MaxHops} промежуточных узлов.");
        return hops;
    }

    static int ParsePort(string text, string raw) =>
        int.TryParse(text, out var port) && port is >= 1 and <= 65535 ? port : throw new ArgumentException($"ProxyJump «{raw}»: порт должен быть от 1 до 65535.");

    /// <summary>
    /// The hop the target connects through is the LAST one; earlier hops become that hop's own ProxyJump (OpenSSH order).
    /// A hop naming a saved profile uses that profile's sign-in. An address hop uses the target's agent or key file;
    /// a password is never forwarded to another host, so an address hop for a password profile is refused.
    /// </summary>
    public static ResolvedJump ResolveLast(ServerProfile target, IReadOnlyCollection<ServerProfile> saved, ISet<string>? visited = null)
    {
        var hops = Parse(target.ProxyJump);
        if (hops.Count == 0) throw new InvalidOperationException("ProxyJump не задан.");
        visited ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Key(target.Host, target.Port) };
        var last = hops[^1]; var earlier = string.Join(",", hops.Take(hops.Count - 1).Select(h => h.Text));
        var named = last.User is null && last.Port is null ? saved.FirstOrDefault(p => string.Equals(p.Name, last.Host, StringComparison.OrdinalIgnoreCase) && p.Id != target.Id) : null;
        ServerProfile hop;
        bool needsSecret;
        if (named is not null)
        {
            hop = Clone(named); needsSecret = named.Authentication is AuthenticationKind.Password or AuthenticationKind.KeyboardInteractive;
            // The named profile's own chain applies unless the target's list already gives earlier hops.
            if (earlier.Length > 0) hop.ProxyJump = earlier;
        }
        else
        {
            if (last.Host.Any(char.IsWhiteSpace)) throw new InvalidOperationException($"ProxyJump: профиль «{last.Host}» не найден.");
            if (target.Authentication is not (AuthenticationKind.Agent or AuthenticationKind.PrivateKey))
                throw new InvalidOperationException($"Промежуточный узел «{last.Text}» задан адресом, а профиль входит по паролю. Пароль не передаётся другому узлу: создайте профиль для «{last.Host}» и укажите его имя в ProxyJump или используйте ключ/агент.");
            hop = new ServerProfile
            {
                Name = last.Text, Host = last.Host, Port = last.Port ?? 22, Username = last.User ?? target.Username,
                Authentication = target.Authentication, PrivateKeyPath = target.PrivateKeyPath, TimeoutSeconds = target.TimeoutSeconds, ProxyJump = earlier,
            };
            needsSecret = false;
        }
        if (!visited.Add(Key(hop.Host, hop.Port))) throw new InvalidOperationException($"ProxyJump образует цикл через {hop.Endpoint}.");
        if (visited.Count > MaxHops + 1) throw new InvalidOperationException($"ProxyJump: не более {MaxHops} промежуточных узлов.");
        return new ResolvedJump(hop, named is not null, needsSecret);
    }

    static string Key(string host, int port) => host.Trim('[', ']').ToLowerInvariant() + ":" + port;
    static ServerProfile Clone(ServerProfile p) => new()
    {
        Id = p.Id, Name = p.Name, Host = p.Host, Port = p.Port, Username = p.Username, Authentication = p.Authentication, PrivateKeyPath = p.PrivateKeyPath,
        TimeoutSeconds = p.TimeoutSeconds, SaveSecret = p.SaveSecret, ProxyJump = p.ProxyJump, Group = p.Group,
    };
}
