using SSHExplorer.Monitoring;

namespace SSHExplorer.App.ViewModels;

public sealed class MonitorViewModel : ObservableObject
{
    MonitorSnapshot? snapshot;
    string searchQuery = "", freshness = "Ещё нет замера";
    IReadOnlyList<ProcessItemViewModel> processes = [];
    ProcessIdentity? selectedIdentity;
    bool isActive, isBusy;
    public MonitorSnapshot? Snapshot { get => snapshot; private set => Set(ref snapshot, value); }
    public string SearchQuery { get => searchQuery; set { if (Set(ref searchQuery, value)) RebuildProcesses(); } }
    public string Freshness { get => freshness; private set => Set(ref freshness, value); }
    public bool IsActive { get => isActive; set => Set(ref isActive, value); }
    public bool IsBusy { get => isBusy; set => Set(ref isBusy, value); }
    public IReadOnlyList<ProcessItemViewModel> Processes { get => processes; private set => Set(ref processes, value); }
    public ProcessIdentity? SelectedIdentity { get => selectedIdentity; set => Set(ref selectedIdentity, value); }
    public string Summary => Snapshot is null ? "" : $"ЦП: {Percent(Snapshot.System.CpuPercent)}    Память: {Percent(Snapshot.System.MemoryPercent)}";
    public string CpuText => Percent(Snapshot?.System.CpuPercent);
    public string MemoryText => Percent(Snapshot?.System.MemoryPercent);
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
    public IReadOnlyList<NetworkRow> Networks { get; private set; } = [];
    public IReadOnlyList<DiskRow> Disks { get; private set; } = [];
    public IReadOnlyList<StorageRow> Storage { get; private set; } = [];
    public double?[] CpuHistory { get; private set; } = [];
    public double?[] MemoryHistory { get; private set; } = [];
    public void ApplySnapshot(MonitorSnapshot value, IEnumerable<HistoryPoint> history)
    {
        Snapshot = value; Freshness = $"Последний успешный замер: {value.Timestamp.LocalDateTime:HH:mm:ss} · {value.Processes.Count} процессов";
        Networks = value.Networks.Select(x => new NetworkRow(x.Name, Speed(x.ReceiveBytesPerSecond), Speed(x.SendBytesPerSecond))).ToList();
        Disks = value.Disks.Select(x => new DiskRow(x.Name, Speed(x.ReadBytesPerSecond), Speed(x.WriteBytesPerSecond))).ToList();
        Storage = value.Storage.Select(x => new StorageRow(x.FileSystem, x.MountPoint, Ui.Size(x.TotalBytes), Ui.Size(x.AvailableBytes))).ToList();
        var points = history.ToArray(); CpuHistory = points.Select(h => h.CpuPercent).ToArray(); MemoryHistory = points.Select(h => h.MemoryPercent).ToArray();
        foreach (var name in new[] { nameof(Summary), nameof(CpuText), nameof(MemoryText), nameof(Limitations), nameof(SystemInfo), nameof(Networks), nameof(Disks), nameof(Storage), nameof(CpuHistory), nameof(MemoryHistory) }) Raise(name);
        RebuildProcesses();
    }
    public void ReportFailure(string reason) => Freshness = "Данные устарели. " + (Snapshot is null ? "Успешных замеров нет. " : $"Последний замер {Snapshot.Timestamp.LocalDateTime:HH:mm:ss}. ") + reason;
    void RebuildProcesses()
    {
        if (Snapshot is null) { Processes = []; return; } var query = SearchQuery;
        var priorIdentity = SelectedIdentity;
        Processes = Snapshot.Processes.Where(x => query.Length == 0 || (x.Name + " " + x.Pid + " " + x.User).Contains(query, StringComparison.CurrentCultureIgnoreCase)).Select(x => new ProcessItemViewModel(x, Snapshot.System.MemoryTotalBytes)).ToList();
        SelectedIdentity = priorIdentity is not null && Snapshot.Processes.Any(p => p.Identity == priorIdentity) ? priorIdentity : null;
    }
    public static string Percent(double? v) => v is { } n ? $"{n:0.0}%" : "Недоступно";
    public static string Number(double? v) => v is { } n ? $"{n:0.00}" : "Недоступно";
    public static string Speed(double? v) => v is { } n ? Ui.Size((long)n) + "/с" : "Недоступно";
}

public sealed record NetworkRow(string Name, string Receive, string Send);
public sealed record DiskRow(string Name, string Read, string Write);
public sealed record StorageRow(string FileSystem, string MountPoint, string Total, string Available);
public sealed class ProcessItemViewModel(ProcessMetric metric, long? totalMemory)
{
    public ProcessMetric Metric => metric;
    public string Name => metric.Name;
    public int Pid => metric.Pid;
    public string User => metric.User;
    public string State => metric.State;
    public double? Cpu => metric.CpuPercent;
    public string CpuText => MonitorViewModel.Percent(Cpu);
    public double? MemoryFraction => totalMemory is > 0 && metric.MemoryBytes is { } n ? n * 100d / totalMemory.Value : null;
    public string MemoryText => metric.MemoryBytes is { } b ? Ui.Size(b) : "Недоступно";
    public string ReadText => MonitorViewModel.Speed(metric.ReadBytesPerSecond);
    public string WriteText => MonitorViewModel.Speed(metric.WriteBytesPerSecond);
}
