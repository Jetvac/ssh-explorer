#if CORE_TESTS
using SSHExplorer.Core;

/// <summary>crontab: parsing, schedules in words, next runs, edits, the off marker and the safe install command.</summary>
internal static class CronTests
{
    const string Output = "#user deploy\n#now 2026-11-05T10:20:00\n#crontab\n# m h dom mon dow command\nMAILTO=ops@example.org\n*/15 * * * * /usr/local/bin/check\n30 3 * * * tar czf /backup/etc.tgz /etc\n#sshx-off# 0 9 * * 1-5 /opt/report.sh\n@reboot /opt/start.sh\nnot a job\n#exit 0\n";

    public static async Task RunAsync(TestSuite t)
    {
        await t.Run("cron: jobs, environment lines, comments and switched-off jobs are told apart; an empty crontab is not a problem", () =>
        {
            var tab = Cron.Parse(Output);
            var jobs = tab.Jobs.ToArray();
            TestSuite.True(tab.Problem is null && tab.User == "deploy" && tab.ServerNow == new DateTime(2026, 11, 5, 10, 20, 0) && tab.Lines.Count == 7 && jobs.Length == 4
                && jobs[0] is { Schedule: "*/15 * * * *", Command: "/usr/local/bin/check", Enabled: true } && jobs[2] is { Enabled: false, Schedule: "0 9 * * 1-5", Command: "/opt/report.sh" } && jobs[3].Schedule == "@reboot"
                && tab.Lines[1].IsEnvironment && !tab.Lines[6].IsJob, string.Join(" | ", jobs.Select(j => j.Schedule + " → " + j.Command)));
            var empty = Cron.Parse("#user u\n#now 2026-01-01T00:00:00\n#crontab\nno crontab for u\n#exit 1\n");
            var denied = Cron.Parse("#user u\n#now 2026-01-01T00:00:00\n#crontab\nYou (u) are not allowed to use this program\n#exit 1\n");
            TestSuite.True(empty.Problem is null && empty.Lines.Count == 0 && denied.Problem!.Contains("not allowed", StringComparison.Ordinal), "empty and refused");
        });

        await t.Run("cron: schedules are described in words and validated per field", () =>
        {
            TestSuite.True(Cron.Describe("*/15 * * * *") == "Каждые 15 мин" && Cron.Describe("30 3 * * *") == "Каждый день в 03:30" && Cron.Describe("0 9 * * 1-5") == "По будням в 09:00"
                && Cron.Describe("0 4 * * 0") == "По воскресеньям в 04:00" && Cron.Describe("5 * * * *") == "Каждый час в :05" && Cron.Describe("0 5 1 * *") == "1-го числа каждого месяца в 05:00"
                && Cron.Describe("@reboot") == "При загрузке сервера" && Cron.Describe("1,2 3 * 6 *").StartsWith("По расписанию", StringComparison.Ordinal), Cron.Describe("0 4 * * 0"));
            TestSuite.True(Cron.ValidateSchedule("60 * * * *")!.StartsWith("Минута", StringComparison.Ordinal) && Cron.ValidateSchedule("* 24 * * *")!.StartsWith("Час", StringComparison.Ordinal)
                && Cron.ValidateSchedule("* * * * * *") is not null && Cron.ValidateSchedule("0 0 * jan-mar mon") is null && Cron.ValidateSchedule("*/0 * * * *") is not null && Cron.ValidateSchedule("@daily") is null, "validation");
            TestSuite.True(Cron.ValidateCommand("a\nb") is not null && Cron.ValidateCommand(" ") is not null && Cron.ValidateCommand("echo ok") is null, "command");
        });

        await t.Run("cron: next runs follow cron, including the «either day field» rule", () =>
        {
            var from = new DateTime(2026, 11, 5, 10, 20, 0); // Thursday
            TestSuite.True(Cron.Next("*/15 * * * *", from).SequenceEqual([new DateTime(2026, 11, 5, 10, 30, 0), new DateTime(2026, 11, 5, 10, 45, 0), new DateTime(2026, 11, 5, 11, 0, 0)]), "every 15");
            TestSuite.True(Cron.Next("30 3 * * *", from, 1).Single() == new DateTime(2026, 11, 6, 3, 30, 0) && Cron.Next("0 9 * * 1-5", from, 2).SequenceEqual([new DateTime(2026, 11, 6, 9, 0, 0), new DateTime(2026, 11, 9, 9, 0, 0)]), "daily and weekdays");
            TestSuite.True(Cron.Next("0 0 13 * 5", from, 2).SequenceEqual([new DateTime(2026, 11, 6, 0, 0, 0), new DateTime(2026, 11, 13, 0, 0, 0)]) && Cron.Next("0 0 * * 7", from, 1).Single().DayOfWeek == DayOfWeek.Sunday, "13th or Friday; 7 is Sunday");
            TestSuite.True(Cron.Next("@reboot", from).Count == 0 && Cron.Next("0 0 30 2 *", from).Count == 0 && Cron.Next("@monthly", from, 1).Single() == new DateTime(2026, 12, 1), "never and macros");
        });

        await t.Run("cron: editing keeps every other line; switching off and on restores the job exactly", () =>
        {
            var tab = Cron.Parse(Output); var report = tab.Jobs.ElementAt(2); var check = tab.Jobs.First();
            var on = Cron.Toggle(report); var off = Cron.Toggle(check);
            TestSuite.True(on == "0 9 * * 1-5 /opt/report.sh" && off == "#sshx-off# */15 * * * * /usr/local/bin/check" && Cron.ParseLine(0, off) is { IsJob: true, Enabled: false } p && Cron.Toggle(p) == check.Raw, on + " | " + off);
            var added = Cron.Edit(tab, -1, Cron.JobLine("0 2 * * *", "/opt/new"), "ночная очистка");
            var removed = Cron.Edit(tab, check.Index, null);
            TestSuite.True(added.EndsWith("# ночная очистка\n0 2 * * * /opt/new\n", StringComparison.Ordinal) && added.StartsWith("# m h dom mon dow command\nMAILTO=ops@example.org\n", StringComparison.Ordinal)
                && !removed.Contains("/usr/local/bin/check", StringComparison.Ordinal) && removed.Contains("not a job", StringComparison.Ordinal) && Cron.Edit(Cron.Parse("#user u\n#now 2026-01-01T00:00:00\n#crontab\n#exit 0\n"), -1, null) == "", added);
            var install = Cron.InstallCommand("sudo -n ", "/home/u/.cache/sshexplorer/crontab-1.txt", tab.Text, "/home/u/.cache/sshexplorer/c.bak");
            TestSuite.True(install.StartsWith("umask 077; ", StringComparison.Ordinal) && install.Contains("sudo -n crontab -l 2>/dev/null | base64", StringComparison.Ordinal) && install.Contains("echo '#changed'", StringComparison.Ordinal)
                && install.Contains("sudo -n crontab '/home/u/.cache/sshexplorer/crontab-1.txt'", StringComparison.Ordinal), install);
        });
        await Task.CompletedTask;
    }
}
#endif
