using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SSHExplorer.App;
using SSHExplorer.App.ViewModels;
using SSHExplorer.Core;

internal static class UiComponentTests
{
    // Deliberately excluded from --resources-only. This renders real WPF controls
    // inside the existing test HWND, and is run only in the permitted CI environment.
    public static int Run(Window host, Action<bool, string> assert)
    {
        int checks = 0;
        void Check(bool value, string name) { assert(value, name); checks++; }
        var directory = Path.Combine(Path.GetTempPath(), "sshexplorer-ui-components-" + Guid.NewGuid().ToString("N"));
        var store = new AppStore(directory); typeof(App).GetProperty(nameof(App.Store))!.SetValue(null, store);
        var profile = new ServerProfile { Name = "UI component fixture", Host = "127.0.0.1", Username = "fixture" };
        var session = new ServerSession(profile, store);
        // No command is invoked; the component's MainWindow owner is irrelevant
        // to this render-only test. No connection, authentication or shell is opened.
        using var browser = new FileBrowserView(session, null!);
        try
        {
            var model = (FileBrowserViewModel)browser.DataContext;
            var entries = Enumerable.Range(0, 1_000).Select(i => new RemoteEntry { Name = "file-" + i + ".txt", FullPath = "/file-" + i + ".txt", IsRegularFile = true, Length = i }).ToArray();
            var generation = model.BeginNavigation(); model.CommitNavigation(generation, "/", entries, true, [], 0); model.ApplyFilter(true, true);
            var composition = new DockPanel(); DockPanel.SetDock(browser.AddressBar, Dock.Top); composition.Children.Add(browser.AddressBar); DockPanel.SetDock(browser.CommandBar, Dock.Top); composition.Children.Add(browser.CommandBar); DockPanel.SetDock(browser.StatusBar, Dock.Bottom); composition.Children.Add(browser.StatusBar);
            var body = new Grid(); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) }); body.ColumnDefinitions.Add(new ColumnDefinition()); body.Children.Add(browser.Navigation); Grid.SetColumn(browser, 1); body.Children.Add(browser); composition.Children.Add(body); host.Content = composition; Layout(host);
            var table = (DataGrid)Field(browser, "table"); var tiles = (ListBox)Field(browser, "tiles"); var setMode = typeof(FileBrowserView).GetMethod("SetMode", BindingFlags.NonPublic | BindingFlags.Instance)!;
            foreach (var mode in new[] { "Details", "Huge", "Large", "Medium", "Small", "List", "Tile", "Content" })
            {
                setMode.Invoke(browser, [mode, false]); Layout(host);
                var containers = Descendants<ListBoxItem>(tiles).ToArray();
                Check(mode == "Details" ? Descendants<DataGridRow>(table).Any() : containers.Length > 0 && containers.Length < entries.Length && ReferenceEquals(tiles.ItemContainerStyle.BasedOn, Application.Current.FindResource(typeof(ListBoxItem))), "real file view renders with inherited row states and bounded containers: " + mode);
            }
            setMode.Invoke(browser, ["Details", false]); Layout(host); table.SelectedItem = model.VisibleEntries[0]; table.ScrollIntoView(table.SelectedItem); Layout(host);
            var row = (DataGridRow)table.ItemContainerGenerator.ContainerFromIndex(0); var cells = Descendants<DataGridCell>(row).ToArray();
            Check(cells.Length > 1 && cells.All(c => c.Template.FindName("CellContent", c) is Border b && b.BorderThickness == new Thickness(0)), "selected real DataGrid row has no per-cell focus/selection rectangles");
            var chrome = (Border)row.Template.FindName("RowChrome", row);
            host.Activate(); Keyboard.Focus(cells[0]); Layout(host);
            if (cells[0].IsKeyboardFocusWithin)
                Check(row.IsKeyboardFocusWithin && Equals(chrome.BorderBrush, Application.Current.FindResource("Accent")), "real keyboard focus draws one whole-row cue");
            else Console.WriteLine("NOT RUN real focus acquisition: the CI desktop did not grant keyboard focus; no live-focus PASS claimed");
            var before = table.TransformToAncestor(composition).Transform(new Point(0, 0)).Y;
            model.Message = "Загрузка и ошибка остаются в status без изменения положения заголовков"; Layout(host); var during = table.TransformToAncestor(composition).Transform(new Point(0, 0)).Y;
            model.Message = ""; Layout(host); var message = (TextBlock)Field(browser, "message");
            Check(before == during && message.Visibility == Visibility.Collapsed, "nonempty and empty operation messages do not shift table headers");
            var tree = (TreeView)Field(browser, "tree"); var root = (TreeViewItem)tree.Items[0]; root.Items.Clear();
            var child = (TreeViewItem)typeof(FileBrowserView).GetMethod("Node", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(browser, ["Child", "/Child"])!;
            root.Items.Add(child); root.IsExpanded = true; typeof(FileBrowserView).GetMethod("UpdateCurrentTree", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(browser, null); Layout(host);
            var rootHeader = (Border)root.Template.FindName("HeaderChrome", root); var childHeader = (Border)child.Template.FindName("HeaderChrome", child);
            Check(Math.Abs(rootHeader.ActualWidth - childHeader.ActualWidth) < 1 && Ui.GetIsCurrentFolder(root), "nested tree headers keep full selection width and current folder independent of table focus");
            return checks;
        }
        finally { session.DisposeAsync().AsTask().GetAwaiter().GetResult(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    static object Field(object target, string name) => target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
    static void Layout(Window host) { host.UpdateLayout(); if (host.Content is FrameworkElement root) { root.Measure(new Size(host.ActualWidth, host.ActualHeight)); root.Arrange(new Rect(root.DesiredSize)); root.UpdateLayout(); } }
    static IEnumerable<T> Descendants<T>(DependencyObject target) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(target); i++) { var child = VisualTreeHelper.GetChild(target, i); if (child is T typed) yield return typed; foreach (var nested in Descendants<T>(child)) yield return nested; }
    }
}
