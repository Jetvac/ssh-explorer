using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using SSHExplorer.App;
using SSHExplorer.Core;

/// <summary>0.3 D1/D3 on fixtures: split layout with plain panes (no WebView2), the tunnel editor and the tunnels page.</summary>
internal static class GroupDUiChecks
{
    public static void Run(AppStore store, CiVisualEvidence evidence, Action<bool, string> check)
    {
        var errors = new List<string>(); Ui.ErrorOverride = message => { lock (errors) errors.Add(message); };
        try { RunCore(store, evidence, check); }
        finally { Ui.ErrorOverride = null; }
        lock (errors) check(errors.Count == 0, "no error dialog was raised during the split/tunnel checks" + (errors.Count > 0 ? ": " + string.Join(" | ", errors) : ""));
    }

    static void RunCore(AppStore store, CiVisualEvidence evidence, Action<bool, string> check)
    {
        // D1: split right, split down, up to four panes; closing gives the space back to the neighbour.
        int made = 0; var released = new List<string>(); var added = new List<string>();
        var workspace = new SplitWorkspace(() => new Border { Name = "Body" + (++made), Child = new TextBlock { Text = "pane " + made } }, body => released.Add(body.Name));
        workspace.PaneAdded += body => added.Add(body.Name);
        var host = new Window { Width = 1100, Height = 700, Content = workspace, ShowInTaskbar = false, Left = 60, Top = 60 }; host.Show();
        try
        {
            var first = workspace.Start(); Layout(host);
            check(workspace.Count == 1 && !workspace.Panes[0].Close.IsEnabled, "one pane cannot be closed");
            workspace.Panes[0].SplitRight.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Layout(host);
            var a = workspace.Panes[0]; var b = workspace.Panes[1];
            check(workspace.Count == 2 && ReferenceEquals(workspace.ActiveBody, b.Body) && added.SequenceEqual(["Body2"]) && a.TranslatePoint(new Point(), host).X < b.TranslatePoint(new Point(), host).X - 300
                && Math.Abs(a.TranslatePoint(new Point(), host).Y - b.TranslatePoint(new Point(), host).Y) < 1, "«split right» puts a new active pane beside the first");
            b.SplitDown.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Layout(host);
            var c = workspace.Panes[2];
            check(workspace.Count == 3 && c.TranslatePoint(new Point(), host).Y > b.TranslatePoint(new Point(), host).Y + 150 && Math.Abs(c.TranslatePoint(new Point(), host).X - b.TranslatePoint(new Point(), host).X) < 1, "«split down» stacks the new pane under the split one");
            workspace.Shortcut(a.Body, "E"); Layout(host);
            check(workspace.Count == SplitWorkspace.MaxPanes && workspace.Panes.All(p => !p.SplitRight.IsEnabled && !p.SplitDown.IsEnabled && p.Close.IsEnabled), "the fourth pane (Ctrl+Shift+E) disables further splits");
            workspace.Shortcut(a.Body, "D"); Layout(host);
            check(workspace.Count == SplitWorkspace.MaxPanes, "no fifth pane");
            evidence.Capture((FrameworkElement)host.Content, "terminal-split", new { theme = "Light", fixtureOnly = true, panes = workspace.Count });
            b.Close.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Layout(host);
            check(workspace.Count == 3 && released.SequenceEqual(["Body2"]) && c.ActualHeight > 600 && workspace.Panes.All(p => p.SplitRight.IsEnabled), "closing a pane releases it and its neighbour takes the space: " + c.ActualHeight.ToString("0"));
            workspace.Shortcut(c.Body, "W"); workspace.Shortcut(workspace.Panes[0].Body, "W"); Layout(host);
            check(workspace.Count == 1 && workspace.Panes[0].ActualWidth > 1000 && released.Count == 3, "closing down to one pane restores the full area");
            workspace.Shortcut(workspace.Panes[0].Body, "W");
            check(workspace.Count == 1, "the last pane stays");
        }
        finally { host.Close(); workspace.Dispose(); }

        // D3: editor — loopback by default, a public address needs a tick, remote needs a port.
        var editor = new TunnelEditorWindow(new TunnelSpec()) { ShowInTaskbar = false, Left = 60, Top = 60 };
        editor.Show(); Layout(editor);
        try
        {
            TextBox Box(string name) => Descendants<TextBox>(editor).Single(x => x.Name == name);
            var save = Descendants<Button>(editor).Single(x => x.Name == "TunnelSave");
            check(!save.IsEnabled && editor.Problem is not null, "an empty target blocks saving");
            Box("TunnelBindPort").Text = "15432"; Box("TunnelTargetHost").Text = "db.internal"; Box("TunnelTargetPort").Text = "5432"; Pump();
            check(save.IsEnabled && editor.Preview.StartsWith("127.0.0.1:15432 на компьютере → db.internal:5432", StringComparison.Ordinal), "a loopback local tunnel is valid and described: " + editor.Preview);
            Box("TunnelBindAddress").Text = "0.0.0.0"; Pump();
            check(!save.IsEnabled && editor.Problem!.Contains("других компьютеров", StringComparison.Ordinal), "listening on all interfaces needs an explicit confirmation");
            Descendants<CheckBox>(editor).Single(x => x.Name == "TunnelExposed").IsChecked = true; Descendants<CheckBox>(editor).Single(x => x.Name == "TunnelExposed").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump();
            check(save.IsEnabled, "after the confirmation the exposed tunnel can be saved");
            Box("TunnelBindAddress").Text = "127.0.0.1"; Descendants<RadioButton>(editor).Single(x => x.Name == "TunnelRemote").IsChecked = true; Box("TunnelBindPort").Text = ""; Pump();
            check(!save.IsEnabled && editor.Problem!.Contains("сервере", StringComparison.Ordinal), "a remote tunnel needs a port on the server: " + editor.Problem);
            evidence.Capture((FrameworkElement)editor.Content, "tunnel-editor", new { theme = "Light", fixtureOnly = true });
        }
        finally { editor.Close(); }

        // D3: the page on an unconnected session — saved tunnels are listed, opening is off and explained.
        var profile = new ServerProfile { Name = "Туннели · фикстура", Host = "tunnels-fixture.example.invalid", Username = "fixture" };
        profile.Tunnels.Add(new TunnelSpec { Name = "Postgres", BindPort = 15432, TargetHost = "127.0.0.1", TargetPort = 5432, StartOnConnect = true });
        Complete(store.SaveProfileAsync(profile, null));
        var session = new ServerSession(profile, store);
        var view = new TunnelsView(session, new TunnelManager(session)) { EditOverride = s => { s.Name = "Web"; s.BindPort = 18080; s.TargetPort = 80; return s; }, ConfirmOverride = _ => true };
        var page = new Window { Width = 1180, Height = 640, Content = view, ShowInTaskbar = false, Left = 60, Top = 60 }; page.Show(); Layout(page);
        try
        {
            var rows = view.Table.Items.Cast<TunnelRow>().ToArray();
            check(rows.Length == 1 && rows[0].Route == "127.0.0.1:15432 на компьютере → 127.0.0.1:5432 со стороны сервера" && rows[0].StateText == "Закрыт" && rows[0].AutoText == "При подключении", "saved tunnels are listed with route, state and start mode");
            check(!Descendants<Button>(view).Single(b => b.Name == "TunnelsOpen").IsEnabled && view.StateText.Contains("не подключён", StringComparison.Ordinal), "opening is off while the server is not connected, and the footer says why");
            Descendants<Button>(view).Single(b => b.Name == "TunnelsAdd").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); for (int i = 0; i < 300 && (view.Table.Items.Count < 2 || profile.Tunnels.Count < 2); i++) Pump();
            check(view.Table.Items.Count == 2 && profile.Tunnels.Count == 2 && store.Profiles.Single(p => p.Id == profile.Id).Tunnels.Any(t => t.Name == "Web"), "a new tunnel is saved to the profile: " + view.Table.Items.Count + "/" + profile.Tunnels.Count + "/" + (store.Profiles.SingleOrDefault(p => p.Id == profile.Id)?.Tunnels.Count.ToString() ?? "нет профиля") + " · " + view.StateText);
            evidence.Capture((FrameworkElement)view, "tunnels", new { theme = "Light", fixtureOnly = true });
            Descendants<Button>(view).Single(b => b.Name == "TunnelsRemove").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); for (int i = 0; i < 20 && view.Table.Items.Count > 1; i++) Pump();
            check(view.Table.Items.Count == 1 && store.Profiles.Single(p => p.Id == profile.Id).Tunnels.Count == 1, "removing a tunnel updates the saved profile");
        }
        finally { page.Close(); view.Dispose(); }
    }

    static void Complete(Task task) { var frame = new DispatcherFrame(); task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default); Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult(); }
    static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false); Dispatcher.PushFrame(frame); }
    static void Layout(Window w) { w.UpdateLayout(); Pump(); }
    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match; foreach (var nested in Descendants<T>(child)) yield return nested; } }
}
