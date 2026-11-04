using SSHExplorer.Core;

namespace SSHExplorer.App.ViewModels;

public sealed record BrowserHistoryItem(string Path, List<string> Selection, double Offset);
public sealed record BrowserContext(string Path, List<BrowserHistoryItem> History, int Index, List<string> Selection, double Offset, string Mode);

public sealed class FileBrowserViewModel : ObservableObject
{
    string currentPath = "", viewMode = "Details", sortKey = "Name", searchQuery = "", message = "", statusText = "Элементов: 0";
    bool descending, isLoading, available, isMutating, isEditingName;
    long navigationGeneration;
    int historyIndex = -1;
    List<FileItemViewModel> entries = [];
    IReadOnlyList<FileItemViewModel> visibleEntries = [];
    List<string> selectedPaths = [];

    public string CurrentPath { get => currentPath; private set { if (Set(ref currentPath, value)) RaiseAvailability(); } }
    public string ViewMode { get => viewMode; set => Set(ref viewMode, value); }
    public string SortKey { get => sortKey; set => Set(ref sortKey, value); }
    public bool Descending { get => descending; set => Set(ref descending, value); }
    public string SearchQuery { get => searchQuery; set => Set(ref searchQuery, value); }
    public string Message { get => message; set => Set(ref message, value); }
    public string StatusText { get => statusText; private set => Set(ref statusText, value); }
    public bool IsLoading { get => isLoading; private set { if (Set(ref isLoading, value)) RaiseAvailability(); } }
    public bool Available { get => available; set { if (Set(ref available, value)) RaiseAvailability(); } }
    public bool IsMutating { get => isMutating; set { if (Set(ref isMutating, value)) RaiseAvailability(); } }
    public bool IsEditingName { get => isEditingName; set { if (Set(ref isEditingName, value)) RaiseAvailability(); } }
    public bool CanCreate => Available && !IsLoading && !IsMutating && !IsEditingName && CurrentPath.Length > 0;
    public bool HasSelection => CanCreate && SelectedPaths.Count > 0;
    public bool CanRename => HasSelection && SelectedPaths.Count == 1;
    public bool CanGoUp => Available && CurrentPath.Length > 0 && CurrentPath != "/";
    public long NavigationGeneration => navigationGeneration;
    public List<BrowserHistoryItem> History { get; } = [];
    public int HistoryIndex { get => historyIndex; private set { Set(ref historyIndex, value); Raise(nameof(CanGoBack)); Raise(nameof(CanGoForward)); } }
    public bool CanGoBack => Available && !IsLoading && HistoryIndex > 0;
    public bool CanGoForward => Available && !IsLoading && HistoryIndex >= 0 && HistoryIndex < History.Count - 1;
    public List<FileItemViewModel> Entries { get => entries; private set => Set(ref entries, value); }
    public IReadOnlyList<FileItemViewModel> VisibleEntries { get => visibleEntries; private set => Set(ref visibleEntries, value); }
    public IReadOnlyList<string> SelectedPaths => selectedPaths;
    public IReadOnlyList<FileItemViewModel> SelectedEntries => Entries.Where(r => selectedPaths.Contains(r.Entry.FullPath, StringComparer.Ordinal)).ToList();

