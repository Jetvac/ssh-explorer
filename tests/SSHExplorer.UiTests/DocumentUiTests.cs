using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SSHExplorer.App;
using SSHExplorer.App.ViewModels;
using SSHExplorer.Core;

internal static class DocumentUiTests
{
    public static int Run(string[] args)
    {
        CiVisualEvidence.RequireCi();
        try
        {
            var app = TestApplication.Create(); Theme.Apply("Light"); int checks = 0;
            // Routed async commands normally start inside Application.Run's
            // Dispatcher context. This component runner pumps manually.
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU"); CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture;
            void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
            string output = args.SkipWhile(a => a != "--render-dir").Skip(1).FirstOrDefault() ?? "artifacts/ui-beta5-documents";
            var evidence = new CiVisualEvidence(output); var store = new AppStore(Path.Combine(Environment.CurrentDirectory, ".tools", "document-ui", Guid.NewGuid().ToString("N")));
            typeof(App).GetProperty(nameof(App.Store))!.SetValue(null, store); store.Settings.ShowHidden = true;
            var profile = new ServerProfile { Name = "Linux · UI fixture", Host = "127.0.0.1", Username = "fixture" }; store.Profiles.Add(profile);
            var session = new ServerSession(profile, store); var main = new MainWindow { Width = 1100, Height = 760, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
            using var files = new FileBrowserView(session, main, new FileInteractionTests.Operations()); var model = (FileBrowserViewModel)files.DataContext;
            model.CommitNavigation(model.BeginNavigation(), "/srv/fixture", CompositionFixtures.Files(), true, [], 0); model.ApplyFilter(true, true); Invoke(files, "ApplyItems"); Invoke(files, "UpdateBreadcrumbs");
            var tree = (TreeView)Field(files, "tree"); tree.Items.Clear(); var node = (TreeViewItem)Invoke(files, "Node", "fixture", "/srv/fixture")!; node.Items.Clear(); Ui.SetIsCurrentFolder(node, true); tree.Items.Add(node);
            using var monitor = new MonitorView(new MonitorViewModel(() => CompositionFixtures.Timestamp)); var view = new SessionView(session, main, files, monitor); ((SessionViewModel)Field(view, "viewModel")).StatusText = "Данные стенда · без подключения"; view.SetActive(false);
            Invoke(main, "AddSessionTab", profile, session, view); main.Show(); view.SetActive(false); view.ShowSection("files"); Layout(main);
            var tabs = (TabControl)Field(main, "tabs"); var panel = Descendants<SessionHeaderPanel>(main).Single(); var scroll = (ScrollViewer)panel.Children[1]; var plus = (Button)panel.Children[3];
            evidence.Capture((FrameworkElement)main.Content, "files-two-tabs", new { theme = "Light", fixtureOnly = true, tabs = 2, nativeShellIcons = true });
            Check(panel.Children[0].Visibility == Visibility.Collapsed && panel.Children[2].Visibility == Visibility.Collapsed && scroll.ScrollableWidth < .5, "two real session headers expose every tab and reserve no scroll-arrow area");
            Check(Math.Abs(plus.TranslatePoint(new Point(), panel).X - (scroll.TranslatePoint(new Point(), panel).X + scroll.ActualWidth)) <= 1.5 && plus.ActualWidth >= 32, "new-tab button follows the actual tab extent with its own full hit target");
            var table = (DataGrid)Field(files, "table"); table.SelectedItems.Add(model.VisibleEntries[0]); table.SelectedItems.Add(model.VisibleEntries[1]); Layout(main);
            string[] modes = ["Details", "Small", "Medium", "Large", "Huge", "List", "Tile", "Content"];
            foreach (var mode in modes)
            {
                Invoke(files, "SetMode", mode, false); Layout(main);
                var icons = Descendants<ShellFileIcon>(files).Where(i => i.IsVisible && i.Entry is not null).ToArray();
                foreach (var icon in icons.Take(12)) SystemFileIconProvider.GetAsync(icon.Entry!, Math.Max(icon.ActualWidth, icon.ActualHeight)).GetAwaiter().GetResult();
                Pump(); Layout(main);
                if (icons.Length == 0 || !icons.Any(i => i.Source is BitmapSource { IsFrozen: true }) || !ViewIconMatches(((Panel)Field(files, "viewModeRow")).Children[0], CommandGlyphs.View(mode))) throw new Exception("Materialized Shell icons/current view command failed for " + mode);
                evidence.Capture((FrameworkElement)main.Content, "files-native-" + mode.ToLowerInvariant(), new { theme = "Light", fixtureOnly = true, mode, nativeShellIcons = true, shellKinds = SystemFileIconProvider.CachedKinds });
            }
            Check(true, "all eight actual file views render native Shell resources and the command glyph follows the active representation");
            app.Resources["UIFontSize"] = 22d; Invoke(files, "SetMode", "Medium", false); Layout(main);
            var enlargedTiles = Descendants<ListBoxItem>(files).Where(i => i.IsVisible).ToArray();
            foreach (var item in enlargedTiles.Take(12)) foreach (var presenter in Descendants<FileNamePresenter>(item)) { var top = presenter.TranslatePoint(new Point(), item).Y; if (top + presenter.ActualHeight > item.ActualHeight + 1) throw new Exception("Enlarged file-name surface exceeds its tile bounds."); }
            evidence.Capture((FrameworkElement)main.Content, "files-native-large-text", new { theme = "Light", fixtureOnly = true, nativeShellIcons = true, applicationFontSize = 22, osDpiChanged = false });
            Check(enlargedTiles.Length > 0, "enlarged application text keeps full file-name surfaces inside the measured virtualized tiles"); app.Resources["UIFontSize"] = 14d;
            Invoke(files, "SetMode", "Details", false); main.Width = 760; Layout(main); evidence.Capture((FrameworkElement)main.Content, "files-native-narrow", new { theme = "Light", fixtureOnly = true, nativeShellIcons = true }); main.Width = 1100; Layout(main);
            table.SelectedItems.Clear(); table.SelectedItem = model.VisibleEntries.Single(r => r.Name == "Фото_пример.png"); Layout(main); var imageMenu = (ContextMenu)Invoke(files, "BuildContextMenu", true)!;
            bool imageEdit = imageMenu.Items.OfType<MenuItem>().Single(i => i.Header?.ToString() == "Редактировать в Блокноте").IsEnabled;
            table.SelectedItems.Clear(); table.SelectedItem = model.VisibleEntries.Single(r => r.Name == "script.sh"); Layout(main); var scriptMenu = (ContextMenu)Invoke(files, "BuildContextMenu", true)!;
            Console.WriteLine($"DIAGNOSTIC editor menu imageEnabled={imageEdit}; scriptEnabled={scriptMenu.Items.OfType<MenuItem>().Single(i => i.Header?.ToString() == "Редактировать в Блокноте").IsEnabled}; selected={model.SelectedEntries.Count}; selectedNames={string.Join(",", model.SelectedEntries.Select(e => e.Name))}");
            Check(!imageEdit && scriptMenu.Items.OfType<MenuItem>().Single(i => i.Header?.ToString() == "Редактировать в Блокноте").IsEnabled && scriptMenu.Items.OfType<MenuItem>().Any(i => i.Header?.ToString() == "Просмотр"), "the actual editor menu distinguishes text editing from image preview and retains a separate read-only view command");
            var workspace = new RemoteEditWorkspace(store.DirectoryPath); typeof(MainWindow).GetField("editWorkspace", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, workspace);
            var transport = new FixtureTransport(profile.Id, session.Id); var document = workspace.OpenAsync(transport, "/srv/fixture/service.sh", "Linux · document fixture", false).GetAwaiter().GetResult();
            int launches = 0; string? launched = null;
            var window = new RemoteDocumentsWindow(main, workspace, _ => transport, d => { launches++; launched = d.WorkingPath; return Task.CompletedTask; }) { Owner = main, ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual };
            window.Show(); window.Select(document); Layout(window);
            void Capture(string name) => evidence.Capture((FrameworkElement)window.Content, name, new { theme = Theme.Current, fixtureOnly = true, documentState = document.Record.State.ToString(), format = document.Record.Format.Description, editorLauncher = "component stub; actual installed Notepad tested separately on host", notepadUsed = false });
            Capture("documents-open"); Button Command(string name) => Descendants<Button>(window).Single(b => AutomationProperties.GetName(b) == name);
            Command("Открыть редактор").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Check(launches == 1 && launched == document.WorkingPath, "the real open-editor command dispatches the exact captured local working copy to the launcher boundary");
            document.AttachForReviewAsync(transport).GetAwaiter().GetResult(); File.WriteAllText(document.WorkingPath, "first saved version\n"); document.CheckAsync().GetAwaiter().GetResult(); Pump(); Capture("documents-pending");
            Check(document.Record.State == RemoteEditState.Pending && Command("Отправить сейчас").IsEnabled, "a stable saved version is visibly pending before the actual send command");
            transport.CommitGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Command("Отправить сейчас").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); WaitFor(() => document.Record.State == RemoteEditState.Uploading && document.BytesTransferred > 0); Pump(); Capture("documents-uploading");
            var progress = (ProgressBar)Field(window, "sendingProgress"); Command("Другие команды").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            var sendingMenu = PresentationSource.CurrentSources.Cast<PresentationSource>().Select(s => s.RootVisual).Where(v => v is not null).SelectMany(v => Descendants<ContextMenu>(v!)).Single(m => m.IsOpen);
            Check(progress.IsVisible && progress.Value > 0 && progress.Value < 100 && sendingMenu.Items.OfType<MenuItem>().Single(i => i.Header?.ToString() == "Приостановить / отменить отправку").IsEnabled, "actual sending progress shows captured snapshot bytes and exposes cancellation during the in-flight operation"); sendingMenu.IsOpen = false; transport.CommitGate.TrySetResult(true);
            WaitFor(() => document.Record.State == RemoteEditState.Confirmed); transport.CommitGate = null; Pump(); Capture("documents-confirmed");
            Check(transport.Writes == 1 && Encoding.UTF8.GetString(transport.Bytes) == "first saved version\n", "the actual send command confirms the resulting hash and performs exactly one replacement");
            transport.Bytes = Encoding.UTF8.GetBytes("external server change\n"); File.WriteAllText(document.WorkingPath, "local conflict version\n"); document.CheckAsync(true).GetAwaiter().GetResult(); Pump(); Capture("documents-conflict");
            Check(document.Record.State == RemoteEditState.Conflict && !Command("Отправить сейчас").IsEnabled && Command("Сохранить локально").IsEnabled, "conflict disables ordinary send while preserving local export and the selected document");
            var conflictState = Descendants<TextBlock>(window).First(t => t.Text == "Конфликт версий" && t.IsVisible);
            Check((conflictState.Foreground as SolidColorBrush)?.Color == (window.FindResource("ValidationInk") as SolidColorBrush)?.Color && conflictState.FontWeight == FontWeights.SemiBold, "a version conflict stands out in the documents list like a failed transfer");
            Command("Другие команды").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            var menu = PresentationSource.CurrentSources.Cast<PresentationSource>().Select(s => s.RootVisual).Where(v => v is not null).SelectMany(v => Descendants<ContextMenu>(v!)).Single(m => m.IsOpen);
            Check(menu.Items.OfType<MenuItem>().Any(i => i.Header?.ToString() == "Заменить проверенную серверную версию…") && menu.Items.OfType<MenuItem>().Any(i => i.Header?.ToString() == "Подключить копию из «Сохранить как»…"), "the live document overflow exposes distinct conflict and external Save As actions");
            evidence.Capture(menu, "documents-conflict-menu", new { theme = "Light", fixtureOnly = true, editorLauncher = "component stub" }); menu.IsOpen = false; Pump();
            transport.DropAfterCommit = true; var approved = transport.ReadAsync(document.Record.Location, CancellationToken.None).GetAwaiter().GetResult(); document.ReplaceApprovedVersionAsync(approved).GetAwaiter().GetResult(); Pump(); Capture("documents-unknown");
            Check(document.Record.State == RemoteEditState.Unknown && File.Exists(document.OperationPath), "uncertain post-commit result remains visible with its durable immutable operation snapshot");
            transport.DropAfterCommit = false; var previousWrites = transport.Writes; document.ResumeAsync(transport).GetAwaiter().GetResult(); Pump(); Capture("documents-recovered");
            Check(document.Record.State == RemoteEditState.Confirmed && transport.Writes == previousWrites, "recovery reads the committed destination and confirms it without another upload");
            File.WriteAllText(document.WorkingPath, "changed format\r\n"); document.CheckAsync(true).GetAwaiter().GetResult(); Pump(); Capture("documents-format-change");
            Check(document.Record.State == RemoteEditState.FormatConfirmation && document.Record.Message?.Contains("LF") == true, "the actual document surface retains an explicit format-change decision instead of showing success");
            document.ResumeAsync(transport, true).GetAwaiter().GetResult(); document.DetachAsync().GetAwaiter().GetResult(); Pump(); Capture("documents-paused");
            Check(document.Record.State == RemoteEditState.Paused && File.Exists(document.WorkingPath), "pausing keeps the working copy and exposes a recoverable state without closing an editor");
            window.Width = 740; window.Height = 440; app.Resources["UIFontSize"] = 22d; Layout(window); Capture("documents-large-text");
            var footer = (TextBlock)Field(window, "detail"); var detailsScroll = (ScrollViewer)Field(window, "detailScroller"); var documentTable = (DataGrid)Field(window, "list"); var headers = Descendants<System.Windows.Controls.Primitives.DataGridColumnHeadersPresenter>(documentTable).Single(); var firstDocumentRow = Descendants<DataGridRow>(documentTable).First();
            Check(detailsScroll.ViewportHeight >= 60 && detailsScroll.ExtentHeight + 1 >= footer.DesiredSize.Height && documentTable.ActualHeight >= headers.ActualHeight + firstDocumentRow.ActualHeight - 1, "narrow enlarged-text document layout retains a complete row and a scrollable full path/reason instead of clipping either surface");
            app.Resources["UIFontSize"] = 14d; window.Width = 980; window.Height = 540; Theme.Apply("Dark"); Layout(window); Capture("documents-dark"); Theme.Apply("Light");
            Check(!Path.GetFullPath(workspace.Root).StartsWith(Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "working documents and operation journals stay outside every published visual-artifact directory");
            window.Close(); bool closed = false; main.Closed += (_, _) => closed = true; main.Close(); WaitFor(() => closed); app.Shutdown();
            Console.WriteLine($"RESULT: {checks} document/native-file actual WPF checks passed; CI-only HWNDs, controlled document transport/launcher, no Notepad or SSH"); return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
    }
    // The view command shows the current mode in its icon slot (a vector key or a font glyph), never in its label.
    static bool ViewIconMatches(UIElement icon, string key) => icon is VectorIcon vector ? vector.Key == key : icon is TextBlock glyph && glyph.Text == key;
    static object Field(object value, string name) => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
    static object? Invoke(object value, string name, params object[] args) => value.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(value, args);
    static void Layout(FrameworkElement element) { element.UpdateLayout(); Pump(); element.UpdateLayout(); }
    static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false); Dispatcher.PushFrame(frame); }
    static void WaitFor(Func<bool> ready) { var watch = System.Diagnostics.Stopwatch.StartNew(); while (!ready() && watch.Elapsed < TimeSpan.FromSeconds(8)) { Pump(); Thread.Sleep(10); } if (!ready()) throw new TimeoutException("Document UI action did not complete."); }
    static IEnumerable<T> Descendants<T>(DependencyObject value) where T : DependencyObject { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(value); i++) { var child = VisualTreeHelper.GetChild(value, i); if (child is T result) yield return result; foreach (var nested in Descendants<T>(child)) yield return nested; } }
    sealed class FixtureTransport(Guid profile, Guid session) : IRemoteDocumentTransport
    {
        public RemoteDocumentIdentity Identity { get; } = new(profile, "fixture.invalid", 22, "fixture", "offline-test-key-not-SSH-trust");
        public Guid SessionId => session; public CancellationToken Lifetime => CancellationToken.None;
        public byte[] Bytes = Encoding.UTF8.GetBytes("#!/bin/sh\n# offline document fixture\n"); public int Writes; public bool DropAfterCommit; public TaskCompletionSource<bool>? CommitGate;
        readonly DocumentMetadata metadata = new(0, CompositionFixtures.Timestamp.UtcDateTime, 493, 1000, 1000);
        public Task<DocumentLocation> LocateAsync(string path, bool followLink, CancellationToken ct) => Task.FromResult(new DocumentLocation(path, path));
        public Task<RemoteDocumentVersion> ReadAsync(DocumentLocation location, CancellationToken ct) => Task.FromResult(new RemoteDocumentVersion(Bytes.ToArray(), metadata with { Length = Bytes.Length }));
        public async Task<RemoteDocumentVersion> ReplaceAsync(DocumentLocation location, string expectedHash, DocumentMetadata original, byte[] snapshot, Guid operation, Func<Task> beforeCommit, IProgress<long>? progress, CancellationToken ct)
        {
            if (RemoteEditSession.Hash(Bytes) != expectedHash) throw new DocumentConflictException("Внешняя версия изменена; controlled UI fixture.");
            if (CommitGate is { } pending) { await Task.Delay(170, ct).ConfigureAwait(false); progress?.Report(snapshot.LongLength / 2); await pending.Task.WaitAsync(ct).ConfigureAwait(false); progress?.Report(snapshot.LongLength); }
            await beforeCommit().ConfigureAwait(false); Bytes = snapshot.ToArray(); Writes++; if (DropAfterCommit) throw new IOException("Controlled loss after commit."); return await ReadAsync(location, ct).ConfigureAwait(false);
        }
        public Task SaveCopyAsync(string path, byte[] bytes, short mode, CancellationToken ct) => Task.CompletedTask;
    }
}
