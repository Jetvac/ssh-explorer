using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using SSHExplorer.App;
using SSHExplorer.Core;

/// <summary>0.3 B3: comparison window on a fixture report — filter, marking, grouped copy actions.</summary>
internal static class CompareUiChecks
{
    public static void Run(AppStore store, CiVisualEvidence evidence, Action<bool, string> check)
    {
        var errors = new List<string>(); Ui.ErrorOverride = message => { lock (errors) errors.Add(message); };
        try { RunCore(store, evidence, check); }
        finally { Ui.ErrorOverride = null; }
        lock (errors) check(errors.Count == 0, "no error dialog was raised during the comparison checks" + (errors.Count > 0 ? ": " + string.Join(" | ", errors) : ""));
    }

    static void RunCore(AppStore store, CiVisualEvidence evidence, Action<bool, string> check)
    {
        var t = new DateTime(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc);
        CompareEntry F(string p, long s, double sec = 0) => new(p, false, s, t.AddSeconds(sec)); CompareEntry D(string p) => new(p, true, 0, t);
        var report = new CompareReport(FolderCompare.Compare(
            [F("same.txt", 3), F("newer-local.txt", 5, 600), F("old-local.txt", 5), D("new-dir"), F("new-dir/inner.txt", 1), F("only-here.txt", 2)],
            [F("same.txt", 3), F("newer-local.txt", 5), F("old-local.txt", 5, 600), D("docs"), F("docs/only-server.txt", 9)]), ["Сервер: link — символическая ссылка не обходится"], false);
        var session = new ServerSession(new ServerProfile { Name = "Сравнение · фикстура", Host = "compare.example.invalid", Username = "fixture" }, store);
        var uploads = new List<(string[] Sources, string Target)>(); var downloads = new List<(string[] Sources, string Target)>();
        var window = new CompareWindow(session, "/srv/site", @"C:\Site") { ShowInTaskbar = false, Left = 60, Top = 60,
            CompareOverride = (_, _, _) => Task.FromResult(report), ConfirmOverride = _ => true,
            UploadOverride = (s, target) => uploads.Add((s.ToArray(), target)), DownloadOverride = (s, target) => downloads.Add((s.ToArray(), target)) };
        window.Show(); Layout(window);
        try
        {
            Button B(string name) => Descendants<Button>(window).Single(b => b.Name == name);
            B("CompareRun").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            for (int i = 0; i < 50 && window.Table.Items.Count == 0; i++) Pump();
            var shown = window.Table.Items.Cast<CompareRow>().Select(r => r.Path).ToArray();
            check(!shown.Contains("same.txt") && shown.Contains("newer-local.txt") && shown.Contains("docs/only-server.txt"), "«Только различия» hides equal files: " + string.Join(",", shown));
            check(Descendants<Expander>(window).Single(e => e.Name == "CompareSkipped").IsVisible, "skipped links are listed");
            B("CompareMark").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump();
            var marked = window.Rows.Where(r => r.Selected).Select(r => r.Result.RelativePath).Order(StringComparer.Ordinal).ToArray();
            check(marked.SequenceEqual(["docs", "docs/only-server.txt", "new-dir", "new-dir/inner.txt", "newer-local.txt", "old-local.txt", "only-here.txt"]), "marking selects every one-sided object and the newer side of changed files: " + string.Join(",", marked));
            foreach (var r in window.Rows) r.Selected = r.Result.RelativePath is "new-dir" or "new-dir/inner.txt" or "newer-local.txt" or "only-here.txt"; Pump();
            B("CompareUpload").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); for (int i = 0; i < 20 && uploads.Count == 0; i++) Pump();
            var up = uploads.SelectMany(u => u.Sources.Select(s => s + " -> " + u.Target)).Order(StringComparer.Ordinal).ToArray();
            check(up.SequenceEqual([@"C:\Site\new-dir -> /srv/site", @"C:\Site\newer-local.txt -> /srv/site", @"C:\Site\only-here.txt -> /srv/site"]), "upload sends a selected folder once (not its children again) to the right server folder: " + string.Join(" | ", up));
            foreach (var r in window.Rows) r.Selected = r.Result.RelativePath is "docs/only-server.txt" or "old-local.txt"; Pump();
            B("CompareDownload").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); for (int i = 0; i < 20 && downloads.Count == 0; i++) Pump();
            var down = downloads.SelectMany(d => d.Sources.Select(s => s + " -> " + d.Target)).Order(StringComparer.Ordinal).ToArray();
            check(down.SequenceEqual([@"/srv/site/docs/only-server.txt -> C:\Site\docs", @"/srv/site/old-local.txt -> C:\Site"]), "download puts each file into the matching local subfolder: " + string.Join(" | ", down));
            evidence.Capture((FrameworkElement)window.Content, "compare", new { theme = "Light", fixtureOnly = true });
        }
        finally { window.Close(); }
    }

    static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false); Dispatcher.PushFrame(frame); }
    static void Layout(Window w) { w.UpdateLayout(); Pump(); }
    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match; foreach (var nested in Descendants<T>(child)) yield return nested; } }
}
