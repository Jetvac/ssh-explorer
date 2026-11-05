using System.Windows;
using System.Windows.Threading;
using SSHExplorer.App;
using SSHExplorer.Core;

/// <summary>0.4: «Фрагменты» — saved terminal commands; typed lines must be single, several lines only run at once.</summary>
internal static class SnippetUiChecks
{
    public static void Run(CiVisualEvidence evidence, Action<bool, string> check)
    {
        var window = new SnippetsWindow([new CommandSnippet { Name = "Логи nginx", Command = "sudo tail -f /var/log/nginx/error.log" }]) { ShowInTaskbar = false, Left = 60, Top = 60 };
        window.Show(); Pump();
        try
        {
            check(window.List.Items.Count == 1 && window.SaveButton.IsEnabled, "saved fragments are listed");
            window.AddNew("Обновить и перезапустить", "git pull\nsystemctl restart app"); Pump();
            check(!window.SaveButton.IsEnabled && window.Problem!.Contains("Несколько строк", StringComparison.Ordinal), "several typed lines are refused: they would run one by one");
            window.AddNew("Место", "df -h", r: false); Pump();
            check(!window.SaveButton.IsEnabled, "one invalid fragment still blocks saving");
        }
        finally { window.Close(); }
        var clean = new SnippetsWindow([]) { ShowInTaskbar = false, Left = 60, Top = 60 }; clean.Show(); Pump();
        try
        {
            clean.AddNew("Обновить и перезапустить", "git pull\nsystemctl restart app", r: true); Pump();
            clean.AddNew("Место", "df -h"); Pump();
            check(clean.SaveButton.IsEnabled && clean.Result.Count == 2 && clean.Result[0].RunImmediately && clean.Result[1].Command == "df -h", "a multi-line fragment that runs at once and a typed one are saved");
            evidence.Capture((FrameworkElement)clean.Content, "snippets", new { theme = "Light", fixtureOnly = true });
        }
        finally { clean.Close(); }
        // Pageant: absent on the CI runner — the agent says so instead of hanging or crashing.
        string? pageant = null; try { Pageant.Client().ListIdentities(); } catch (SshAgentException ex) { pageant = ex.Message; }
        check(!Pageant.IsRunning && pageant is not null && pageant.Contains("Pageant", StringComparison.Ordinal), "without Pageant the agent reports it plainly: " + pageant);
        check(CommandSnippet.Validate(new CommandSnippet { Name = "x", Command = "a\nb" }) is not null && CommandSnippet.Validate(new CommandSnippet { Name = "x", Command = "a\nb", RunImmediately = true }) is null && CommandSnippet.Validate(new CommandSnippet { Name = " ", Command = "a" }) is not null, "fragment rules");
    }

    static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false); Dispatcher.PushFrame(frame); }
}
