using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using SSHExplorer.App;
using SSHExplorer.Core;

/// <summary>0.3 G: Docker view on a fixture executor (no server) and the run-parameters editor with real DPAPI.</summary>
internal static class DockerUiChecks
{
    public static void Run(AppStore store, CiVisualEvidence evidence, Action<bool, string> check)
    {
        var profile = new ServerProfile { Name = "Docker · фикстура", Host = "docker-fixture.example.invalid", Username = "fixture" };
        Complete(store.SaveProfileAsync(profile, null));
        var service = new DockerService(new ServerSession(profile, store));
        var list = string.Join('\n',
            """{"ID":"aaaaaaaaaaaaaaaa","Image":"nginx:1.27","Names":"web","Ports":"0.0.0.0:8080->80/tcp","State":"exited","Status":"Exited (0) 2 days ago","CreatedAt":"2026-11-01","Command":"\"nginx\""}""",
            """{"ID":"bbbbbbbbbbbbbbbb","Image":"postgres:16","Names":"db","Ports":"5432/tcp","State":"running","Status":"Up 3 hours","CreatedAt":"2026-11-02","Command":"\"postgres\""}""");
        var commands = new List<string>();
        service.ExecuteOverride = (command, _) =>
        {
            lock (commands) commands.Add(command);
            string output = command.StartsWith("if ! command -v docker", StringComparison.Ordinal) ? "#docker direct 27.3.1"
                : command.Contains(" ps -a ", StringComparison.Ordinal) ? list
                : command.Contains(" logs ", StringComparison.Ordinal) ? "2026-11-04T10:00:00Z database system is ready to accept connections\n" : "";
            return Task.FromResult(new CommandResult(output, "", 0));
        };
        var view = new DockerView(service);
        var host = new Window { Width = 1180, Height = 760, Content = view, ShowInTaskbar = false, Left = 60, Top = 60 }; host.Show(); Layout(host);
        try
        {
            for (int i = 0; i < 50 && view.Table.Items.Count < 2; i++) Pump();
            var names = view.Table.Items.Cast<DockerContainer>().Select(c => c.Name).ToArray();
            check(names.SequenceEqual(["db", "web"]), "Docker lists containers with running ones first: " + string.Join(",", names));
            Button Cmd(string key) => Descendants<Button>(view).Single(b => b.Name == "Docker" + key);
            view.Table.SelectedItem = view.Table.Items.Cast<DockerContainer>().Single(c => c.Name == "db"); Pump(); Layout(host);
            check(Cmd("Stop").IsEnabled && Cmd("Restart").IsEnabled && !Cmd("Start").IsEnabled && !Cmd("Recreate").IsEnabled, "a running container offers stop/restart; recreate needs saved parameters first");
            var log = Descendants<TextBox>(view).Single(b => b.Name == "DockerLog");
            for (int i = 0; i < 50 && !log.Text.Contains("ready", StringComparison.Ordinal); i++) Pump();
            bool tailed; lock (commands) tailed = commands.Any(c => c.Contains(" logs --timestamps --tail 200 'bbbbbbbbbbbbbbbb'", StringComparison.Ordinal));
            check(log.Text.Contains("ready to accept connections", StringComparison.Ordinal) && tailed, "selecting a container shows its quoted, tail-limited log");
            var search = Descendants<TextBox>(view).Single(b => b.Name == "DockerSearch"); search.Text = "nginx"; Pump();
            check(view.Table.Items.Count == 1 && ((DockerContainer)view.Table.Items[0]).Name == "web", "search filters containers by image"); search.Text = ""; Pump();
            view.Table.SelectedItem = view.Table.Items.Cast<DockerContainer>().Single(c => c.Name == "db"); Pump();
            view.EditOverride = (_, _, _) => new DockerTemplateWindow.Result(new DockerTemplate { ContainerName = "db", Image = "postgres:16", RestartPolicy = "always", Env = [new DockerEnv { Key = "POSTGRES_DB", Value = "app" }] }, false);
            Cmd("Edit").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            for (int i = 0; i < 50 && !Cmd("Recreate").IsEnabled; i++) Pump();
            var saved = App.Store.Profiles.Single(p => p.Id == profile.Id).DockerTemplates.SingleOrDefault(t => t.ContainerName == "db");
            check(Cmd("Recreate").IsEnabled && saved is { RestartPolicy: "always" } && saved.Env.Single().Key == "POSTGRES_DB", "saved run parameters are stored in the profile and enable «Пересоздать с параметрами»");
            evidence.Capture(view, "docker", new { theme = "Light", fixtureOnly = true });
        }
        finally { host.Close(); view.Dispose(); }

        // Editor: problems are listed; secrets are encrypted with the real DPAPI; an untouched stored secret is kept.
        var stored = new DockerEnv { Key = "API_TOKEN" }; DockerSecrets.SetSecret(stored, "kept-secret");
        var editor = new DockerTemplateWindow(new DockerTemplate { ContainerName = "api", Image = "ghcr.io/acme/api:2", Ports = ["8080:80"], Env = [stored, new DockerEnv { Key = "MODE", Value = "prod" }] }, true, ["db"]) { ShowInTaskbar = false, Left = 80, Top = 60 };
        editor.Show(); Layout(editor);
        try
        {
            editor.EnvRows.Add(new DockerEnvRow { Key = "1BAD", Value = "x" }); editor.EnvRows.Add(new DockerEnvRow { Key = "DB_PASSWORD", Value = "s3cr3t-typed", Secret = true });
            check(editor.Build(out var problems) is null && problems.Any(p => p.Contains("«1BAD»", StringComparison.Ordinal)), "invalid env names are refused with a message: " + string.Join(" | ", problems));
            editor.EnvRows.Remove(editor.EnvRows.Single(r => r.Key == "1BAD"));
            var built = editor.Build(out problems);
            var kept = built?.Env.Single(e => e.Key == "API_TOKEN"); var typed = built?.Env.Single(e => e.Key == "DB_PASSWORD");
            check(built is not null && kept is { Secret: true, Value: "" } && DockerSecrets.ValueOf(kept) == "kept-secret" && typed is { Secret: true, Value: "" } && typed.Protected.Length > 0
                && !typed.Protected.Contains("s3cr3t", StringComparison.Ordinal) && DockerSecrets.ValueOf(typed) == "s3cr3t-typed",
                "secret env values are DPAPI-encrypted and an untouched stored secret is kept: " + string.Join(" | ", problems));
            evidence.Capture((FrameworkElement)editor.Content, "docker-template", new { theme = "Light", fixtureOnly = true });
        }
        finally { editor.Close(); }
    }

    static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false); Dispatcher.PushFrame(frame); }
    static void Layout(Window w) { w.UpdateLayout(); Pump(); }
    static void Complete(Task task) { for (int i = 0; i < 500 && !task.IsCompleted; i++) { Pump(); Thread.Sleep(5); } task.GetAwaiter().GetResult(); }
    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match; foreach (var nested in Descendants<T>(child)) yield return nested; } }
}
