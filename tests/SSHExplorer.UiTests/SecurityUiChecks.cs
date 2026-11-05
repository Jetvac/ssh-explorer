using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using SSHExplorer.App;
using SSHExplorer.Core;

/// <summary>0.4: «Защита SSH» — keys, key login and sshd findings with fixes, on fixture executors (no server).</summary>
internal static class SecurityUiChecks
{
    const string Sshd = "#user deploy 1000\n#effective\nport 22\npermitrootlogin yes\npasswordauthentication yes\nkbdinteractiveauthentication no\npubkeyauthentication yes\nmaxauthtries 6\nx11forwarding no\nlogingracetime 30\nusepam yes\nallowusers deploy\n#include\nyes\n#ours\n#end\n";

    public static void Run(AppStore store, CiVisualEvidence evidence, Action<bool, string> check)
    {
        var errors = new List<string>(); Ui.ErrorOverride = message => { lock (errors) errors.Add(message); };
        var profile = new ServerProfile { Name = "Защита · фикстура", Host = "sec-fixture.example.invalid", Username = "deploy" };
        store.Profiles.Add(profile);
        var existing = SshKeys.GenerateEd25519("office");
        string keysFile = existing.PublicLine + "\n";
        var executed = new List<string>(); var uploaded = new List<string>();
        var keys = new KeyLoginService(new ServerSession(profile, store), store)
        {
            ExecuteOverride = (command, _) =>
            {
                lock (executed) executed.Add(command);
                if (command == SshKeys.ListCommand) return Task.FromResult(new CommandResult(keysFile + "#end\n", "", 0));
                if (command.Contains(">> ~/.ssh/authorized_keys", StringComparison.Ordinal)) { keysFile +="x\n"; return Task.FromResult(new CommandResult("#added\n", "", 0)); }
                return Task.FromResult(new CommandResult("#removed\n", "", 0));
            },
            VerifyOverride = (_, _, _) => Task.FromResult<string?>(null),
        };
        var sshd = new SshdService(keys.Session)
        {
            ExecuteOverride = (command, _) => Task.FromResult(new CommandResult(command == Ufw.RightsCommand ? "sudo" : command.Contains("sshd -T", StringComparison.Ordinal) ? Sshd : command.Contains("/tmp/.sshx-sshd-", StringComparison.Ordinal) ? "#timer systemd\n#applied\n" : "#confirmed", "", 0)),
            UploadOverride = (_, text, _) => { uploaded.Add(text); return Task.CompletedTask; },
            VerifyOverride = _ => Task.FromResult(true),
        };
        var confirmations = new List<string>();
        var view = new SecurityView(keys) { SshdFactory = () => sshd, ConfirmOverride = t => { confirmations.Add(t); return true; } };
        var host = new Window { Width = 1180, Height = 820, Content = view, ShowInTaskbar = false, Left = 60, Top = 60 }; host.Show(); Layout(host);
        try
        {
            for (int i = 0; i < 80 && view.Findings.Count == 0; i++) Pump();
            check(view.Rows.Count == 1 && view.Rows[0].Comment == "office" && view.LoginText.StartsWith("По паролю", StringComparison.Ordinal), "the page lists the server's keys and says how this profile logs in: " + view.LoginText);
            var password = view.Findings.Single(f => f.Id == "password");
            var fixPassword = Descendants<Button>(view).Single(b => b.Name == "Fix_password"); var fixRoot = Descendants<Button>(view).Single(b => b.Name == "Fix_root");
            check(view.Findings[0].Id == "password" && password.Blocked is not null && !fixPassword.IsEnabled && fixRoot.IsEnabled && view.SshdStateText.Contains("sshd -T", StringComparison.Ordinal),
                "findings come most important first; turning passwords off is blocked while this profile logs in by password");
            evidence.Capture((FrameworkElement)view, "security", new { theme = "Light", fixtureOnly = true });
            fixRoot.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            for (int i = 0; i < 80 && !view.StateText.StartsWith("Применено", StringComparison.Ordinal); i++) Pump();
            check(confirmations.Count == 1 && confirmations[0].Contains("PermitRootLogin no", StringComparison.Ordinal) && uploaded.Count == 1 && uploaded[0].Contains("PermitRootLogin no", StringComparison.Ordinal) && uploaded[0].Contains("sshd -t", StringComparison.Ordinal)
                && view.StateText.StartsWith("Применено и подтверждено", StringComparison.Ordinal), "a fix shows what is written, runs as a safe apply and is confirmed by a new connection: " + view.StateText);

            // Adding a private key by mistake is refused with the reason.
            view.PastePublicOverride = () => "-----BEGIN OPENSSH PRIVATE KEY-----\nAAAA\n-----END OPENSSH PRIVATE KEY-----";
            var expected = new List<string>(); Ui.ErrorOverride = expected.Add;
            Descendants<Button>(view).Single(b => b.Name == "SecurityAddKey").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            for (int i = 0; i < 30 && expected.Count == 0; i++) Pump();
            check(expected.Count == 1 && expected[0].Contains("закрытый ключ", StringComparison.Ordinal), "a private key pasted as public is refused: " + string.Join(" | ", expected));
            Ui.ErrorOverride = message => { lock (errors) errors.Add(message); };
        }
        finally { host.Close(); view.Dispose(); }

        // Key login: a new key is made, installed, verified and the profile switched.
        var name = "sshx-ci-" + Guid.NewGuid().ToString("N")[..8];
        ServerProfile? saved = null; string? savedSecret = "none";
        var window = new KeyLoginWindow(keys, profile, (p, secret) => { saved = p; savedSecret = secret; return Task.CompletedTask; }) { ShowInTaskbar = false, Left = 60, Top = 60 };
        window.Show(); Layout(window);
        try
        {
            window.SetNew("bad/name"); Pump();
            check(!window.RunButton.IsEnabled && window.Problem!.Contains("недопустимые", StringComparison.Ordinal), "a bad file name is refused in place");
            window.SetNew(name); Pump();
            Wait(window.RunAsync());
            var path = Path.Combine(LocalKeys.Directory, name);
            check(File.Exists(path) && File.Exists(path + ".pub") && window.Log.Contains("Ключ создан", StringComparison.Ordinal) && window.Log.Contains("добавлен", StringComparison.Ordinal) && window.Log.Contains("Вход по ключу работает", StringComparison.Ordinal),
                "the key is created here, installed and a new key login verified: " + window.Log.Replace("\n", " / "));
            check(window.Switched && saved is { Authentication: AuthenticationKind.PrivateKey, SaveSecret: false } && saved.PrivateKeyPath == path && savedSecret is null && saved.Id == profile.Id, "the profile is switched to the key only after the check");
            evidence.Capture((FrameworkElement)window.Content, "key-login", new { theme = "Light", fixtureOnly = true });
            var installs = executed.Count(c => c.Contains(">> ~/.ssh/authorized_keys", StringComparison.Ordinal));
            check(installs == 1, "the public key was sent once");
        }
        finally
        {
            window.Close();
            foreach (var f in new[] { Path.Combine(LocalKeys.Directory, name), Path.Combine(LocalKeys.Directory, name + ".pub") }) try { File.Delete(f); } catch (IOException) { }
            store.Profiles.Remove(profile); Ui.ErrorOverride = null;
        }
        // A failed key login leaves the profile alone.
        keys.VerifyOverride = (_, _, _) => Task.FromResult<string?>("Permission denied (publickey).");
        var name2 = "sshx-ci-" + Guid.NewGuid().ToString("N")[..8]; bool switched = false;
        var failing = new KeyLoginWindow(keys, profile, (_, _) => { switched = true; return Task.CompletedTask; }) { ShowInTaskbar = false, Left = 60, Top = 60 };
        failing.Show(); Layout(failing);
        try
        {
            failing.SetNew(name2); Wait(failing.RunAsync());
            check(!switched && !failing.Switched && failing.Log.Contains("не прошёл", StringComparison.Ordinal) && failing.Problem!.Contains("Профиль не изменён", StringComparison.Ordinal), "a failed key login does not touch the profile: " + failing.Problem);
        }
        finally { failing.Close(); foreach (var f in new[] { Path.Combine(LocalKeys.Directory, name2), Path.Combine(LocalKeys.Directory, name2 + ".pub") }) try { File.Delete(f); } catch (IOException) { } }
        lock (errors) check(errors.Count == 0, "no unexpected error dialog on «Защита SSH»" + (errors.Count > 0 ? ": " + string.Join(" | ", errors) : ""));
    }

    static void Wait(Task task) { var watch = System.Diagnostics.Stopwatch.StartNew(); while (!task.IsCompleted) { if (watch.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("security action"); Pump(); } task.GetAwaiter().GetResult(); }
    static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false); Dispatcher.PushFrame(frame); }
    static void Layout(Window w) { w.UpdateLayout(); Pump(); }
    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match; foreach (var nested in Descendants<T>(child)) yield return nested; } }
}
