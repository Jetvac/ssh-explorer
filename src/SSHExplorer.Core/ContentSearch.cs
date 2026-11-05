using System.Globalization;

namespace SSHExplorer.Core;

public sealed record ContentSearchOptions
{
    public string Pattern { get; init; } = "";
    public bool Regex { get; init; }
    public bool IgnoreCase { get; init; } = true;
    public bool WholeWord { get; init; }
    /// <summary>File name masks separated by commas or spaces («*.conf, *.yml»); empty — every file.</summary>
    public string Include { get; init; } = "";
    public bool SkipCommonFolders { get; init; } = true;
}

/// <summary>One matching line: path relative to the search folder, 1-based line number, the line (shortened).</summary>
public sealed record ContentMatch(string Path, int Line, string Text);

public sealed record ContentSearchResult(IReadOnlyList<ContentMatch> Matches, bool Truncated, string? Error);

/// <summary>
/// Searches file contents under a folder with the server's own grep: binary files are skipped, unreadable files are quiet,
/// other file systems and symbolic links to folders are not followed (-r, not -R). The pattern goes in with -e, quoted,
/// so it is never an option or shell text. The output is capped by line count and line length on the server.
/// </summary>
public static class ContentSearch
{
    public const int MatchLimit = 2000, LineLimit = 400, PerFileLimit = 200;
    public static readonly string[] CommonFolders = [".git", "node_modules", ".svn", ".hg", "__pycache__", ".cache", "vendor"];

    public static string? Validate(ContentSearchOptions options)
    {
        if (string.IsNullOrEmpty(options.Pattern)) return "Введите текст для поиска.";
        if (options.Pattern.Contains('\n') || options.Pattern.Contains('\r')) return "Текст поиска должен быть в одну строку.";
        if (options.Pattern.Contains('\0')) return "Недопустимый символ в тексте поиска.";
        foreach (var mask in Masks(options.Include)) if (mask.Contains('/')) return "Маска имени файла не может содержать «/».";
        return null;
    }

    public static IEnumerable<string> Masks(string include) => include.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string Command(string directory, ContentSearchOptions options)
    {
        if (Validate(options) is { } problem) throw new ArgumentException(problem, nameof(options));
        var flags = "-rIHns -Z" + (options.IgnoreCase ? " -i" : "") + (options.WholeWord ? " -w" : "") + (options.Regex ? " -E" : " -F");
        var include = string.Concat(Masks(options.Include).Select(m => " --include=" + PosixPath.Quote(m)));
        var exclude = options.SkipCommonFolders ? string.Concat(CommonFolders.Select(f => " --exclude-dir=" + PosixPath.Quote(f))) : "";
        var limit = (MatchLimit + 1).ToString(CultureInfo.InvariantCulture);
        return $"cd {PosixPath.Quote(directory)} || exit 2; T=; command -v timeout >/dev/null 2>&1 && T='timeout 120'; " +
               $"$T grep {flags} -m {PerFileLimit.ToString(CultureInfo.InvariantCulture)}{include}{exclude} -e {PosixPath.Quote(options.Pattern)} -- . | cut -c1-{(LineLimit + 600).ToString(CultureInfo.InvariantCulture)} | head -n {limit}";
    }

    /// <summary>Parses «./path\0line:text» lines; stderr from grep (a bad expression, say) becomes the error.</summary>
    public static ContentSearchResult Parse(string output, string error)
    {
        var matches = new List<ContentMatch>();
        foreach (var line in output.Split('\n'))
        {
            int zero = line.IndexOf('\0'); if (zero <= 0) continue;
            var path = line[..zero]; if (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
            var rest = line[(zero + 1)..]; int colon = rest.IndexOf(':');
            if (colon <= 0 || !int.TryParse(rest.AsSpan(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out var number)) continue;
            var text = rest[(colon + 1)..].TrimEnd('\r').Replace('\t', ' ');
            if (text.Length > LineLimit) text = text[..LineLimit] + "…";
            matches.Add(new ContentMatch(path, number, text));
        }
        bool truncated = matches.Count > MatchLimit; if (truncated) matches.RemoveRange(MatchLimit, matches.Count - MatchLimit);
        var message = error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault(l => !l.Contains("Permission denied", StringComparison.Ordinal));
        return new ContentSearchResult(matches, truncated, matches.Count == 0 && message is not null ? message : null);
    }
}
