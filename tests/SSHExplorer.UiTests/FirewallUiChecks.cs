using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using SSHExplorer.App;
using SSHExplorer.Core;

/// <summary>0.3.1: the firewall page, rule editor and «only through a jump server» wizard on a fixture executor (no server).</summary>
internal static class FirewallUiChecks
{
    const string Fixture = "#client 203.0.113.10 51234 22\n#status\nStatus: active\nLogging: on (low)\nDefault: deny (incoming), allow (outgoing), disabled (routed)\n#added\nAdded user rules (see 'ufw status' for running firewall):\nufw allow 22/tcp\nufw allow from 10.0.0.0/8 to any port 5432 proto tcp comment 'office db'\n#neighbours\ndocker\n";

    public static void Run(AppStore store, CiVisualEvidence evidence, Action<bool, string> check)
    {
        var errors = new List<string>(); Ui.ErrorOverride = message => { lock (errors) errors.Add(message); };
        try { RunCore(store, evidence, check, errors); }
        finally { Ui.ErrorOverride = null; }
        lock (errors) check(errors.Count == 0, "no error dialog was raised during the firewall checks" + (errors.Count > 0 ? ": " + string.Join(" | ", errors) : ""));
    }

    static void RunCore(AppStore store, CiVisualEvidence evidence, Action<bool, string> check, List<string> errors)
    {
        var snapshot = Ufw.Parse(Fixture);

        // Rule editor: presets, validation, the exact command and the warning when it would cut this SSH connection.
        var editor = new FirewallRuleWindow(snapshot) { ShowInTaskbar = false, Left = 60, Top = 60 };
        editor.Show(); Layout(editor);
        try
        {
            T Find<T>(string name) where T : FrameworkElement => Descendants<T>(editor).Single(x => x.Name == name);
            Find<ComboBox>("RulePreset").SelectedIndex = 4; Pump();
            check(Find<TextBox>("RulePort").Text == "80,443" && Find<Button>("RuleSave").IsEnabled && editor.Preview == "ufw allow in proto tcp from 'any' to any port '80,443'", "the web preset fills ports and shows the exact command: " + editor.Preview);
            Find<RadioButton>("RuleFromSome").IsChecked = true; Find<TextBox>("RuleFrom").Text = "10.0.0.300"; Pump();
            check(!Find<Button>("RuleSave").IsEnabled && editor.Problem!.Contains("Откуда", StringComparison.Ordinal), "a bad address blocks saving with a reason");
            Find<ComboBox>("RulePreset").SelectedIndex = 1; Find<ComboBox>("RuleAction").SelectedIndex = 1; Find<TextBox>("RuleFrom").Text = "203.0.113.0/24"; Find<CheckBox>("RuleFirst").IsChecked = true; Find<CheckBox>("RuleFirst").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump();
            check(editor.Preview.StartsWith("ufw prepend deny in proto tcp from '203.0.113.0/24' to any port '22'", StringComparison.Ordinal) && editor.Verdict.StartsWith("⚠", StringComparison.Ordinal), "a rule that would block this connection is flagged before saving: " + editor.Verdict);
            evidence.Capture((FrameworkElement)editor.Content, "firewall-rule", new { theme = "Light", fixtureOnly = true });
        }
        finally { editor.Close(); }

        // Page: rules, access line, safe apply through the fixture executor.
        var profile = new ServerProfile { Name = "Файервол · фикстура", Host = "fw-fixture.example.invalid", Username = "fixture" };
        var service = new FirewallService(new ServerSession(profile, store));
        var sent = new List<string>(); var uploaded = new List<string>(); var confirmations = new List<string>();
        service.ExecuteOverride = (command, _) =>
        {
            lock (sent) sent.Add(command);
            var output = command == Ufw.RightsCommand ? "sudo" : command.Contains("ufw status verbose", StringComparison.Ordinal) ? Fixture
                : command.StartsWith("sudo -n sh '/tmp/.sshx-ufw-", StringComparison.Ordinal) ? "Rule added\n#timer systemd\n#applied\n" : "#confirmed";
            return Task.FromResult(new CommandResult(output, "", 0));
        };
        service.UploadOverride = (_, text, _) => { uploaded.Add(text); return Task.CompletedTask; };
        service.VerifyOverride = _ => Task.FromResult(true);
        var view = new FirewallView(service) { ConfirmOverride = t => { confirmations.Add(t); return true; }, EditOverride = _ => new RuleDraft { Port = "8443", Comment = "panel" } };
        var host = new Window { Width = 1180, Height = 760, Content = view, ShowInTaskbar = false, Left = 60, Top = 60 }; host.Show(); Layout(host);
        try
        {
            for (int i = 0; i < 50 && view.Table.Items.Count == 0; i++) Pump();
            check(view.Table.Items.Count == 2 && view.AccessText.Contains("203.0.113.10", StringComparison.Ordinal) && view.AccessText.Contains("Разрешено правилом «allow 22/tcp»", StringComparison.Ordinal), "rules are listed and this connection's access is explained: " + view.AccessText);
            check(Descendants<TextBlock>(view).Single(b => b.Name == "FirewallWarnings").Text.Contains("Docker", StringComparison.Ordinal), "Docker bypassing ufw is pointed out");
            Descendants<Button>(view).Single(b => b.Name == "FirewallAdd").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            for (int i = 0; i < 50 && !view.StateText.StartsWith("Применено", StringComparison.Ordinal); i++) Pump();
            check(confirmations.Count == 1 && confirmations[0].Contains("ufw prepend", StringComparison.Ordinal) == false && confirmations[0].Contains("ufw allow in proto tcp from 'any' to any port '8443' comment 'panel'", StringComparison.Ordinal)
                && confirmations[0].Contains(FirewallService.RollbackSeconds + " с", StringComparison.Ordinal), "adding shows the command, the predicted access and the automatic restore");
            check(uploaded.Count == 1 && uploaded[0].Contains("systemd-run", StringComparison.Ordinal) && sent.Any(c => c.Contains("#confirmed", StringComparison.Ordinal) || c.Contains("rm -rf '/var/tmp/sshx-ufw-", StringComparison.Ordinal)) && view.StateText.StartsWith("Применено и подтверждено", StringComparison.Ordinal),
                "the change runs as a safe-apply script, is checked by a new connection and confirmed: " + view.StateText);
            evidence.Capture((FrameworkElement)view, "firewall", new { theme = "Light", fixtureOnly = true });
            // A failing check restores at once and says so.
            // The restore is reported with an error dialog on purpose: collect it separately from unexpected ones.
            var expected = new List<string>(); Ui.ErrorOverride = expected.Add;
            service.VerifyOverride = _ => Task.FromResult(false); var errorsBefore = confirmations.Count;
            view.Table.SelectedIndex = 1; Pump();
            Descendants<Button>(view).Single(b => b.Name == "FirewallRemove").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            for (int i = 0; i < 50 && !view.StateText.StartsWith("Новое SSH-подключение", StringComparison.Ordinal); i++) Pump();
            check(confirmations.Count == errorsBefore + 1 && confirmations[^1].Contains("ufw --force delete 'allow' 'from' '10.0.0.0/8'", StringComparison.Ordinal) && sent.Any(c => c.Contains("rollback.sh", StringComparison.Ordinal)) && view.StateText.Contains("восстановлены", StringComparison.Ordinal),
                "when the new connection fails the old rules are restored: " + view.StateText);
            check(expected.Count == 1 && expected[0].Contains("восстановлены", StringComparison.Ordinal), "the restore is reported to the user: " + string.Join(" | ", expected));
        }
        finally { Ui.ErrorOverride = message => { lock (errors) errors.Add(message); }; host.Close(); view.Dispose(); }

        // Wizard: a direct connection cannot use it; through a jump server the plan keeps SSH only for it.
        var direct = new BastionWizardWindow(snapshot, null, "", []) { ShowInTaskbar = false, Left = 60, Top = 60 };
        direct.Show(); Layout(direct);
        try { check(!Descendants<Button>(direct).Single(b => b.Name == "BastionApply").IsEnabled && direct.Problem!.Contains("через промежуточный", StringComparison.Ordinal), "the wizard refuses a direct connection and explains what to set first"); }
        finally { direct.Close(); }
        var wizard = new BastionWizardWindow(snapshot, "bastion", "bastion", []) { ShowInTaskbar = false, Left = 60, Top = 60 };
        wizard.Show(); Layout(wizard);
        try
        {
            var apply = Descendants<Button>(wizard).Single(b => b.Name == "BastionApply");
            check(!apply.IsEnabled && wizard.Preview.Contains("from '203.0.113.10' to any port '22'", StringComparison.Ordinal) && wizard.Preview.Contains("ufw --force delete 'allow' '22/tcp'", StringComparison.Ordinal), "the plan is shown before the confirmation tick: " + wizard.Preview);
            check(wizard.Verdicts.Contains("Промежуточный сервер (203.0.113.10): Разрешено", StringComparison.Ordinal) && wizard.Verdicts.Contains("Любой другой адрес: Будет заблокировано", StringComparison.Ordinal), "the predicted access for the jump server and for others: " + wizard.Verdicts);
            var tick = Descendants<CheckBox>(wizard).Single(c => c.Name == "BastionUnderstood"); tick.IsChecked = true; tick.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump();
            check(apply.IsEnabled && wizard.Commands.Count == 3, "after the tick the plan can be applied");
            var keep = Descendants<CheckBox>(wizard).Single(c => c.Content as string == "allow 22/tcp"); keep.IsChecked = false; keep.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump();
            check(!apply.IsEnabled && wizard.Problem!.Contains("останется открыт", StringComparison.Ordinal), "keeping a rule that opens SSH to everyone blocks the plan: " + wizard.Problem);
            evidence.Capture((FrameworkElement)wizard.Content, "firewall-bastion", new { theme = "Light", fixtureOnly = true });
        }
        finally { wizard.Close(); }
    }

    static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false); Dispatcher.PushFrame(frame); }
    static void Layout(Window w) { w.UpdateLayout(); Pump(); }
    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match; foreach (var nested in Descendants<T>(child)) yield return nested; } }
}
