namespace SSHExplorer.Core;

public static class PosixPath
{
    public static string Normalize(string path, string home = "/")
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Contains('\0')) throw new ArgumentException("Путь содержит NUL.");
        if (path == "~") path = home;
        else if (path.StartsWith("~/", StringComparison.Ordinal)) path = Combine(home, path[2..]);
        else if (path.StartsWith('~')) throw new ArgumentException("Поддерживается только ~ текущего пользователя.");
        if (!path.StartsWith('/')) throw new ArgumentException("Укажите абсолютный POSIX-путь или ~.");
        List<string> parts = [];
        foreach (var part in path.Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else parts.Add(part);
        }
        return "/" + string.Join('/', parts);
    }
    public static string Combine(string parent, string name) => parent.TrimEnd('/') + "/" + name;
    public static string Parent(string path) { path = Normalize(path); int i = path.LastIndexOf('/'); return i <= 0 ? "/" : path[..i]; }
    public static string Name(string path) => path.TrimEnd('/').Split('/')[^1];
    public static string Quote(string value)
    {
        if (value.Contains('\0')) throw new ArgumentException("NUL нельзя передать как POSIX-аргумент.");
        return "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }
    public static bool IsSameOrDescendant(string path, string parent)
    { path = Normalize(path); parent = Normalize(parent); return path == parent || parent == "/" || path.StartsWith(parent + "/", StringComparison.Ordinal); }
    public static void ValidateName(string name)
    { if (string.IsNullOrEmpty(name) || name is "." or ".." || name.Contains('/') || name.Contains('\0')) throw new ArgumentException("Недопустимое POSIX-имя: пустое имя, . , .. , / или NUL."); }
}
