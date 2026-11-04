using System.Text;

namespace SSHExplorer.Core;

/// <summary>One directive as written, with its source for messages.</summary>
public sealed record SshConfigDirective(string Keyword, string[] Arguments, string Source, int Line);

/// <summary>A <c>Host</c> block (or the implicit global block before the first <c>Host</c>).</summary>
public sealed record SshConfigBlock(string[] Patterns, IReadOnlyList<SshConfigDirective> Directives, bool IsMatch);

/// <summary>Effective settings for one host alias, resolved with OpenSSH precedence (first value wins).</summary>
public sealed class SshConfigHost
{
    public required string Alias { get; init; }
    public string HostName { get; init; } = "";
    public string? User { get; init; }
    public int? Port { get; init; }
    public IReadOnlyList<string> IdentityFiles { get; init; } = [];
    public string? ProxyJump { get; init; }
    public int? ConnectTimeoutSeconds { get; init; }
    public bool? IdentitiesOnly { get; init; }
    /// <summary>Directives that applied to this host but are not used by SSH Explorer (shown, never silently dropped).</summary>
    public IReadOnlyList<string> Ignored { get; init; } = [];
}

/// <summary>
/// Reads the subset of OpenSSH <c>ssh_config(5)</c> that maps to a profile: Host patterns (with <c>*</c>, <c>?</c>
/// and <c>!</c>), HostName, User, Port, IdentityFile, ProxyJump, ConnectTimeout, IdentitiesOnly and Include.
/// The file is data: nothing is executed (ProxyCommand, LocalCommand and Match exec are reported, not run).
/// </summary>
public sealed class SshConfig
{
    public IReadOnlyList<SshConfigBlock> Blocks { get; }
    public IReadOnlyList<string> Warnings { get; }

    SshConfig(IReadOnlyList<SshConfigBlock> blocks, IReadOnlyList<string> warnings) { Blocks = blocks; Warnings = warnings; }

    static readonly HashSet<string> Used = new(StringComparer.OrdinalIgnoreCase) { "hostname", "user", "port", "identityfile", "proxyjump", "connecttimeout", "identitiesonly" };

    /// <param name="sshDirectory">The user's <c>.ssh</c> folder: relative Include paths resolve against it.</param>
    /// <param name="readFile">Reads an included file; null when it does not exist. Injected for tests.</param>
    /// <param name="listFiles">Expands an Include glob to existing files in order. Injected for tests.</param>
    public static SshConfig Parse(string text, string sshDirectory, string source = "config",
        Func<string, string?>? readFile = null, Func<string, IEnumerable<string>>? listFiles = null)
    {
        readFile ??= path => File.Exists(path) ? File.ReadAllText(path) : null;
        listFiles ??= DefaultGlob;
        var blocks = new List<SshConfigBlock>(); var warnings = new List<string>();
        var current = new List<SshConfigDirective>(); string[] patterns = ["*"]; bool isMatch = false;
        void Flush() { if (current.Count > 0 || blocks.Count > 0 || patterns is not ["*"]) blocks.Add(new SshConfigBlock(patterns, current.ToArray(), isMatch)); current = []; }
        void Read(string content, string name, int depth)
        {
            int lineNumber = 0;
            foreach (var raw in content.Replace("\r\n", "\n").Split('\n'))
            {
                lineNumber++;
                var line = raw.Trim(); if (line.Length == 0 || line[0] == '#') continue;
                var tokens = Tokenize(line, out var error);
                if (error is not null) { warnings.Add($"{name}:{lineNumber}: {error}"); continue; }
                if (tokens.Count == 0) continue;
                // "Keyword=value" and "Keyword = value" are both valid.
                var keyword = tokens[0]; var args = tokens.Skip(1).ToList();
                int eq = keyword.IndexOf('=');
                if (eq > 0) { args.Insert(0, keyword[(eq + 1)..]); keyword = keyword[..eq]; args.RemoveAll(a => a.Length == 0); }
                else if (args.Count > 0 && args[0] == "=") args.RemoveAt(0);
                else if (args.Count > 0 && args[0].StartsWith('=')) args[0] = args[0][1..];
                if (keyword.Equals("Host", StringComparison.OrdinalIgnoreCase))
                {
                    Flush(); patterns = args.Count == 0 ? ["*"] : args.ToArray(); isMatch = false; continue;
                }
                if (keyword.Equals("Match", StringComparison.OrdinalIgnoreCase))
                {
                    // Match criteria (exec, canonical, localnetwork…) are not evaluated: the block is kept but never applied.
                    Flush(); patterns = []; isMatch = true;
                    warnings.Add($"{name}:{lineNumber}: блок Match не применяется (критерии не вычисляются): {string.Join(' ', args)}");
                    continue;
                }
                if (keyword.Equals("Include", StringComparison.OrdinalIgnoreCase))
                {
                    if (depth >= 8) { warnings.Add($"{name}:{lineNumber}: слишком глубокая вложенность Include"); continue; }
                    foreach (var pattern in args)
                    {
                        var expanded = ExpandHome(pattern);
                        var full = Path.IsPathRooted(expanded) ? expanded : Path.Combine(sshDirectory, expanded);
                        var files = listFiles(full).ToArray();
                        if (files.Length == 0) warnings.Add($"{name}:{lineNumber}: Include не нашёл файлов: {pattern}");
                        // Included directives belong to the enclosing Host block, as in OpenSSH.
                        foreach (var file in files) if (readFile(file) is { } included) Read(included, Path.GetFileName(file), depth + 1);
                    }
                    continue;
                }
                current.Add(new SshConfigDirective(keyword, args.ToArray(), name, lineNumber));
            }
        }
        Read(text, source, 0);
        Flush();
        return new SshConfig(blocks, warnings);
    }

