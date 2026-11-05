using System.Globalization;
using System.Text;

namespace SSHExplorer.Core;

/// <summary>One line of a crontab. Jobs know their schedule and command; other lines are kept verbatim.</summary>
public sealed record CronLine(int Index, string Raw, bool IsJob, bool Enabled, string Schedule, string Command, string? Comment)
{
    public bool IsEnvironment => !IsJob && Raw.Trim() is { Length: > 0 } t && !t.StartsWith('#') && t.Contains('=');
}

/// <summary>A crontab as read: its lines, whose crontab it is, and the server's local time (cron runs in it).</summary>
public sealed record CronTab(IReadOnlyList<CronLine> Lines, string Text, string User, DateTime ServerNow, string? Problem)
{
    public IEnumerable<CronLine> Jobs => Lines.Where(l => l.IsJob);
}

/// <summary>
/// The user's crontab (or root's through sudo): read, explained in words with the next runs, edited line by line and
/// written back whole with <c>crontab file</c> after a check that nobody changed it meanwhile. A job switched off is
/// commented with a marker so it can be switched on again exactly as it was.
/// </summary>
public static class Cron
{
    public const string OffMarker = "#sshx-off# ";
    static readonly string[] Macros = ["@reboot", "@yearly", "@annually", "@monthly", "@weekly", "@daily", "@midnight", "@hourly"];
    static readonly string[] Months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];
    static readonly string[] Days = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];
    static readonly string[] DayNames = ["воскресеньям", "понедельникам", "вторникам", "средам", "четвергам", "пятницам", "субботам"];

    public static string ReadCommand(string prefix) =>
        "echo \"#user $(id -un)\"; echo \"#now $(date '+%Y-%m-%dT%H:%M:%S')\"; echo '#crontab'; " + prefix + "crontab -l 2>&1; echo \"#exit $?\"";

    public static CronTab Parse(string output)
    {
        var all = output.Replace("\r", "").Split('\n');
        string user = ""; DateTime now = DateTime.Now; var body = new List<string>(); bool inBody = false; int exit = 0;
        foreach (var line in all)
        {
            if (!inBody && line.StartsWith("#user ", StringComparison.Ordinal)) { user = line[6..].Trim(); continue; }
            if (!inBody && line.StartsWith("#now ", StringComparison.Ordinal)) { DateTime.TryParse(line[5..], CultureInfo.InvariantCulture, DateTimeStyles.None, out now); continue; }
            if (!inBody && line == "#crontab") { inBody = true; continue; }
            if (inBody && line.StartsWith("#exit ", StringComparison.Ordinal)) { int.TryParse(line[6..], out exit); break; }
            if (inBody) body.Add(line);
        }
        if (body.Count > 0 && body[^1].Length == 0) body.RemoveAt(body.Count - 1);
        string? problem = null;
        if (exit != 0)
        {
            var text = string.Join(" ", body).Trim();
            if (text.Contains("no crontab", StringComparison.OrdinalIgnoreCase)) body.Clear();
            else { problem = "crontab недоступен: " + (text.Length > 0 ? text : "код " + exit); body.Clear(); }
        }
        // Lines the crontab tool adds itself on some systems are not the user's.
        var lines = body.Select((raw, i) => ParseLine(i, raw)).ToArray();
        return new CronTab(lines, string.Join("\n", body), user, now, problem);
    }

    public static CronLine ParseLine(int index, string raw)
    {
        var text = raw.Trim(); bool enabled = true;
        if (text.StartsWith(OffMarker.Trim(), StringComparison.Ordinal)) { enabled = false; text = text[OffMarker.Trim().Length..].Trim(); }
        else if (text.StartsWith('#') || text.Length == 0) return new CronLine(index, raw, false, true, "", "", null);
        var parts = text.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && Macros.Contains(parts[0].ToLowerInvariant())) return new CronLine(index, raw, true, enabled, parts[0].ToLowerInvariant(), Rest(text, 1), null);
        if (parts.Length >= 6 && !parts[0].Contains('='))
        {
            var schedule = string.Join(' ', parts.Take(5));
            if (ValidateSchedule(schedule) is null) return new CronLine(index, raw, true, enabled, schedule, Rest(text, 5), null);
        }
        return new CronLine(index, raw, false, true, "", "", null);
    }

    static string Rest(string text, int words)
    {
        int at = 0;
        for (int w = 0; w < words; w++) { while (at < text.Length && text[at] is ' ' or '\t') at++; while (at < text.Length && text[at] is not (' ' or '\t')) at++; }
        return text[at..].Trim();
    }

    public static string? ValidateCommand(string command) =>
        command.Trim().Length == 0 ? "Укажите команду." : command.Contains('\n') || command.Contains('\r') ? "Команда — одна строка (несколько команд можно разделить «;» или вынести в скрипт)." : command.Length > 2000 ? "Команда слишком длинная." : null;

    /// <summary>Null for a valid five-field schedule or macro; otherwise which field is wrong.</summary>
    public static string? ValidateSchedule(string schedule)
    {
        var s = schedule.Trim().ToLowerInvariant();
        if (Macros.Contains(s)) return null;
        var fields = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5) return "Расписание — пять полей: минута, час, день месяца, месяц, день недели (или @daily, @reboot…).";
        string[] names = ["Минута", "Час", "День месяца", "Месяц", "День недели"];
        (int Min, int Max)[] ranges = [(0, 59), (0, 23), (1, 31), (1, 12), (0, 7)];
        for (int i = 0; i < 5; i++) if (Expand(fields[i], ranges[i].Min, ranges[i].Max, i == 3 ? Months : i == 4 ? Days : null) is null) return $"{names[i]}: «{fields[i]}» — допустимо {ranges[i].Min}–{ranges[i].Max}, «*», списки через запятую, диапазоны «a-b» и шаг «/n».";
        return null;
    }

    static SortedSet<int>? Expand(string field, int min, int max, string[]? names)
    {
        var result = new SortedSet<int>();
        foreach (var part in field.Split(','))
        {
            if (part.Length == 0) return null;
            var stepParts = part.Split('/'); if (stepParts.Length > 2) return null;
            int step = 1; if (stepParts.Length == 2 && (!int.TryParse(stepParts[1], out step) || step < 1)) return null;
            int from, to;
            if (stepParts[0] == "*") { from = min; to = max; }
            else
            {
                var range = stepParts[0].Split('-'); if (range.Length > 2) return null;
                int? a = Value(range[0], names, min), b = range.Length == 2 ? Value(range[1], names, min) : a;
                if (a is null || b is null || a < min || b > max || a > b) return null;
                from = a.Value; to = stepParts.Length == 2 && range.Length == 1 ? max : b.Value;
            }
            for (int v = from; v <= to; v += step) result.Add(v);
        }
        return result;
    }

    static int? Value(string text, string[]? names, int min)
    {
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return n;
        if (names is not null) { var i = Array.IndexOf(names, text); if (i >= 0) return i + (min == 1 ? 1 : 0); }
        return null;
    }

    /// <summary>The schedule in plain Russian for the common shapes; the raw fields otherwise.</summary>
    public static string Describe(string schedule)
    {
        var s = schedule.Trim().ToLowerInvariant();
        switch (s)
        {
            case "@reboot": return "При загрузке сервера";
            case "@hourly": return "Каждый час";
            case "@daily" or "@midnight": return "Каждый день в 00:00";
            case "@weekly": return "По воскресеньям в 00:00";
            case "@monthly": return "1-го числа в 00:00";
            case "@yearly" or "@annually": return "1 января в 00:00";
        }
        var f = s.Split(' ', StringSplitOptions.RemoveEmptyEntries); if (f.Length != 5) return schedule;
        bool Num(string x) => int.TryParse(x, out _);
        string Time() => $"{int.Parse(f[1], CultureInfo.InvariantCulture):00}:{int.Parse(f[0], CultureInfo.InvariantCulture):00}";
        if (f[2] == "*" && f[3] == "*" && f[4] == "*")
        {
            if (f[0] == "*" && f[1] == "*") return "Каждую минуту";
            if (f[0].StartsWith("*/", StringComparison.Ordinal) && f[1] == "*") return $"Каждые {f[0][2..]} мин";
            if (Num(f[0]) && f[1] == "*") return $"Каждый час в :{int.Parse(f[0], CultureInfo.InvariantCulture):00}";
            if (Num(f[0]) && f[1].StartsWith("*/", StringComparison.Ordinal)) return $"Каждые {f[1][2..]} ч в :{int.Parse(f[0], CultureInfo.InvariantCulture):00}";
            if (Num(f[0]) && Num(f[1])) return "Каждый день в " + Time();
        }
        if (Num(f[0]) && Num(f[1]) && f[2] == "*" && f[3] == "*")
        {
            if (f[4] is "1-5" or "mon-fri") return "По будням в " + Time();
            if (f[4] is "0,6" or "6,0" or "sat,sun") return "По выходным в " + Time();
            if (Expand(f[4], 0, 7, Days) is { Count: 1 } one) return $"По {DayNames[one.Min % 7]} в " + Time();
        }
        if (Num(f[0]) && Num(f[1]) && Num(f[2]) && f[3] == "*" && f[4] == "*") return $"{f[2]}-го числа каждого месяца в " + Time();
        return "По расписанию «" + schedule.Trim() + "»";
    }

    /// <summary>The next runs after <paramref name="from"/> (server local time); none for @reboot.</summary>
    public static IReadOnlyList<DateTime> Next(string schedule, DateTime from, int count = 3)
    {
        var s = schedule.Trim().ToLowerInvariant();
        s = s switch { "@hourly" => "0 * * * *", "@daily" or "@midnight" => "0 0 * * *", "@weekly" => "0 0 * * 0", "@monthly" => "0 0 1 * *", "@yearly" or "@annually" => "0 0 1 1 *", _ => s };
        if (s == "@reboot" || ValidateSchedule(s) is not null) return [];
        var f = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var minutes = Expand(f[0], 0, 59, null)!; var hours = Expand(f[1], 0, 23, null)!; var dom = Expand(f[2], 1, 31, null)!; var months = Expand(f[3], 1, 12, Months)!;
        var dow = Expand(f[4], 0, 7, Days)!; if (dow.Contains(7)) dow.Add(0);
        bool domAny = f[2] == "*", dowAny = f[4] == "*";
        var result = new List<DateTime>();
        var t = new DateTime(from.Year, from.Month, from.Day, from.Hour, from.Minute, 0).AddMinutes(1);
        var limit = from.AddYears(5);
        while (result.Count < count && t < limit)
        {
            if (!months.Contains(t.Month)) { t = new DateTime(t.Year, t.Month, 1).AddMonths(1); continue; }
            // cron: when both day fields are restricted, either one matching is enough.
            bool dayOk = domAny && dowAny || (domAny ? dow.Contains((int)t.DayOfWeek) : dowAny ? dom.Contains(t.Day) : dom.Contains(t.Day) || dow.Contains((int)t.DayOfWeek));
            if (!dayOk) { t = t.Date.AddDays(1); continue; }
            if (!hours.Contains(t.Hour)) { t = t.Date.AddHours(t.Hour + 1); continue; }
            if (!minutes.Contains(t.Minute)) { t = t.AddMinutes(1); continue; }
            result.Add(t); t = t.AddMinutes(1);
        }
        return result;
    }

    public static string JobLine(string schedule, string command, bool enabled = true) => (enabled ? "" : OffMarker) + schedule.Trim() + " " + command.Trim();

    /// <summary>The new crontab text after replacing, inserting (index −1 appends) or removing (null) one line.</summary>
    public static string Edit(CronTab tab, int index, string? newLine, string? comment = null)
    {
        var lines = tab.Lines.Select(l => l.Raw).ToList();
        if (index < 0) { if (comment is { Length: > 0 } c) lines.Add("# " + c.Replace("\n", " ")); if (newLine is not null) lines.Add(newLine); }
        else if (newLine is null) lines.RemoveAt(index);
        else lines[index] = newLine;
        var text = string.Join("\n", lines);
        return text.Length == 0 ? "" : text + "\n";
    }

    public static string Toggle(CronLine job) => job.Enabled ? OffMarker + job.Raw.TrimStart() : job.Raw.TrimStart()[OffMarker.Trim().Length..].TrimStart();

    /// <summary>
    /// Installs <paramref name="path"/> as the crontab only if the current one still equals <paramref name="expected"/>
    /// (base64 of the text read before), keeping the previous one next to it. Prints #saved, #changed or the error.
    /// </summary>
    public static string InstallCommand(string prefix, string path, string expected, string backup) =>
        $"umask 077; cur=$({prefix}crontab -l 2>/dev/null | base64 | tr -d '\\n'); if [ \"$cur\" != {PosixPath.Quote(Convert.ToBase64String(Encoding.UTF8.GetBytes(expected.Length == 0 ? "" : expected + "\n")))} ]; then echo '#changed'; rm -f {PosixPath.Quote(path)}; exit 0; fi; " +
        $"{prefix}crontab -l > {PosixPath.Quote(backup)} 2>/dev/null; {prefix}crontab {PosixPath.Quote(path)} 2>&1 && echo '#saved'; rm -f {PosixPath.Quote(path)}";
}
