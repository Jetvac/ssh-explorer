using System.Globalization;
using System.Text;

namespace SSHExplorer.Core;

public enum ScriptTrigger { Manual, OnConnect, Interval, Daily }

/// <summary>A saved sequence of shell commands for one server and when it runs.</summary>
public sealed class SavedScript
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    /// <summary>Commands, one per line (or any shell script; a leading #! picks the interpreter).</summary>
    public string Body { get; set; } = "";
    /// <summary>Stop at the first failing command (<c>set -e</c>) — ignored for scripts with their own #!.</summary>
    public bool StopOnError { get; set; } = true;
    public ScriptTrigger Trigger { get; set; } = ScriptTrigger.Manual;
    /// <summary>Automatic triggers can be paused without losing their settings.</summary>
    public bool Enabled { get; set; } = true;
    public int IntervalMinutes { get; set; } = 60;
    /// <summary>Local time "HH:mm" for <see cref="ScriptTrigger.Daily"/>.</summary>
    public string DailyTime { get; set; } = "03:00";
    public int TimeoutMinutes { get; set; } = 10;
    public DateTime? LastRunUtc { get; set; }
    public int? LastExitCode { get; set; }

    public SavedScript Clone() => (SavedScript)MemberwiseClone();

    public string Schedule => Trigger switch
    {
        ScriptTrigger.OnConnect => "При подключении",
        ScriptTrigger.Interval => IntervalMinutes % 60 == 0 && IntervalMinutes >= 60 ? $"Каждые {IntervalMinutes / 60} ч" : $"Каждые {IntervalMinutes} мин",
        ScriptTrigger.Daily => "Ежедневно в " + DailyTime,
        _ => "Вручную",
    } + (Trigger != ScriptTrigger.Manual && !Enabled ? " · приостановлен" : "");
}

/// <summary>The result of one run (kept in memory per session; the last exit code is also saved with the script).</summary>
public sealed record ScriptRun(Guid ScriptId, string ScriptName, string Reason, DateTime StartedUtc, TimeSpan Duration, int ExitCode, string Output, bool TimedOut)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut;
}

public static class ScriptRules
{
    public const int MaxOutputBytes = 1024 * 1024;

    public static IReadOnlyList<string> Validate(SavedScript s)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(s.Name)) errors.Add("Укажите название скрипта.");
        if (string.IsNullOrWhiteSpace(s.Body)) errors.Add("Добавьте хотя бы одну команду.");
        if (s.Body.Contains('\0')) errors.Add("Скрипт содержит нулевой символ.");
        if (s.Trigger == ScriptTrigger.Interval && s.IntervalMinutes is < 1 or > 7 * 24 * 60) errors.Add("Интервал: от 1 минуты до 7 суток.");
        if (s.Trigger == ScriptTrigger.Daily && !TryDaily(s.DailyTime, out _)) errors.Add("Время запуска в формате ЧЧ:ММ, например 03:30.");
        if (s.TimeoutMinutes is < 1 or > 24 * 60) errors.Add("Таймаут: от 1 минуты до 24 часов.");
        return errors;
    }

    public static bool TryDaily(string text, out TimeSpan time) =>
        TimeSpan.TryParseExact(text.Trim(), [@"h\:mm", @"hh\:mm"], CultureInfo.InvariantCulture, out time) && time < TimeSpan.FromDays(1);

    /// <summary>The file uploaded and run: LF line endings; <c>set -e</c> first when requested and no own #!.</summary>
    public static string FileContent(SavedScript s)
    {
        var body = s.Body.Replace("\r\n", "\n").Replace('\r', '\n');
        if (body.StartsWith("#!", StringComparison.Ordinal)) return body.EndsWith('\n') ? body : body + "\n";
        var text = new StringBuilder("#!/bin/sh\n");
        if (s.StopOnError) text.Append("set -e\n");
        text.Append(body); if (!body.EndsWith('\n')) text.Append('\n');
        return text.ToString();
    }

    /// <summary>
    /// Whether an automatic script is due at <paramref name="now"/>. <paramref name="connectedUtc"/> is when this session
    /// connected; <paramref name="lastRunUtc"/> the last start in any session (null — never).
    /// OnConnect: once per session. Interval: every N minutes, first one N minutes after connecting unless overdue.
    /// Daily: once per local day at or after the time, never twice the same day.
    /// </summary>
    public static bool IsDue(SavedScript s, DateTime nowUtc, DateTime connectedUtc, DateTime? lastRunUtc, bool ranThisSession, TimeZoneInfo? zone = null)
    {
        if (!s.Enabled || Validate(s).Count > 0) return false;
        zone ??= TimeZoneInfo.Local;
        switch (s.Trigger)
        {
            case ScriptTrigger.OnConnect: return !ranThisSession;
            case ScriptTrigger.Interval:
                var interval = TimeSpan.FromMinutes(s.IntervalMinutes);
                var since = lastRunUtc is { } last && last > connectedUtc - interval ? last : connectedUtc;
                return nowUtc - since >= interval;
            case ScriptTrigger.Daily:
                TryDaily(s.DailyTime, out var at);
                var localNow = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone);
                if (localNow.TimeOfDay < at) return false;
                if (lastRunUtc is { } previous && TimeZoneInfo.ConvertTimeFromUtc(previous, zone).Date == localNow.Date) return false;
                return true;
            default: return false;
        }
    }
}

/// <summary>A saved terminal command: typed into the terminal (Enter is the user's) or run at once.</summary>
public sealed class CommandSnippet
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public bool RunImmediately { get; set; }
    public CommandSnippet Clone() => (CommandSnippet)MemberwiseClone();
    public static string? Validate(CommandSnippet s) => s.Name.Trim().Length == 0 ? "Укажите название." : s.Command.Trim().Length == 0 ? "Укажите команду." : s.Command.Contains('\0') || s.Command.Length > 4000 ? "Команда слишком длинная или содержит недопустимые символы." : !s.RunImmediately && s.Command.Trim().Contains('\n') ? "Несколько строк можно только выполнять сразу: вписанные строки начали бы выполняться по одной. Включите «Выполнять сразу» или сохраните команды как скрипт." : null;
}
