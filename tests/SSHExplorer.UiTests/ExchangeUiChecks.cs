using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using SSHExplorer.App;
using SSHExplorer.Core;

/// <summary>0.4: «Экспорт» on Home writes an .sshx file (with sealed passwords on request) and «Импорт» reads it back.</summary>
internal static class ExchangeUiChecks
{
    public static void Run(MainWindow main, AppStore store, CiVisualEvidence evidence, Action<bool, string> check)
    {
        var errors = new List<string>(); Ui.ErrorOverride = message => { lock (errors) errors.Add(message); };
        var folder = Path.Combine(Path.GetTempPath(), "sshx-exchange-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "servers.sshx");
        try
        {
            var a = new ServerProfile { Name = "Экспорт · прод", Host = "prod.example.invalid", Username = "deploy", SaveSecret = true, Group = "Экспорт",
                Scripts = [new SavedScript { Name = "проверка", Body = "uptime", Trigger = ScriptTrigger.OnConnect }], Tunnels = [new TunnelSpec { Name = "db", BindPort = 15432, TargetPort = 5432 }] };
            var b = new ServerProfile { Name = "Экспорт · тест", Host = "test.example.invalid", Username = "deploy", Group = "Экспорт" };
            Complete(store.SaveProfileAsync(a, "секрет прод")); Complete(store.SaveProfileAsync(b, null));

            // Export: a short passphrase is refused in place; a valid one seals the saved password.
            main.ExportPathOverride = () => file;
            main.ExportWindowOverride = w =>
            {
                w.ShowInTaskbar = false; w.Left = 60; w.Top = 60; w.Show(); Pump();
                check(w.Selected.Count == 2 && w.Rows.Count(r => r.Selected) == 2, "the export window starts with the chosen servers ticked: " + w.Selected.Count);
                w.UseSecrets(true); w.SetPassphrase("1234567", "1234567"); Pump();
                check(!w.SaveButton.IsEnabled && w.Problem!.Contains("не короче", StringComparison.Ordinal), "a short passphrase blocks saving with a reason: " + w.Problem);
                w.SetPassphrase("фраза для переноса", "фраза для переноса"); Pump();
                check(w.SaveButton.IsEnabled && w.Problem is null, "a valid passphrase allows saving");
                evidence.Capture((FrameworkElement)w.Content, "export-window", new { theme = "Light", fixtureOnly = true });
                var options = w.Options; w.Close(); Pump();
                check(w.Chosen.Passphrase == "фраза для переноса" && options.IncludeSecrets && options.IncludeHostKeys, "the choices survive closing the window");
                return true;
            };
            int exported = Complete(main.ExportProfilesAsync([a.Id, b.Id]));
            var text = File.ReadAllText(file);
            check(exported == 2 && text.Contains("sshexplorer-export", StringComparison.Ordinal) && !text.Contains("секрет прод", StringComparison.Ordinal) && text.Contains("\"Seal\"", StringComparison.Ordinal),
                "the file holds both servers and the password only sealed");

            // Import back: both match existing servers; one is added as a copy with its password, one replaces.
            main.ImportPathOverride = () => file;
            main.ImportWindowOverride = w =>
            {
                w.ShowInTaskbar = false; w.Left = 60; w.Top = 60; w.Show(); Pump();
                check(w.Rows.Count == 2 && w.Rows.All(r => r.Choice == ExchangeRow.Skip) && !w.AcceptButton.IsEnabled, "servers that already exist start as «Пропустить»");
                w.EnterPassphrase("не та фраза"); Pump();
                check(!w.SecretsOpened && w.SecretState.Contains("Неверная", StringComparison.Ordinal), "a wrong passphrase is said in place");
                w.EnterPassphrase("фраза для переноса"); Pump();
                var prod = w.Rows.Single(r => r.Name == a.Name); var test = w.Rows.Single(r => r.Name == b.Name);
                check(w.SecretsOpened && prod.Candidate.Secret == "секрет прод" && prod.Notes.Contains("приостановленными", StringComparison.Ordinal), "the right passphrase opens the password; the automatic script arrives paused");
                prod.Choice = ExchangeRow.KeepBoth; test.Choice = ExchangeRow.Replace; Pump();
                check(w.AcceptButton.IsEnabled && (string)w.AcceptButton.Content == "Импортировать (2)", "choices enable the import");
                evidence.Capture((FrameworkElement)w.Content, "import-sshx-window", new { theme = "Light", fixtureOnly = true });
                w.Close(); Pump(); return true;
            };
            int before = store.Profiles.Count;
            int imported = Complete(main.ImportExchangeAsync());
            var copy = store.Profiles.SingleOrDefault(p => p.Name == a.Name + " (импорт)");
            check(imported == 2 && store.Profiles.Count == before + 1 && copy is { SaveSecret: true } && copy.Scripts.Single().Enabled == false && store.Profiles.Count(p => p.Host == b.Host) == 1,
                $"a copy and a replacement are saved: {imported} imported, {store.Profiles.Count - before} new");
            check(copy is not null && Complete(store.ReadSecretAsync(copy.Id)) == "секрет прод", "the copy's password is saved with Windows protection on this computer");

            // A foreign file is refused with a reason, nothing changes.
            File.WriteAllText(file, "{\"Format\":\"other\"}"); int count = store.Profiles.Count;
            lock (errors) errors.Clear();
            check(Complete(main.ImportExchangeAsync()) == 0 && store.Profiles.Count == count && errors.Count == 1 && errors[0].Contains("не файл экспорта", StringComparison.Ordinal), "a foreign file is refused: " + string.Join(" | ", errors));
            lock (errors) errors.Clear();
            foreach (var p in store.Profiles.Where(p => p.Group == "Экспорт").ToArray()) Complete(store.DeleteProfileAsync(p.Id));
        }
        finally
        {
            main.ExportPathOverride = main.ImportPathOverride = null; main.ExportWindowOverride = null; main.ImportWindowOverride = null; Ui.ErrorOverride = null;
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
        lock (errors) check(errors.Count == 0, "no unexpected error dialog during export and import" + (errors.Count > 0 ? ": " + string.Join(" | ", errors) : ""));
    }

    static T Complete<T>(Task<T> task) { var watch = System.Diagnostics.Stopwatch.StartNew(); while (!task.IsCompleted) { if (watch.Elapsed > TimeSpan.FromSeconds(60)) throw new TimeoutException("Export/import did not finish."); Pump(); } return task.GetAwaiter().GetResult(); }
    static void Complete(Task task) => Complete(task.ContinueWith(t => { t.GetAwaiter().GetResult(); return 0; }, TaskScheduler.Default));
    static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false); Dispatcher.PushFrame(frame); }
}