    /// <summary>Concrete aliases (no wildcard or negation) in file order — the candidates for import.</summary>
    public IReadOnlyList<string> Aliases =>
        Blocks.Where(b => !b.IsMatch).SelectMany(b => b.Patterns).Where(p => p.IndexOfAny(['*', '?', '!']) < 0).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>Resolves an alias: every matching Host block contributes, the first value of each keyword wins.</summary>
    public SshConfigHost Resolve(string alias, string localUser, string homeDirectory)
    {
        var first = new Dictionary<string, SshConfigDirective>(StringComparer.OrdinalIgnoreCase);
        var identities = new List<string>(); var ignored = new List<string>();
        foreach (var block in Blocks.Where(b => !b.IsMatch && Matches(b.Patterns, alias)))
            foreach (var d in block.Directives)
            {
                if (d.Keyword.Equals("IdentityFile", StringComparison.OrdinalIgnoreCase)) { if (d.Arguments.Length > 0) identities.Add(d.Arguments[0]); continue; }
                if (!Used.Contains(d.Keyword)) { ignored.Add($"{d.Keyword} ({d.Source}:{d.Line})"); continue; }
                first.TryAdd(d.Keyword, d);
            }
        string? Value(string key) => first.TryGetValue(key, out var d) && d.Arguments.Length > 0 ? d.Arguments[0] : null;
        var hostName = Value("HostName") is { } h ? h.Replace("%h", alias, StringComparison.Ordinal).Replace("%%", "%", StringComparison.Ordinal) : alias;
        var user = Value("User");
        int? port = int.TryParse(Value("Port"), out var p) && p is >= 1 and <= 65535 ? p : null;
        if (Value("Port") is { } badPort && port is null) ignored.Add($"Port {badPort} (недопустимое значение)");
        int? timeout = int.TryParse(Value("ConnectTimeout"), out var t) && t > 0 ? t : null;
        var proxy = Value("ProxyJump"); if (string.Equals(proxy, "none", StringComparison.OrdinalIgnoreCase)) proxy = null;
        bool? identitiesOnly = Value("IdentitiesOnly") is { } io ? io.Equals("yes", StringComparison.OrdinalIgnoreCase) : null;
        var remoteUser = user ?? localUser;
        var files = identities.Distinct(StringComparer.Ordinal).Select(f => ExpandTokens(f, homeDirectory, localUser, hostName, remoteUser, alias)).ToArray();
        return new SshConfigHost { Alias = alias, HostName = hostName, User = user, Port = port, IdentityFiles = files, ProxyJump = proxy, ConnectTimeoutSeconds = timeout, IdentitiesOnly = identitiesOnly, Ignored = ignored };
    }

    /// <summary>OpenSSH pattern-list semantics: any positive match, and no negated match.</summary>
    public static bool Matches(IEnumerable<string> patterns, string host)
    {
        bool positive = false;
        foreach (var pattern in patterns)
        {
            if (pattern.StartsWith('!')) { if (Wildcard(pattern[1..], host)) return false; }
            else if (Wildcard(pattern, host)) positive = true;
        }
        return positive;
    }

    static bool Wildcard(string pattern, string text)
    {
        // Case-insensitive like OpenSSH host matching; '*' any run, '?' one character.
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(text[t]))) { p++; t++; }
            else if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = t; }
            else if (star >= 0) { p = star + 1; t = ++mark; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    static string ExpandHome(string path) =>
        path == "~" ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        : path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]) : path;

    /// <summary>Expands <c>~</c>, <c>%d</c>, <c>%u</c>, <c>%h</c>, <c>%r</c>, <c>%n</c> and <c>%%</c> in IdentityFile.</summary>
    public static string ExpandTokens(string value, string home, string localUser, string hostName, string remoteUser, string alias)
    {
        if (value == "~") value = home;
        else if (value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal)) value = home.TrimEnd('/', '\\') + Path.DirectorySeparatorChar + value[2..];
        var result = new StringBuilder();
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != '%' || i + 1 >= value.Length) { result.Append(value[i]); continue; }
            var token = value[++i];
            result.Append(token switch { 'd' => home, 'u' => localUser, 'h' => hostName, 'r' => remoteUser, 'n' => alias, '%' => "%", _ => "%" + token });
        }
        var expanded = result.ToString();
        return Path.DirectorySeparatorChar == '\\' ? expanded.Replace('/', '\\') : expanded;
    }

    /// <summary>Splits a config line like OpenSSH: whitespace separated, double quotes group, '#' outside quotes starts a comment.</summary>
    static List<string> Tokenize(string line, out string? error)
    {
        var tokens = new List<string>(); var current = new StringBuilder(); bool quoted = false, any = false; error = null;
        foreach (var c in line)
        {
            if (c == '"') { quoted = !quoted; any = true; continue; }
            if (!quoted && c == '#') break;
            if (!quoted && char.IsWhiteSpace(c)) { if (any) { tokens.Add(current.ToString()); current.Clear(); any = false; } continue; }
            current.Append(c); any = true;
        }
        if (quoted) { error = "незакрытая кавычка"; return []; }
        if (any) tokens.Add(current.ToString());
        return tokens;
    }

    static IEnumerable<string> DefaultGlob(string pattern)
    {
        var directory = Path.GetDirectoryName(pattern); var name = Path.GetFileName(pattern);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return [];
        if (name.IndexOfAny(['*', '?']) < 0) return File.Exists(pattern) ? [pattern] : [];
        return Directory.EnumerateFiles(directory, name).Order(StringComparer.Ordinal);
    }
}
