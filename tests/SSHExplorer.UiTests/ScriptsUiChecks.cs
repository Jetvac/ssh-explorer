using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using SSHExplorer.App;
using SSHExplorer.Core;

/// <summary>0.3 S: Scripts view on a fixture runner (no server) and the script editor.</summary>
internal static class ScriptsUiChecks
{
    public static void Run(AppStore store, CiVisualEvidence evidence, Action<bool, string> check)
    {
        var errors = new List<string>(); Ui.ErrorOverride = message => { lock (errors) errors.Add(message); };
        try { RunCore(store, evidence, check); }
        finally { Ui.ErrorOverride = null; }
        lock (errors) check(errors.Count == 0, "no error dialog was raised during the Scripts checks" + (errors.Count > 0 ? ": " + string.Join(" | ", errors) : ""));
    }

    static void RunCore(AppStore store, CiVisualEvidence evidence, Action<bool, string> check)
    {
        var profile = new ServerProfile { Name = "Скрипты · фикстура", Host = "scripts-fixture.example.invalid", Username = "fixture",
            Scripts = [new SavedScript { Name = "Место на дисках", Body = "df -h" }, new SavedScript { Name = "Обновление", Body = "apt update\napt upgrade -y", Trigger = ScriptTrigger.Daily, DailyTime = "03:30" }] };
        Complete(store.SaveProfileAsync(profile, null));
        var session = new ServerSession(profile, store);
        var runner = new ScriptRunner(session, () => store.Profiles.Single(p => p.Id == profile.Id).Scripts, () => true)
        {
            ExecuteOverride = (script, _) => Task.FromResult(script.Body.StartsWith("apt", StringComparison.Ordinal) ? (100, "E: Could not open lock file\n", false) : (0, "Filesystem  Size  Used Avail Use% Mounted on\n/dev/sda1   40G   12G   26G  32% /\n", false)),
        };
        var view = new ScriptsView(session, runner) { ConnectedOverride = () => true };
        var host = new Window { Width = 1100, Height = 720, Content = view, ShowInTaskbar = false, Left = 60, Top = 60 }; host.Show(); Layout(host);
        try
        {
            Button Cmd(string key) => Descendants<Button>(view).Single(b => b.Name == "Script" + key);
            var names = view.Table.Items.Cast<ScriptRow>().Select(r => r.Name).ToArray();
            check(names.SequenceEqual(["Место на дисках", "Обновление"]) && view.Table.Items.Cast<ScriptRow>().Single(r => r.Name == "Обновление").Schedule == "Ежедневно в 03:30", "scripts list shows names and schedules: " + string.Join(",", names));
            view.Table.SelectedItem = view.Table.Items.Cast<ScriptRow>().Single(r => r.Name == "Место на дисках"); Pump();
            Cmd("Run").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var output = Descendants<TextBox>(view).Single(b => b.Name == "ScriptOutput");
            for (int i = 0; i < 100 && !output.Text.Contains("/dev/sda1", StringComparison.Ordinal); i++) Pump();
            var row = view.Table.Items.Cast<ScriptRow>().Single(r => r.Name == "Место на дисках");
            check(output.Text.Contains("/dev/sda1", StringComparison.Ordinal) && row.Result == "Успешно" && row.LastRun != "—", "running a script shows its output and a successful result");
            view.Table.SelectedItem = view.Table.Items.Cast<ScriptRow>().Single(r => r.Name == "Обновление"); Pump();
            Cmd("Run").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            for (int i = 0; i < 100 && !output.Text.Contains("lock file", StringComparison.Ordinal); i++) Pump();
            check(view.Table.Items.Cast<ScriptRow>().Single(r => r.Name == "Обновление").Result == "Ошибка (код 100)" && store.Profiles.Single(p => p.Id == profile.Id).Scripts.Single(s => s.Name == "Обновление").LastExitCode == 100, "a failing run shows its exit code and is saved with the script");
            view.EditOverride = _ => new SavedScript { Name = "Перезапуск nginx", Body = "systemctl reload nginx", Trigger = ScriptTrigger.OnConnect };
            Cmd("Add").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            // The add waits for the previous run to finish and then saves the profile: wait by time, not by a pump count.
            var addWatch = System.Diagnostics.Stopwatch.StartNew();
            while (addWatch.Elapsed < TimeSpan.FromSeconds(10) && !(view.Table.Items.Count == 3 && store.Profiles.Single(p => p.Id == profile.Id).Scripts.Any(s => s.Name == "Перезапуск nginx"))) Pump();
            check(view.Table.Items.Count == 3 && store.Profiles.Single(p => p.Id == profile.Id).Scripts.Any(s => s.Name == "Перезапуск nginx" && s.Trigger == ScriptTrigger.OnConnect), "a new script is added to the list and saved in the profile");
            evidence.Capture(view, "scripts", new { theme = "Light", fixtureOnly = true });
            view.ConfirmOverride = _ => true; view.Table.SelectedItem = view.Table.Items.Cast<ScriptRow>().Single(r => r.Name == "Перезапуск nginx"); Pump();
            Cmd("Remove").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            for (int i = 0; i < 50 && view.Table.Items.Count > 2; i++) Pump();
            check(view.Table.Items.Count == 2 && store.Profiles.Single(p => p.Id == profile.Id).Scripts.All(s => s.Name != "Перезапуск nginx"), "removing a script removes it from the profile");
        }
        finally { host.Close(); runner.Dispose(); }

        var editor = new ScriptEditorWindow(new SavedScript { Name = "Проверка", Body = "uptime" }) { ShowInTaskbar = false, Left = 80, Top = 60 };
        editor.Show(); Layout(editor);
        try
        {
            var trigger = Descendants<ComboBox>(editor).Single(c => c.Name == "ScriptTrigger"); var daily = Descendants<TextBox>(editor).Single(b => b.Name == "ScriptDaily");
            var interval = Descendants<TextBox>(editor).Single(b => b.Name == "ScriptInterval");
            check(!daily.IsVisible && !interval.IsVisible, "a manual script hides schedule fields");
            trigger.SelectedIndex = (int)ScriptTrigger.Daily; Pump(); Layout(editor);
            check(daily.IsVisible && !interval.IsVisible, "choosing «Ежедневно» shows only the time field");
            daily.Text = "25:61"; check(editor.Build(out var problems) is null && problems.Any(p => p.Contains("ЧЧ:ММ", StringComparison.Ordinal)), "an invalid time is refused with a message");
            daily.Text = "7:05"; var built = editor.Build(out problems);
            check(built is { Trigger: ScriptTrigger.Daily, DailyTime: "07:05" }, "a valid time is normalised to ЧЧ:ММ: " + string.Join(" | ", problems));
            evidence.Capture((FrameworkElement)editor.Content, "script-editor", new { theme = "Light", fixtureOnly = true });
        }
        finally { editor.Close(); }
    }

    static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false); Dispatcher.PushFrame(frame); }
    static void Layout(Window w) { w.UpdateLayout(); Pump(); }
    static void Complete(Task task) { for (int i = 0; i < 500 && !task.IsCompleted; i++) { Pump(); Thread.Sleep(5); } task.GetAwaiter().GetResult(); }
    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match; foreach (var nested in Descendants<T>(child)) yield return nested; } }
}
