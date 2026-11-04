using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SSHExplorer.App.ViewModels;
using SSHExplorer.Core;
using SSHExplorer.Monitoring;

internal static class ViewModelTests
{
    public static int Run(Action<bool, string> assert)
    {
        int count = 0;
        void Check(bool value, string name) { assert(value, name); count++; }
        static RemoteEntry File(string name, string parent = "/first") => new() { Name = name, FullPath = parent + "/" + name, IsRegularFile = true, Length = 100, LastWriteTimeUtc = DateTime.UnixEpoch };
        var sessionModel = new SessionViewModel(); bool sectionsAccepted = true;
        foreach (var key in new[] { "server", "terminal", "monitor", "files" }) { try { sessionModel.CurrentSection = key; sectionsAccepted &= sessionModel.CurrentSection == key; } catch (ArgumentException) { sectionsAccepted = false; } }
        bool unknownRejected; try { sessionModel.CurrentSection = "unknown"; unknownRejected = false; } catch (ArgumentException) { unknownRejected = sessionModel.CurrentSection == "files"; }
        Check(sectionsAccepted && unknownRejected, "MVVM session accepts every navigation section, including Server, and rejects unknown ones without changing state");
        var files = new FileBrowserViewModel();
        var first = files.BeginNavigation(); files.CommitNavigation(first, "/first", [File("A"), File("a"), File("item10"), File("item2"), File(".hidden")], true, [], 0);
        files.ApplyFilter(false, true); files.SetSelection(["/first/A"]);
        var stale = files.BeginNavigation(); var current = files.BeginNavigation();
        Check(!files.CommitNavigation(stale, "/late", [], true, [], 0) && files.CurrentPath == "/first" && files.Entries.Count == 5, "MVVM late navigation cannot replace the current path or listing");
        files.FailNavigation(current, "/denied", "Нет доступа");
        Check(files.CurrentPath == "/first" && files.SelectedPaths.SequenceEqual(["/first/A"]) && files.Message.Contains("Нет доступа") && !files.IsLoading, "MVVM failed navigation preserves path, listing and selection");
        files.SetSelection(["/first/a"]); Check(files.SelectedEntries.Single().Name == "a", "MVVM POSIX selection keeps uppercase and lowercase identities distinct");
        files.SetSelection(["/first/A"]); var second = files.BeginNavigation(); files.CommitNavigation(second, "/second", [File("b", "/second")], true, files.SelectedPaths, 47);
        var back = files.HistoryTarget(-1)!.Value; var failure = files.BeginNavigation(); files.FailNavigation(failure, back.Item.Path, "Нет доступа");
        Check(files.HistoryIndex == 1 && files.CurrentPath == "/second", "MVVM failed history traversal does not advance the history cursor");
        var successful = files.BeginNavigation(); files.CommitNavigation(successful, "/first", [File("A"), File("a")], false, [], 3, back.Index);
        Check(files.HistoryIndex == 0 && files.SelectedPaths.SequenceEqual(["/first/A"]) && files.History[0].Offset == 47, "MVVM successful history traversal restores the original selection and scroll position");
        var branch = files.BeginNavigation(); files.CommitNavigation(branch, "/branch", [], true, files.SelectedPaths, 47);
        Check(files.History.Count == 2 && files.History[1].Path == "/branch" && !files.CanGoForward, "MVVM navigation from history prunes the abandoned forward branch");
        var refreshed = files.BeginNavigation(); files.CommitNavigation(refreshed, "/branch", [File("new", "/branch")], false, ["/branch/gone"], 0); Check(files.SelectedPaths.Count == 0, "MVVM refresh drops selections whose objects disappeared");
        files.SetEntries([new(File("item10")), new(File("item2")), new(File(".hidden"))]); files.SearchQuery = ""; files.ApplyFilter(false, true);
        Check(files.VisibleEntries.Select(r => r.Name).SequenceEqual(["item2", "item10"]), "MVVM file filtering hides dotfiles and sorts numeric names naturally");
        var closed = files.BeginNavigation(); files.InvalidateNavigation(); Check(!files.CommitNavigation(closed, "/after-close", [], true, [], 0), "MVVM closed navigation ignores in-flight replies");
        var home = new HomeViewModel(); var profile1 = new ServerProfile { Name = "Alpha", Host = "alpha.test", Username = "alice" }; var profile2 = new ServerProfile { Name = "Beta", Host = "beta.test", Username = "bob" };
        home.Refresh([profile1, profile2], [(profile1.Id, SessionState.Connected)]);
        Check(home.VisibleProfiles.Single(p => p.Profile.Id == profile2.Id).Status == "Не подключён", "MVVM home never calls an unchecked profile unavailable");
        var search = new TextBox { DataContext = home }; search.SetBinding(TextBox.TextProperty, new Binding(nameof(HomeViewModel.SearchQuery)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }); search.SetCurrentValue(TextBox.TextProperty, "bob"); search.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
        Check(home.SearchQuery == "bob" && home.VisibleProfiles.Single().Profile.Id == profile2.Id, "MVVM real WPF two-way search binding drives profile filtering");
        var message = new TextBlock { DataContext = files }; message.SetBinding(TextBlock.TextProperty, new Binding(nameof(FileBrowserViewModel.Message))); files.Message = "Ошибка тестового пути";
        Check(message.Text == "Ошибка тестового пути", "MVVM property notifications update a real WPF bound status element");
        var monitor = new MonitorViewModel(); var identity = new ProcessIdentity(42, 123, 1000);
        monitor.ApplySnapshot(Snapshot(identity), []); monitor.SelectedIdentity = identity; var processCollection = monitor.Processes; var processRow = monitor.Processes.Single(); var resets = 0; monitor.Processes.CollectionChanged += (_, e) => { if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) resets++; }; monitor.ApplySnapshot(Snapshot(identity), []);
        Check(monitor.SelectedIdentity == identity && monitor.Processes.Single().ReadText == "—" && monitor.Processes.Single().ReadReason.Length > 0, "MVVM monitoring preserves exact process identity and does not invent missing I/O");
        Check(ReferenceEquals(processCollection, monitor.Processes) && ReferenceEquals(processRow, monitor.Processes.Single()) && resets == 0, "MVVM repeated process snapshots preserve collection, row identity and selection without table reset");
        var changedSample = Snapshot(identity); monitor.ApplySnapshot(changedSample with { Processes = [changedSample.Processes[0] with { CpuPercent = 22, MemoryBytes = 300 }] }, []);
        Check(ReferenceEquals(processRow, monitor.Processes.Single()) && processRow.Cpu == 22 && processRow.MemoryText == MetricFormatter.Size(300) && monitor.SelectedIdentity == identity && resets == 0, "MVVM changed process measurements update existing selected row without replacing the collection");
        monitor.ApplySnapshot(Snapshot(new ProcessIdentity(42, 999, 1000)), []); Check(monitor.SelectedIdentity is null, "MVVM process selection rejects a reused PID with a new start time");
        monitor.SearchQuery = "nobody"; Check(monitor.Processes.Count == 0, "MVVM process search filters the snapshot without changing server metrics");
        monitor.ReportFailure("timeout"); Check(monitor.Snapshot is not null && monitor.Freshness.Contains("устарели") && monitor.CpuText != "Недоступно", "MVVM monitor failure marks existing measurements stale and keeps their values");
        return count;
    }

    static MonitorSnapshot Snapshot(ProcessIdentity identity) => new(DateTimeOffset.Now,
        new SystemMetric(25, 2, 1024, 512, 512, 0, 0, TimeSpan.FromMinutes(5), .1, .2, .3, 100, false),
        [new ProcessMetric(identity, "worker", "1000", "Running", 10, 100, null, null, null)], [], [], [], []);
}
