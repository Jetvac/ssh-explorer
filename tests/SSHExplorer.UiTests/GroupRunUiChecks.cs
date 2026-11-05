using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using SSHExplorer.App;
using SSHExplorer.Core;

/// <summary>0.4: «Выполнить на нескольких серверах» on a fixture runner: results per server, output, rerun on failed.</summary>
internal static class GroupRunUiChecks
{
    public static void Run(AppStore store, CiVisualEvidence evidence, Action<bool, string> check)
    {
        var profiles = new[]
        {
            new ServerProfile { Name = "web-1", Host = "web1.invalid", Username = "deploy", Group = "Прод", Authentication = AuthenticationKind.Agent },
            new ServerProfile { Name = "web-2", Host = "web2.invalid", Username = "deploy", Group = "Прод", Authentication = AuthenticationKind.Agent },
            new ServerProfile { Name = "db-1", Host = "db1.invalid", Username = "deploy", Group = "Прод", Authentication = AuthenticationKind.Password },
            new ServerProfile { Name = "test", Host = "test.invalid", Username = "deploy", Group = "Тест", Authentication = AuthenticationKind.Agent, Scripts = [new SavedScript { Name = "Место на диске", Body = "df -h /", TimeoutMinutes = 2 }] },
        };
        var calls = new List<string>(); bool fixedNow = false;
        var runner = new GroupRunner(store)
        {
            RunOverride = async (p, s, ct) =>
            {
                lock (calls) calls.Add(p.Name); await Task.Delay(30, ct);
                bool fail = p.Name == "web-2" && !fixedNow;
                return new GroupRunResult(p.Id, fail ? GroupRunState.Failed : GroupRunState.Succeeded, fail ? 1 : 0, fail ? "apt: lock held\n" : "up 3 days\n", TimeSpan.FromMilliseconds(30), fail ? "Код завершения 1." : null);
            },
        };
        var window = new GroupRunWindow(profiles, profiles.Where(p => p.Group == "Прод").Select(p => p.Id).ToArray(), runner) { ShowInTaskbar = false, Left = 60, Top = 60, ConfirmOverride = _ => true };
        window.Show(); Pump();
        try
        {
            check(window.Rows.Count(r => r.Selected) == 3 && !window.StartButton.IsEnabled, "the group is ticked; nothing to run until commands are given");
            window.UseCommands("uptime"); Pump();
            check(window.StartButton.IsEnabled && (string)window.StartButton.Content == "Запустить (3)", "commands enable the run on the ticked servers");
            window.StartButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Wait(window.Running);
            var web2 = window.Rows.Single(r => r.Name == "web-2"); var db = window.Rows.Single(r => r.Name == "db-1");
            check(window.Rows.Single(r => r.Name == "web-1").State == "✓ Готово" && web2.State == "✗ Ошибка" && web2.Code == "1" && db.State == "Пропущен" && db.Summary.Contains("Пароль не сохранён", StringComparison.Ordinal)
                && window.Summary.StartsWith("Готово: 1 · ошибок: 1 · пропущено: 1", StringComparison.Ordinal), "each server shows its result and the summary counts them: " + window.Summary);
            window.Table.SelectedItem = web2; Pump();
            check(window.Output.Contains("apt: lock held", StringComparison.Ordinal) && window.RetryButton.IsEnabled, "the failed server's output is shown and a rerun is offered");
            evidence.Capture((FrameworkElement)window.Content, "group-run", new { theme = "Light", fixtureOnly = true });
            fixedNow = true; lock (calls) calls.Clear();
            window.RetryButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Wait(window.Running);
            check(calls.SequenceEqual(["web-2"]) && web2.State == "✓ Готово" && !window.RetryButton.IsEnabled, "the rerun goes only to the failed server: " + string.Join(",", calls));
        }
        finally { window.Close(); }

        // «Копировать на другой сервер»: the target's open folder is suggested; a relative path is refused.
        var a = new ServerSession(profiles[0], store); var b = new ServerSession(profiles[3], store);
        var copy = new CopyToServerWindow("site.tar.gz", [new CopyTarget("web-1", a, "/srv/www"), new CopyTarget("test", b, "/home/deploy")]) { ShowInTaskbar = false, Left = 60, Top = 60 };
        copy.Show(); Pump();
        try
        {
            check(copy.Target?.Title == "web-1" && copy.Destination == "/srv/www" && copy.OkButton.IsEnabled, "the first open server and its current folder are suggested");
            copy.Choose(1); Pump();
            check(copy.Destination == "/home/deploy", "choosing another server takes its folder");
            copy.Choose(1, "relative/dir"); Pump();
            check(!copy.OkButton.IsEnabled, "a relative destination is refused");
            copy.Choose(1, "~/incoming", moveIt: true); Pump();
            check(copy.OkButton.IsEnabled && copy.Move, "a home-relative folder and «move» are accepted");
            evidence.Capture((FrameworkElement)copy.Content, "copy-to-server", new { theme = "Light", fixtureOnly = true });
        }
        finally { copy.Close(); }

        // «Задания cron»: jobs in words with the next run; the editor explains and validates the schedule.
        const string crontab = "#user deploy\n#now 2026-11-05T10:20:00\n#crontab\nMAILTO=ops@example.org\n*/15 * * * * /usr/local/bin/check\n#sshx-off# 0 9 * * 1-5 /opt/report.sh\n#exit 0\n";
        var cronService = new CronService(new ServerSession(profiles[0], store)) { ExecuteOverride = (_, _) => Task.FromResult(new CommandResult(crontab, "", 0)) };
        var cronView = new CronView(cronService);
        var cronHost = new Window { Width = 1100, Height = 600, Content = cronView, ShowInTaskbar = false, Left = 60, Top = 60 }; cronHost.Show(); Pump();
        try
        {
            for (int i = 0; i < 50 && cronView.Rows.Count == 0; i++) Pump();
            check(cronView.Rows.Count == 2 && cronView.Rows[0].When == "Каждые 15 мин" && cronView.Rows[0].Next == new DateTime(2026, 11, 5, 10, 30, 0).ToString("dd.MM HH:mm") && cronView.Rows[1].Enabled == "Выкл." && cronView.Rows[1].Next == "—"
                && cronView.InfoText.Contains("Переменные окружения", StringComparison.Ordinal), "jobs are shown in words with the next run in server time: " + cronView.InfoText);
            evidence.Capture((FrameworkElement)cronView, "cron", new { theme = "Light", fixtureOnly = true });
        }
        finally { cronHost.Close(); cronView.Dispose(); }
        var job = new CronJobWindow(null, new DateTime(2026, 11, 5, 10, 20, 0)) { ShowInTaskbar = false, Left = 60, Top = 60 };
        job.Show(); Pump();
        try
        {
            job.Set("30 3 * * *", ""); Pump();
            check(job.Words == "Каждый день в 03:30" && job.NextText.Contains("06.11 03:30", StringComparison.Ordinal) && !job.SaveButton.IsEnabled && job.Problem is null, "the schedule is explained with the next runs; an empty command waits quietly: " + job.NextText);
            job.Set("70 3 * * *", "/opt/backup.sh"); Pump();
            check(!job.SaveButton.IsEnabled && job.Problem!.StartsWith("Минута", StringComparison.Ordinal), "a wrong minute is named");
            job.Pick(6); job.Set("0 9 * * 1-5", "/opt/backup.sh"); Pump();
            check(job.SaveButton.IsEnabled && job.Words == "По будням в 09:00", "a preset fills the schedule");
            evidence.Capture((FrameworkElement)job.Content, "cron-job", new { theme = "Light", fixtureOnly = true });
        }
        finally { job.Close(); }

        // «Обновления пакетов»: security first, stale lists and a pending reboot are said; installing is typed into the terminal.
        string? typed = null;
        var packages = new PackagesWindow("web-1", _ => Task.FromResult("#mgr apt\n#age 180000\ncurl/jammy-updates 7.81.0-1ubuntu1.16 amd64 [upgradable from: 7.81.0-1ubuntu1.15]\nopenssl/jammy-security 3.0.2-0ubuntu1.15 amd64 [upgradable from: 3.0.2-0ubuntu1.14]\n#reboot\n#end\n"),
            text => { typed = text; return Task.CompletedTask; }) { ShowInTaskbar = false, Left = 60, Top = 60 };
        packages.Show(); Pump();
        try
        {
            for (int i = 0; i < 50 && packages.Report is null; i++) Pump();
            check(packages.SummaryText == "Обновлений: 2, безопасности: 1 · требуется перезагрузка" && packages.NoteText.Contains("2 дн.", StringComparison.Ordinal) && packages.NoteText.Contains("перезагрузки", StringComparison.Ordinal) && packages.SecurityButton.IsEnabled,
                "updates, stale lists and a pending reboot are reported: " + packages.NoteText);
            evidence.Capture((FrameworkElement)packages.Content, "packages", new { theme = "Light", fixtureOnly = true });
            packages.SecurityButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump();
            check(typed == "sudo apt-get update && sudo apt-get install --only-upgrade 'openssl'" && packages.Typed == typed, "the security-only command is typed into the terminal, not run: " + typed);
        }
        finally { if (packages.IsVisible) packages.Close(); }
    }

    static void Wait(Task task) { var watch = System.Diagnostics.Stopwatch.StartNew(); while (!task.IsCompleted) { if (watch.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("group run"); Pump(); } task.GetAwaiter().GetResult(); Pump(); }
    static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false); Dispatcher.PushFrame(frame); }
}
