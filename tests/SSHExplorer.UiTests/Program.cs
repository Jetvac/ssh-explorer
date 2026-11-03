using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SSHExplorer.App;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--resources-only") return ThemeResourceTests.Run();
        if (args.Length == 1 && args[0] == "--motion-only") return MotionTests.Run();
        if (args.Length == 1 && args[0] == "--connection-only") return ConnectionTests.Run();
        if (args.Length == 1 && args[0] == "--monitor-graph-only") return MonitorUiTests.GraphOnly();
        if (args.Length == 1 && args[0] == "--drag-payload-only") return DragPayloadTests.Run();
        if (args.Length == 1 && args[0] == "--monitor-poll-only") return MonitorPollTests.Run();
        if (args.Length == 1 && args[0] == "--file-state-only") return FileInteractionTests.StateOnly();
        if (args.Length == 1 && args[0] == "--monitor-refinement-only")
        {
            try { int count=MonitorRefinementTests.Run(Assert);Console.WriteLine($"RESULT: {count} monitor formatting/preferences/action checks passed; no Application, Window or SSH created");return 0; }
            catch(Exception ex){Console.WriteLine(ex);return 1;}
        }
        if (args.Length > 0 && args[0] == "--file-interactions-ui") return FileInteractionTests.Run(args.Skip(1).ToArray());
        if (args.Length > 0 && args[0] == "--forms-tabs-ui") return FormTabTests.Run(args.Skip(1).ToArray());
        if (args.Length == 1 && args[0] == "--monitor-state-only")
        {
            try { int count=MonitorStateTests.Run(Assert);Console.WriteLine($"RESULT: {count} monitor state/history assertions passed; no Application, Window or SSH created");return 0; }
            catch(Exception ex){Console.WriteLine(ex);return 1;}
        }
        if (args.Length > 0 && args[0] == "--monitor-ui-only") return MonitorUiTests.Run(args.Skip(1).ToArray());
        try { CiVisualEvidence.RequireCi(); }
        catch(InvalidOperationException)
        { Console.Error.WriteLine("Window checks require an isolated GitHub Actions runner; use an explicit headless mode on the host."); return 2; }
        try
        {
            var app = TestApplication.Create(); Theme.Apply("Light");
            var list = new ListBox { Width = 800, Height = 500, ItemsSource = Enumerable.Range(0, 10_000).Select(i => "item-" + i).ToList() };
            var factory = new FrameworkElementFactory(typeof(VirtualizingWrapPanel)); factory.SetValue(VirtualizingWrapPanel.ItemWidthProperty, 100d); factory.SetValue(VirtualizingWrapPanel.ItemHeightProperty, 80d); list.ItemsPanel = new ItemsPanelTemplate(factory); list.SetValue(ScrollViewer.CanContentScrollProperty, true);
            var host = new Window { Content = list, Width = 820, Height = 550, ShowInTaskbar = false, Left = -10_000, Top = -10_000 }; host.Show();
            Layout(list); var panel = Find<VirtualizingWrapPanel>(list) ?? throw new Exception("Virtualizing panel was not created.");
            Assert(Count<ListBoxItem>(list) is > 0 and < 100, "10,000 items only create visible containers");
            panel.SetVerticalOffset(90_000); Layout(list); Assert(Count<ListBoxItem>(list) is > 0 and < 100, "scroll recycles a bounded nonempty set of containers");
            list.ItemsSource = Array.Empty<string>(); Layout(list); Assert(Count<ListBoxItem>(list) == 0, "empty directory clears containers");
            list.ItemsSource = Enumerable.Range(0, 100).Select(i => "replacement-" + i).ToArray(); panel.SetVerticalOffset(0); Layout(list); Assert(Count<ListBoxItem>(list) is > 0 and < 100, "new listing after an empty directory");
            list.Width = 350; list.Height = 250; Layout(list); Assert(Count<ListBoxItem>(list) is > 0 and < 40, "resize limits realized containers");
            panel.SetVerticalOffset(-100); Layout(list); Assert(panel.VerticalOffset == 0, "negative scroll is clamped");
            var stateChecks = ViewModelTests.Run(Assert);
            var componentChecks = UiComponentTests.Run(host, Assert);
            host.Close(); app.Shutdown(); Console.WriteLine($"RESULT: 6 WPF layout/virtualization + {stateChecks} MVVM state/binding + {componentChecks} UI component checks passed"); return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
    }
    static void Layout(FrameworkElement e) { e.Measure(new Size(e.Width, e.Height)); e.Arrange(new Rect(0, 0, e.Width, e.Height)); e.UpdateLayout(); }
    static void Assert(bool condition, string description) { if (!condition) throw new Exception(description); Console.WriteLine("PASS " + description); }
    static T? Find<T>(DependencyObject parent) where T : DependencyObject { for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) { var c = VisualTreeHelper.GetChild(parent, i); if (c is T t) return t; if (Find<T>(c) is T nested) return nested; } return null; }
    static int Count<T>(DependencyObject parent) where T : DependencyObject { var count = 0; for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) { var c = VisualTreeHelper.GetChild(parent, i); count += c is T ? 1 : Count<T>(c); } return count; }
}
