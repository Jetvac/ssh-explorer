using System.Globalization;
using System.Text.RegularExpressions;

namespace SSHExplorer.Core;

/// <summary>
/// User and group names for UID/GID on the server (getent), and the reverse for owner fields that accept a name.
/// Read-only commands; names are validated before they are put into a command.
/// </summary>
public static partial class Ownership
{
    public static string NamesCommand(int uid, int gid) =>
        $"printf 'u:%s\\n' \"$(getent passwd {uid} | cut -d: -f1)\"; printf 'g:%s\\n' \"$(getent group {gid} | cut -d: -f1)\"";

    public static (string? User, string? Group) ParseNames(string output)
    {
        string? user = null, group = null;
        foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("u:", StringComparison.Ordinal) && line.Length > 2) user = line[2..];
            else if (line.StartsWith("g:", StringComparison.Ordinal) && line.Length > 2) group = line[2..];
        }
        return (user, group);
    }

    /// <summary>A number is used as is; a name is looked up (null — no such user/group, or not a valid name).</summary>
    public static string? IdCommand(string text, bool group)
    {
        text = text.Trim();
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _)) return null;
        return NameRegex().IsMatch(text) ? $"getent {(group ? "group" : "passwd")} {PosixPath.Quote(text)} | cut -d: -f3" : null;
    }

    public static bool IsValidName(string text) => NameRegex().IsMatch(text.Trim());

    /// <summary>How many objects a recursive change would touch (the root included; other file systems are not crossed).</summary>
    public static string CountCommand(string path) => "find " + PosixPath.Quote(path) + " -xdev 2>/dev/null | wc -l";

    /// <summary>Special bits shown as separate check boxes: setuid 4000, setgid 2000, sticky 1000 (octal).</summary>
    public static readonly (int Bit, string Label)[] SpecialBits = [(0x800, "setuid"), (0x400, "setgid"), (0x200, "sticky")];

    [GeneratedRegex("^[a-z_][a-z0-9_.-]{0,31}\\$?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)] private static partial Regex NameRegex();
}
