using SSHExplorer.Monitoring;
using System.Collections.ObjectModel;

namespace SSHExplorer.App.ViewModels;

public sealed class MonitorViewModel : ObservableObject
{
    MonitorSnapshot? snapshot;
    string searchQuery = "", freshness = "Ещё нет замера";
    readonly Dictionary<ProcessIdentity, ProcessItemViewModel> processRows = [];
    ProcessIdentity? selectedIdentity;
    bool isActive, isBusy;
    public MonitorSnapshot? Snapshot { get => snapshot; private set => Set(ref snapshot, value); }
    public string SearchQuery { get => searchQuery; set { if (Set(ref searchQuery, value)) RebuildProcesses(); } }
    public string Freshness { get => freshness; private set => Set(ref freshness, value); }
    public bool IsActive { get => isActive; set => Set(ref isActive, value); }
    public bool IsBusy { get => isBusy; set => Set(ref isBusy, value); }
    public ObservableCollection<ProcessItemViewModel> Processes { get; } = [];
    public event Action? ProcessesUpdated;
    public ProcessIdentity? SelectedIdentity { get => selectedIdentity; set { if (Set(ref selectedIdentity, value)) Raise(nameof(CanSignalSelected)); } }
    public bool CanSignalSelected => SelectedIdentity is not null && Processes.Any(p => p.Metric.Identity == SelectedIdentity && p.Metric.CanSignal);
    public string Summary => Snapshot is null ? "" : $"ЦП: {Percent(Snapshot.System.CpuPercent)}    Память: {Percent(Snapshot.System.MemoryPercent)}";
    public string CpuText => Percent(Snapshot?.System.CpuPercent);
    public string MemoryText => Percent(Snapshot?.System.MemoryPercent);
    public string CpuHeader => Snapshot?.System.CpuPercent is null ? "—" : CpuText;
    public string MemoryHeader => Snapshot?.System.MemoryPercent is null ? "—" : MemoryText;
    public string Limitations => Snapshot is null ? "" : string.Join(" · ", Snapshot.Limitations);
    public string SystemInfo
    {
        get
        {
            if (Snapshot is null) return ""; var s = Snapshot.System;
            return $"CPU: {(s.CpuCount > 0 ? s.CpuCount.ToString() : "Недоступно")} · Память: {Ui.Size(s.MemoryUsedBytes)} / {Ui.Size(s.MemoryTotalBytes)}" + (s.MemoryIsEstimate ? " (оценка)" : "")
                + $"\nSwap: {Ui.Size(s.SwapUsedBytes)} / {Ui.Size(s.SwapTotalBytes)} · Uptime: {(s.Uptime is { } t ? $"{(int)t.TotalDays} дн. {t:hh\\:mm\\:ss}" : "Недоступно")}\nLoad average: {Number(s.Load1)}  {Number(s.Load5)}  {Number(s.Load15)}";
        }
    }
    public ObservableCollection<NetworkRow> Networks { get; } = [];
    public ObservableCollection<DiskRow> Disks { get; } = [];
    public ObservableCollection<StorageRow> Storage { get; } = [];
    public double?[] CpuHistory { get; private set; } = [];
    public double?[] MemoryHistory { get; private set; } = [];
    public void ApplySnapshot(MonitorSnapshot value, IEnumerable<HistoryPoint> history)
    {
        Snapshot = value; Freshness = $"Последний успешный замер: {value.Timestamp.LocalDateTime:HH:mm:ss} · {value.Processes.Count} процессов";
        Reconcile(Networks, value.Networks.Select(x => new NetworkRow(x.Name, Speed(x.ReceiveBytesPerSecond), Speed(x.SendBytesPerSecond))).ToArray());
        Reconcile(Disks, value.Disks.Select(x => new DiskRow(x.Name, Speed(x.ReadBytesPerSecond), Speed(x.WriteBytesPerSecond))).ToArray());
        Reconcile(Storage, value.Storage.Select(x => new StorageRow(x.FileSystem, x.MountPoint, Ui.Size(x.TotalBytes), Ui.Size(x.AvailableBytes))).ToArray());
        var points = history.ToArray(); CpuHistory = points.Select(h => h.CpuPercent).ToArray(); MemoryHistory = points.Select(h => h.MemoryPercent).ToArray();
        foreach (var name in new[] { nameof(Summary), nameof(CpuText), nameof(MemoryText), nameof(CpuHeader), nameof(MemoryHeader), nameof(Limitations), nameof(SystemInfo), nameof(CpuHistory), nameof(MemoryHistory) }) Raise(name);
        RebuildProcesses();
    }
    public void ReportFailure(string reason) => Freshness = "Данные устарели. " + (Snapshot is null ? "Успешных замеров нет. " : $"Последний замер {Snapshot.Timestamp.LocalDateTime:HH:mm:ss}. ") + reason;
    void RebuildProcesses()
    {
        if (Snapshot is null) { Processes.Clear(); return; } var query = SearchQuery;
        var priorIdentity = SelectedIdentity;
        var identities = Snapshot.Processes.Select(x => x.Identity).ToHashSet();
        foreach (var old in processRows.Keys.Where(x => !identities.Contains(x)).ToArray()) processRows.Remove(old);
        foreach (var metric in Snapshot.Processes)
        {
            if (processRows.TryGetValue(metric.Identity, out var row)) row.Update(metric, Snapshot.System.MemoryTotalBytes);
            else processRows[metric.Identity] = new ProcessItemViewModel(metric, Snapshot.System.MemoryTotalBytes);
        }
        var visible = Snapshot.Processes.Where(x => query.Length == 0 || (x.Name + " " + x.Pid + " " + x.User).Contains(query, StringComparison.CurrentCultureIgnoreCase)).Select(x => processRows[x.Identity]).ToArray();
        Reconcile(Processes, visible);
        SelectedIdentity = priorIdentity is not null && Processes.Any(p => p.Metric.Identity == priorIdentity) ? priorIdentity : null;
        Raise(nameof(CanSignalSelected));
        ProcessesUpdated?.Invoke();
    }
    static void Reconcile<T>(ObservableCollection<T> target, IReadOnlyList<T> desired)
    {
        var wanted = desired.ToHashSet();
        for (int i = target.Count - 1; i >= 0; i--) if (!wanted.Contains(target[i])) target.RemoveAt(i);
        for (int i = 0; i < desired.Count; i++)
        {
            if (i < target.Count && EqualityComparer<T>.Default.Equals(target[i], desired[i])) continue;
            var existing = target.IndexOf(desired[i]); if (existing >= 0) target.Move(existing, i); else target.Insert(i, desired[i]);
        }
    }
    public static string Percent(double? v) => v is { } n ? $"{n:0.0}%" : "Недоступно";
    public static string Number(double? v) => v is { } n ? $"{n:0.00}" : "Недоступно";
    public static string Speed(double? v) => v is { } n ? Ui.Size((long)n) + "/с" : "Недоступно";
}

public sealed record NetworkRow(string Name, string Receive, string Send);
public sealed record DiskRow(string Name, string Read, string Write);
public sealed record StorageRow(string FileSystem, string MountPoint, string Total, string Available);
public sealed class ProcessItemViewModel(ProcessMetric metric, long? totalMemory) : ObservableObject
{
    ProcessMetric current = metric;
    long? memoryTotal = totalMemory;
    public ProcessMetric Metric => current;
    public string Name => current.Name;
    public int Pid => current.Pid;
    public string User => current.User;
    public string State => current.State;
    public double? Cpu => current.CpuPercent;
    public string CpuText => MonitorViewModel.Percent(Cpu);
    public double? MemoryFraction => memoryTotal is > 0 && current.MemoryBytes is { } n ? n * 100d / memoryTotal.Value : null;
    public string MemoryText => current.MemoryBytes is { } b ? Ui.Size(b) : "Недоступно";
    public string ReadText => MonitorViewModel.Speed(current.ReadBytesPerSecond);
    public string WriteText => MonitorViewModel.Speed(current.WriteBytesPerSecond);
    public void Update(ProcessMetric value, long? total)
    {
        if (current == value && memoryTotal == total) return;
        current = value; memoryTotal = total; Raise(null);
    }
}
