#if CORE_TESTS
using SSHExplorer.Core;

internal static class ScriptTests
{
    public static async Task RunAsync(TestSuite t)
    {
        var utc = TimeZoneInfo.Utc;
        await t.Run("scripts: validation names name, body, interval, daily time and timeout problems", () =>
        {
            var errors = ScriptRules.Validate(new SavedScript { Name = " ", Body = "", Trigger = ScriptTrigger.Daily, DailyTime = "25:99", TimeoutMinutes = 0 });
            foreach (var expected in new[] { "название", "команду", "ЧЧ:ММ", "Таймаут" }) TestSuite.True(errors.Any(e => e.Contains(expected, StringComparison.Ordinal)), expected + " in " + string.Join(" | ", errors));
            TestSuite.True(ScriptRules.Validate(new SavedScript { Name = "x", Body = "uptime", Trigger = ScriptTrigger.Interval, IntervalMinutes = 0 }).Any(e => e.Contains("Интервал", StringComparison.Ordinal)));
            TestSuite.Equal(0, ScriptRules.Validate(new SavedScript { Name = "x", Body = "uptime", Trigger = ScriptTrigger.Daily, DailyTime = "3:05" }).Count);
        });
        await t.Run("scripts: file content uses LF, adds set -e only when asked and keeps an own #! untouched", () =>
        {
            TestSuite.Equal("#!/bin/sh\nset -e\napt update\ndf -h\n", ScriptRules.FileContent(new SavedScript { Body = "apt update\r\ndf -h", StopOnError = true }));
            TestSuite.Equal("#!/bin/sh\nuptime\n", ScriptRules.FileContent(new SavedScript { Body = "uptime\n", StopOnError = false }));
            TestSuite.Equal("#!/usr/bin/env bash\n[[ 1 == 1 ]] && echo ok\n", ScriptRules.FileContent(new SavedScript { Body = "#!/usr/bin/env bash\n[[ 1 == 1 ]] && echo ok", StopOnError = true }));
        });
        await t.Run("scripts: OnConnect once per session; Interval after N minutes from connect or last run; paused never", () =>
        {
            var connected = new DateTime(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc);
            var once = new SavedScript { Name = "x", Body = "uptime", Trigger = ScriptTrigger.OnConnect };
            TestSuite.True(ScriptRules.IsDue(once, connected, connected, null, false, utc) && !ScriptRules.IsDue(once, connected.AddHours(1), connected, connected, true, utc), "once per session");
            var every = new SavedScript { Name = "x", Body = "uptime", Trigger = ScriptTrigger.Interval, IntervalMinutes = 30 };
            TestSuite.True(!ScriptRules.IsDue(every, connected.AddMinutes(29), connected, null, false, utc) && ScriptRules.IsDue(every, connected.AddMinutes(30), connected, null, false, utc), "first after N minutes");
            TestSuite.True(!ScriptRules.IsDue(every, connected.AddMinutes(50), connected, connected.AddMinutes(30), true, utc) && ScriptRules.IsDue(every, connected.AddMinutes(60), connected, connected.AddMinutes(30), true, utc), "then N minutes after the last run");
            every.Enabled = false; TestSuite.True(!ScriptRules.IsDue(every, connected.AddDays(1), connected, null, false, utc), "paused");
        });
        await t.Run("scripts: Daily runs once a day at or after its time, not again the same day", () =>
        {
            var s = new SavedScript { Name = "x", Body = "uptime", Trigger = ScriptTrigger.Daily, DailyTime = "03:30" };
            var day = new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc);
            TestSuite.True(!ScriptRules.IsDue(s, day.AddHours(3).AddMinutes(29), day, null, false, utc), "before the time");
            TestSuite.True(ScriptRules.IsDue(s, day.AddHours(9), day, day.AddDays(-1).AddHours(3).AddMinutes(30), false, utc), "later the same day when missed");
            TestSuite.True(!ScriptRules.IsDue(s, day.AddHours(23), day, day.AddHours(3).AddMinutes(31), true, utc), "not twice a day");
            TestSuite.True(ScriptRules.IsDue(s, day.AddDays(1).AddHours(3).AddMinutes(30), day, day.AddHours(3).AddMinutes(31), true, utc), "next day");
        });
        await Task.CompletedTask;
    }
}
#endif