    public long BeginNavigation() { IsLoading = true; Message = "Загрузка папки…"; return ++navigationGeneration; }
    public void InvalidateNavigation() { navigationGeneration++; IsLoading = false; }
    public bool IsCurrent(long version) => version == navigationGeneration;
    public bool CommitNavigation(long version, string path, IEnumerable<RemoteEntry> result, bool addHistory, IEnumerable<string> previousSelection, double previousOffset, int? targetHistoryIndex = null)
    {
        if (!IsCurrent(version)) return false;
        var priorPath = CurrentPath; var priorSelection = previousSelection.ToList();
        if (HistoryIndex >= 0 && HistoryIndex < History.Count) History[HistoryIndex] = new BrowserHistoryItem(priorPath, priorSelection, previousOffset);
        if (addHistory)
        {
            if (HistoryIndex < History.Count - 1) History.RemoveRange(HistoryIndex + 1, History.Count - HistoryIndex - 1);
            History.Add(new BrowserHistoryItem(path, [], 0)); HistoryIndex = History.Count - 1;
        }
        else if (targetHistoryIndex is int index) HistoryIndex = Math.Clamp(index, 0, Math.Max(0, History.Count - 1));
        var existing = priorPath == path ? Entries.ToDictionary(r => r.Entry.FullPath, StringComparer.Ordinal) : [];
        CurrentPath = path; Entries = result.Select(e => { if (!existing.TryGetValue(e.FullPath, out var row)) return new FileItemViewModel(e); row.UpdateEntry(e); return row; }).ToList(); SearchQuery = "";
        var restore = targetHistoryIndex is int && HistoryIndex >= 0 ? History[HistoryIndex].Selection : priorPath == path ? priorSelection : [];
        SetSelection(restore); IsLoading = false; Message = Entries.Count == 0 ? "Эта папка пуста." : ""; return true;
    }
    public void FailNavigation(long version, string requestedPath, string reason)
    {
        if (!IsCurrent(version)) return; IsLoading = false; Message = $"Не удалось открыть «{requestedPath}»: {reason}";
    }
    public (int Index, BrowserHistoryItem Item)? HistoryTarget(int delta)
    {
        var index = HistoryIndex + delta; return index >= 0 && index < History.Count ? (index, History[index]) : null;
    }
    public void SetEntries(List<FileItemViewModel> result) { Entries = result; SetSelection(selectedPaths.ToArray()); }
    public void SetSelection(IEnumerable<string> paths)
    {
        var available = Entries.Select(r => r.Entry.FullPath).ToHashSet(StringComparer.Ordinal); selectedPaths = paths.Where(available.Contains).Distinct(StringComparer.Ordinal).ToList();
        Raise(nameof(SelectedPaths)); Raise(nameof(SelectedEntries)); UpdateStatus();
        RaiseAvailability();
    }
    void RaiseAvailability() { Raise(nameof(CanCreate)); Raise(nameof(HasSelection)); Raise(nameof(CanRename)); Raise(nameof(CanGoUp)); Raise(nameof(CanGoBack)); Raise(nameof(CanGoForward)); }
    public void ApplyFilter(bool showHidden, bool showExtensions)
    {
        var query = SearchQuery; var rows = Entries.Where(r => (showHidden || !r.Entry.Name.StartsWith('.')) && (query.Length == 0 || r.Entry.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))).ToList();
        foreach (var row in rows) row.ShowExtensions = showExtensions;
        rows.Sort(new FileItemComparer(SortKey, Descending)); VisibleEntries = rows;
        var visiblePaths = rows.Select(r => r.Entry.FullPath).ToHashSet(StringComparer.Ordinal); SetSelection(selectedPaths.Where(visiblePaths.Contains).ToArray()); UpdateStatus();
    }
    void UpdateStatus()
    {
        var selected = SelectedEntries; var bytes = selected.Where(r => r.Entry.IsRegularFile && !r.Entry.IsSymbolicLink && r.Entry.Length >= 0).Sum(r => r.Entry.Length);
        StatusText = $"Элементов: {VisibleEntries.Count}" + (selected.Count > 0 ? $"     Выбрано: {selected.Count}     {Ui.Size(bytes)}" : "");
    }
    public BrowserContext Capture(double offset) => new(CurrentPath, History.ToList(), HistoryIndex, selectedPaths.ToList(), offset, ViewMode);
    public bool Restore(BrowserContext context)
    {
        if (context.Path != CurrentPath) return false; History.Clear(); History.AddRange(context.History); HistoryIndex = context.Index; ViewMode = context.Mode; SetSelection(context.Selection); return true;
    }
}

public sealed class FileItemComparer(string key, bool descending) : IComparer<FileItemViewModel>
{
    public int Compare(FileItemViewModel? x, FileItemViewModel? y)
    {
        if (x is null || y is null) return 0; if (x.Entry.IsDirectory != y.Entry.IsDirectory) return x.Entry.IsDirectory ? -1 : 1;
        var value = key switch { "Modified" => x.Entry.LastWriteTimeUtc.CompareTo(y.Entry.LastWriteTimeUtc), "Size" => x.Entry.Length.CompareTo(y.Entry.Length), "Type" => StringComparer.CurrentCultureIgnoreCase.Compare(x.Type, y.Type), "Permissions" => x.Entry.Permissions.CompareTo(y.Entry.Permissions), "Owner" => x.Entry.UserId.CompareTo(y.Entry.UserId), "Group" => x.Entry.GroupId.CompareTo(y.Entry.GroupId), _ => NaturalNameComparer.Instance.Compare(x.Name, y.Name) };
        if (value == 0 && key != "Name") return NaturalNameComparer.Instance.Compare(x.Name, y.Name); // stable secondary key, always A→Z
        return descending ? -value : value;
    }
}

public sealed class NaturalNameComparer : IComparer<string>
{
    public static NaturalNameComparer Instance { get; } = new();
    public int Compare(string? a, string? b)
    {
        if (a is null || b is null) return StringComparer.Ordinal.Compare(a, b); int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j])) { var ia = i; var jb = j; while (i < a.Length && char.IsAsciiDigit(a[i])) i++; while (j < b.Length && char.IsAsciiDigit(b[j])) j++; var sa = a[ia..i].TrimStart('0'); var sb = b[jb..j].TrimStart('0'); var n = sa.Length.CompareTo(sb.Length); if (n == 0) n = StringComparer.Ordinal.Compare(sa, sb); if (n != 0) return n; }
            else { var n = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j])); if (n != 0) return n; i++; j++; }
        }
        var end = (a.Length - i).CompareTo(b.Length - j); return end == 0 ? StringComparer.Ordinal.Compare(a, b) : end;
    }
}
