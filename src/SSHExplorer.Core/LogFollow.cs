using System.Text.RegularExpressions;

namespace SSHExplorer.Core;

/// <summary>Following a log on the server: the wrapper that ends the command with the channel, and a bounded line buffer with a filter.</summary>
public static class LogFollow
{
    public const int Capacity = 20000;

    /// <summary>
    /// Runs <paramref name="command"/> (a simple command, so <c>$!</c> is its own PID) in the background and kills it when the
    /// SSH channel's input closes — that is, when the viewer is closed — even if the command writes nothing for a long time.
    /// Exits with the command's own status when it ends by itself.
    /// </summary>
    public static string Wrap(string command) =>
        "exec 3<&0; " + command + " & p=$!; ( cat <&3 >/dev/null 2>&1; kill $p 2>/dev/null ) & w=$!; exec 3<&-; wait $p; rc=$?; kill $w 2>/dev/null; exit $rc";

    /// <summary>A plain text filter (case-insensitive), or a regular expression when it is written as /…/.</summary>
    public static Func<string, bool>? Filter(string text, out string? error)
    {
        error = null; text = text.Trim();
        if (text.Length == 0) return null;
        if (text.Length > 2 && text[0] == '/' && text[^1] == '/')
        {
            try { var regex = new Regex(text[1..^1], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)); return line => { try { return regex.IsMatch(line); } catch (RegexMatchTimeoutException) { return false; } }; }
            catch (ArgumentException ex) { error = "Неверное регулярное выражение: " + ex.Message; return null; }
        }
        return line => line.Contains(text, StringComparison.CurrentCultureIgnoreCase);
    }
}

/// <summary>The last <see cref="LogFollow.Capacity"/> lines; older lines are dropped and counted.</summary>
public sealed class LogBuffer(int capacity = LogFollow.Capacity)
{
    readonly Queue<string> lines = new();
    public long Dropped { get; private set; }
    public int Count => lines.Count;
    public IEnumerable<string> Lines => lines;

    public void Add(string line)
    {
        lines.Enqueue(line);
        while (lines.Count > capacity) { lines.Dequeue(); Dropped++; }
    }

    public void Clear() { lines.Clear(); Dropped = 0; }

    public IEnumerable<string> Matching(Func<string, bool>? filter) => filter is null ? lines : lines.Where(filter);
}
