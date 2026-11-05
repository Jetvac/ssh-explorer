using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using SSHExplorer.App;
using SSHExplorer.App.ViewModels;
using SSHExplorer.Core;

/// <summary>0.3.1: the server panel is edited in place — order, visibility and modules — and applies at once.</summary>
internal static class SidebarUiChecks
{
    public static void Run(SessionView view, Dictionary<string, Button> navigation, CiVisualEvidence evidence, Action<bool, string> check)
    {
        var errors = new List<string>(); Ui.ErrorOverride = message => { lock (errors) errors.Add(message); };
        var settings = App.Store.Settings;
        try
        {
            Sidebar.Apply(settings, Sidebar.Defaults()); App.RaiseModulesChanged(); Pump();
            var side = (Panel)VisualTreeHelper.GetParent(navigation["server"]);
            var customize = Descendants<Button>(view).Single(b => b.Name == "SidebarCustomize");
            var divider = (FrameworkElement)VisualTreeHelper.GetParent(customize);
            check(customize.IsVisible && divider.Name == "SidebarDivider" && side.Children.IndexOf(divider) == side.Children.IndexOf(navigation["files"]) - 1 && !navigation["docker"].IsVisible && !navigation["scripts"].IsVisible,
                "the «Настроить панель» gear sits at the end of the divider above «Файлы»; modules are hidden by default");

            customize.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump();
            var editor = view.SidebarEditorView!;
            CheckBox Show(string key) => Descendants<CheckBox>(editor).Single(c => Equals(c.Tag, key + "|show"));
            Button Arrow(string key, string part) => Descendants<Button>(editor).Single(c => Equals(c.Tag, key + "|" + part));
            check(editor is { IsVisible: true } && editor.Items.Count == Sidebar.DefaultOrder.Length && Show("docker").IsChecked == false && Show("scripts").IsChecked == false && !Show("files").IsEnabled && !navigation["server"].IsVisible,
                "the editor replaces the panel in place: every section, modules unticked, «Файлы» cannot be hidden");
            evidence.Capture(editor, "sidebar-editor", new { theme = "Light", fixtureOnly = true });

            var docker = Show("docker"); docker.IsChecked = true; docker.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump();
            check(settings.DockerModule && Show("docker").IsChecked == true, "ticking Docker turns the module on and is saved at once");
            Arrow("server", "down").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump();
            check(Sidebar.Resolve(settings)[1].Key == "server" && Arrow("server", "up").IsEnabled, "the arrow moves «Сервер» one place down");
            check(Arrow(Sidebar.Resolve(settings)[0].Key, "up").IsEnabled == false && Arrow("files", "down").IsEnabled == false, "the first row cannot go up and the last cannot go down");

            Descendants<Button>(editor).Single(b => b.Name == "SidebarDone").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump();
            side = (Panel)VisualTreeHelper.GetParent(navigation["server"]);
            check(view.SidebarEditorView is null && navigation["docker"].IsVisible && side.Children.IndexOf(navigation["server"]) > side.Children.IndexOf(navigation["terminal"]),
                "«Готово» returns to the panel in the new order with Docker shown");

            // Hiding the open section leaves it for «Файлы».
            view.ShowSection("docker"); Pump();
            customize.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump(); editor = view.SidebarEditorView!;
            docker = Show("docker"); docker.IsChecked = false; docker.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump();
            var model = (SessionViewModel)typeof(SessionView).GetField("viewModel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
            check(!settings.DockerModule && model.CurrentSection == "files", "hiding the open section switches the tab to «Файлы»: " + model.CurrentSection);

            var hide = Show("firewall"); hide.IsChecked = false; hide.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump();
            Descendants<Button>(editor).Single(b => b.Name == "SidebarReset").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump();
            check(Sidebar.Resolve(settings).SequenceEqual(Sidebar.Defaults()) && Show("firewall").IsChecked == true && Show("docker").IsChecked == false, "«По умолчанию» restores the usual order and hides the modules");
            view.SidebarEditorView!.Done(); Pump();
            Sidebar.Apply(settings, Sidebar.Move(Sidebar.Resolve(settings), "files", -100)); App.RaiseModulesChanged(); Pump();
            side = (Panel)VisualTreeHelper.GetParent(navigation["server"]); divider = (FrameworkElement)VisualTreeHelper.GetParent(customize);
            check(side.Children.IndexOf(navigation["files"]) == 0 && side.Children.IndexOf(divider) > side.Children.IndexOf(navigation["scripts"]), "with «Файлы» first the divider and its gear close the list");
            Sidebar.Apply(settings, Sidebar.Defaults()); App.RaiseModulesChanged(); Pump();

            // The right-click menu hides one section without opening the editor.
            var menu = (ContextMenu)typeof(SessionView).GetMethod("SidebarMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [navigation["tunnels"]])!;
            var hideTunnels = menu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "Скрыть «Туннели»"));
            hideTunnels.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump();
            check(!navigation["tunnels"].IsVisible && settings.SidebarHidden.Contains("tunnels") && menu.Items.OfType<MenuItem>().Any(m => Equals(m.Header, "Настроить панель…")), "the context menu hides «Туннели» and offers the editor");
        }
        finally
        {
            Sidebar.Apply(settings, Sidebar.Defaults()); App.RaiseModulesChanged(); Pump();
            if (view.SidebarEditorView is { } open) { open.Done(); Pump(); }
            Ui.ErrorOverride = null;
        }
        lock (errors) check(errors.Count == 0, "no error dialog was raised while editing the panel" + (errors.Count > 0 ? ": " + string.Join(" | ", errors) : ""));
    }

    static void Pump() { for (int i = 0; i < 3; i++) { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false); Dispatcher.PushFrame(frame); } }
    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match; foreach (var nested in Descendants<T>(child)) yield return nested; } }
}
