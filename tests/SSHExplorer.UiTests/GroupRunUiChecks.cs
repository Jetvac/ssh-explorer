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
    }

    static void Wait(Task task) { var watch = System.Diagnostics.Stopwatch.StartNew(); while (!task.IsCompleted) { if (watch.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("group run"); Pump(); } task.GetAwaiter().GetResult(); Pump(); }
    static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false); Dispatcher.PushFrame(frame); }
}
