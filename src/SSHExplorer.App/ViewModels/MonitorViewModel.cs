using SSHExplorer.Monitoring;
using System.Collections.ObjectModel;

namespace SSHExplorer.App.ViewModels;

public sealed class MonitorViewModel : ObservableObject
{
    MonitorSnapshot? snapshot;
    string searchQuery = "", failure = "";
    readonly Dictionary<ProcessIdentity, ProcessItemViewModel> processRows = [];
    readonly Func<DateTimeOffset> clock;
    ProcessIdentity? selectedIdentity;
    bool isActive, isBusy, isStale, isCommandRunning, requiresFreshSnapshot;
    public MonitorViewModel(Func<DateTimeOffset>? clock = null) => this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    public MonitorSnapshot? Snapshot { get => snapshot; private set => Set(ref snapshot, value); }
    public string SearchQuery { get => searchQuery; set { if (Set(ref searchQuery, value)) RebuildProcesses(); } }
    public bool IsActive
    {
        get => isActive;
        set
        {
            if (!Set(ref isActive, value)) return;
            if (!value && Snapshot is not null) { requiresFreshSnapshot=true;AppendGap(clock(), "Сбор приостановлен: раздел скрыт."); }
            RaiseStatus();
        }
    }
    public bool IsBusy { get => isBusy; set { if(Set(ref isBusy,value)) RaiseStatus(); } }
    public bool IsCommandRunning => isCommandRunning;
    public bool IsStale => isStale;
    public bool HasError => failure.Length > 0;
    public string StatusText
    {
        get
        {
            string state = HasError ? (Snapshot is null ? "Ошибка сбора" : "Данные устарели")
                : !IsActive ? (Snapshot is null ? "Ещё нет замера" : "Пауза")
                : IsCommandRunning ? "Выполняем команду…" : Snapshot is null || Snapshot.System.CpuStatus.State is MetricState.Initial or MetricState.Reset || History.LastOrDefault()?.IsGap == true ? "Собираем данные…"
                : IsBusy ? "Обновляем…" : "Обновлено";
            return Snapshot is null ? state : $"{state} · {MetricFormatter.Time(Snapshot.Timestamp)} · {Snapshot.Processes.Count} процессов";
        }
    }
    public string Freshness => StatusText;
    public string StatusDetails => (Snapshot is null ? "Успешных замеров пока нет." : $"Последний успешный замер: {MetricFormatter.Time(Snapshot.Timestamp)}.")
        + (HasError ? " " + failure : !IsActive && Snapshot is not null ? " Сбор приостановлен; значения сохранены, новые точки не добавляются." : "")
        + (Snapshot is null ? "" : $" CPU: {CpuReason} Память: {MemoryReason}");
    public string DataDetails => SystemMetric.ObservationScope + ". " + SystemMetric.CpuScale + ".\n"
        + "CPU: /proc/stat и /proc/PID/stat. Память: /proc/meminfo и /proc/PID/status/stat. Скорости: разности счётчиков и /proc/uptime.\n"
        + "Чтение /proc/PID/io зависит от прав. Сеть измеряется по интерфейсам, не по процессам.\n"
        + (Snapshot is null ? "Успешных замеров пока нет." : $"Длительность последнего сбора: {MetricFormatter.Seconds(Snapshot.CollectionDuration)}.\n{SystemInfo}\n{string.Join("\n",Snapshot.Limitations)}");
    public bool HasDataDetails => true;
    public string CpuReason => Reason(Snapshot?.System.CpuStatus, Snapshot?.System.CpuPercent.HasValue == true);
    public string MemoryReason => Reason(Snapshot?.System.MemoryStatus, Snapshot?.System.MemoryPercent.HasValue == true);
    string Reason(MetricStatus? status, bool measured) => (IsStale && measured ? "Данные устарели. " : "") + (status?.Reason ?? "Первоначальный сбор ещё не завершён.");
    public ObservableCollection<ProcessItemViewModel> Processes { get; } = [];
    public event Action? ProcessesUpdated;
    public ProcessIdentity? SelectedIdentity { get => selectedIdentity; set { if (Set(ref selectedIdentity, value)) { Raise(nameof(CanSignalSelected)); Raise(nameof(SignalDisabledReason)); } } }
    public bool CanSignalSelected => IsActive && !IsCommandRunning && !IsStale && !requiresFreshSnapshot && SelectedIdentity is not null && Processes.Any(p => p.Metric.Identity == SelectedIdentity && ProcessSignalPolicy.CanSignal(p.Metric.Identity));
    public string SignalDisabledReason => !IsActive ? "Сбор приостановлен. Вернитесь к актуальным данным перед отправкой сигнала."
        : IsCommandRunning ? "Дождитесь завершения команды над процессом." : IsStale ? "Данные устарели. Обновите список перед отправкой сигнала."
        : requiresFreshSnapshot ? "После паузы дождитесь нового успешного замера."
        : SelectedIdentity is null ? "Выберите процесс." : ProcessSignalPolicy.DisabledReason(SelectedIdentity) ?? "Права и идентичность будут повторно проверены сервером; автоматического sudo нет.";
    public string Summary => Snapshot is null ? "" : $"ЦП: {CpuText}    Память: {MemoryText}";
    public string CpuText => MetricFormatter.AggregatePercent(Snapshot?.System.CpuPercent);
    public string MemoryText => MetricFormatter.AggregatePercent(Snapshot?.System.MemoryPercent);
    public string CpuHeader => MetricFormatter.AggregatePercent(Snapshot?.System.CpuPercent);
    public string MemoryHeader => MetricFormatter.AggregatePercent(Snapshot?.System.MemoryPercent);
    public string Limitations => Snapshot is null ? "" : string.Join(" · ", Snapshot.Limitations);
    public string SystemInfo
    {
        get
        {
            if (Snapshot is null) return ""; var s = Snapshot.System;
            return $"CPU: {(s.CpuCount > 0 ? s.CpuCount.ToString() : "—")} · Память: {Size(s.MemoryUsedBytes)} / {Size(s.MemoryTotalBytes)}" + (s.MemoryIsEstimate ? " (оценка)" : "")
                + $"\nSwap: {Size(s.SwapUsedBytes)} / {Size(s.SwapTotalBytes)} · Uptime: {MetricFormatter.Duration(s.Uptime)}\nLoad average: {Number(s.Load1)}  {Number(s.Load5)}  {Number(s.Load15)}";
        }
    }
    public ObservableCollection<NetworkRow> Networks { get; } = [];
    public ObservableCollection<DiskRow> Disks { get; } = [];
    public ObservableCollection<StorageRow> Storage { get; } = [];
    public IReadOnlyList<HistoryPoint> History { get; private set; } = [];
    public double?[] CpuHistory => History.Select(p => p.CpuPercent).ToArray();
    public double?[] MemoryHistory => History.Select(p => p.MemoryPercent).ToArray();
    public void ApplySnapshot(MonitorSnapshot value, IEnumerable<HistoryPoint> history)
    {
        Snapshot = value; failure = ""; isStale = false;requiresFreshSnapshot=false;
        Reconcile(Networks, value.Networks.Select(x => new NetworkRow(x.Name, Speed(x.ReceiveBytesPerSecond), Speed(x.SendBytesPerSecond), x.ReceiveStatus.Reason, x.SendStatus.Reason)).ToArray());
        Reconcile(Disks, value.Disks.Select(x => new DiskRow(x.Name, Speed(x.ReadBytesPerSecond), Speed(x.WriteBytesPerSecond),x.ReadStatus.Reason,x.WriteStatus.Reason)).ToArray());
        Reconcile(Storage, value.Storage.Select(x => new StorageRow(x.FileSystem, x.MountPoint, Size(x.TotalBytes), Size(x.AvailableBytes))).ToArray());
        History = history.ToArray(); RaiseHistory();
        foreach (var name in new[] { nameof(Summary), nameof(CpuText), nameof(MemoryText), nameof(CpuHeader), nameof(MemoryHeader), nameof(Limitations), nameof(SystemInfo) }) Raise(name);
        RebuildProcesses(); RaiseStatus();
    }
    public void ReportFailure(string reason, IEnumerable<HistoryPoint>? history = null)
    {
        failure = reason; isStale = Snapshot is not null;
        if(history is not null) { History=history.ToArray(); RaiseHistory(); }
        else AppendGap(clock(), "Последний замер не удался.");
        foreach(var row in processRows.Values) row.SetStale(IsStale);
        RaiseStatus();
    }
    void AppendGap(DateTimeOffset timestamp,string reason)
    { History=History.Append(new HistoryPoint(timestamp,null,null,null,null){IsGap=true,Reason=reason}).TakeLast(120).ToArray(); RaiseHistory(); }
    void RaiseHistory() { Raise(nameof(History)); Raise(nameof(CpuHistory)); Raise(nameof(MemoryHistory)); }
    void RaiseStatus()
    { foreach(var name in new[]{nameof(StatusText),nameof(Freshness),nameof(StatusDetails),nameof(DataDetails),nameof(HasError),nameof(IsStale),nameof(CpuReason),nameof(MemoryReason),nameof(CanSignalSelected),nameof(SignalDisabledReason),nameof(IsCommandRunning)}) Raise(name); }
    public async Task<bool> SignalSelectedAsync(int signal,Func<ProcessMetric,CancellationToken,Task<bool>> confirm,
        Func<ProcessIdentity,int,CancellationToken,Task> send,CancellationToken ct=default)
    {
        if(signal is not (9 or 15))throw new ArgumentOutOfRangeException(nameof(signal));
        ct.ThrowIfCancellationRequested();
        if(!CanSignalSelected)return false;
        var target=Snapshot?.Processes.FirstOrDefault(p=>p.Identity==SelectedIdentity);
        if(target is null)return false;
        isCommandRunning=true;RaiseStatus();
        try
        {
            if(!await confirm(target,ct).WaitAsync(ct))return false;
            ct.ThrowIfCancellationRequested();
            if(!IsActive||IsStale||requiresFreshSnapshot||Snapshot?.Processes.Any(p=>p.Identity==target.Identity&&ProcessSignalPolicy.CanSignal(p.Identity))!=true)
                throw new ProcessChangedException("Процесс завершился, изменился или данные больше не актуальны. Сигнал не отправлен.");
            await send(target.Identity,signal,ct);return true;
        }
        catch(ProcessChangedException){if(SelectedIdentity==target.Identity)SelectedIdentity=null;throw;}
        finally { isCommandRunning=false;RaiseStatus(); }
    }
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
            processRows[metric.Identity].SetStale(IsStale);
        }
        var visible = Snapshot.Processes.Where(x => query.Length == 0 || (x.Name + " " + x.Pid + " " + x.User).Contains(query, StringComparison.CurrentCultureIgnoreCase)).Select(x => processRows[x.Identity]).ToArray();
        Reconcile(Processes, visible);
        SelectedIdentity = priorIdentity is not null && Processes.Any(p => p.Metric.Identity == priorIdentity) ? priorIdentity : null;
        Raise(nameof(CanSignalSelected)); ProcessesUpdated?.Invoke();
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
    public static string Percent(double? v) => MetricFormatter.ProcessPercent(v);
    public static string Number(double? v) => MetricFormatter.Number(v);
    public static string Speed(double? v) => MetricFormatter.Speed(v);
    static string Size(long? value) => MetricFormatter.Size(value);
}

public sealed record NetworkRow(string Name, string Receive, string Send, string ReceiveReason = "", string SendReason = "");
public sealed record DiskRow(string Name, string Read, string Write, string ReadReason = "", string WriteReason = "");
public sealed record StorageRow(string FileSystem, string MountPoint, string Total, string Available);
public sealed class ProcessItemViewModel(ProcessMetric metric, long? totalMemory) : ObservableObject
{
    ProcessMetric current = metric;
    long? memoryTotal = totalMemory;
    bool isStale;
    public ProcessMetric Metric => current;
    public string Name => current.Name;
    public int Pid => current.Pid;
    public string User => current.User;
    public string State => current.State;
    public bool IsStale => isStale;
    public double? Cpu => current.CpuPercent;
    public long? MemoryBytes => current.MemoryBytes;
    public double? ReadBytesPerSecond => current.ReadBytesPerSecond;
    public double? WriteBytesPerSecond => current.WriteBytesPerSecond;
    public string CpuText => MonitorViewModel.Percent(Cpu);
    public double? MemoryFraction => memoryTotal is > 0 && current.MemoryBytes is { } n ? n * 100d / memoryTotal.Value : null;
    public string MemoryText => MetricFormatter.Size(current.MemoryBytes);
    public string ReadText => MonitorViewModel.Speed(ReadBytesPerSecond);
    public string WriteText => MonitorViewModel.Speed(WriteBytesPerSecond);
    public string CpuReason => Reason(current.CpuStatus);
    public string MemoryReason => Reason(current.MemoryStatus) + (current.MemoryBytes.HasValue && memoryTotal is not > 0 ? " Общая память недоступна: доля не вычисляется." : "");
    public string ReadReason => Reason(current.ReadStatus);
    public string WriteReason => Reason(current.WriteStatus);
    public MetricState CpuState => IsStale && Cpu.HasValue ? MetricState.Stale : current.CpuStatus.State;
    public MetricState MemoryState => IsStale && current.MemoryBytes.HasValue ? MetricState.Stale : current.MemoryStatus.State;
    public MetricState ReadState => IsStale && ReadBytesPerSecond.HasValue ? MetricState.Stale : current.ReadStatus.State;
    public MetricState WriteState => IsStale && WriteBytesPerSecond.HasValue ? MetricState.Stale : current.WriteStatus.State;
    string Reason(MetricStatus status) => (IsStale ? "Данные устарели. " : "") + status.Reason;
    public void SetStale(bool value) { if(isStale==value)return;isStale=value;Raise(null); }
    public void Update(ProcessMetric value, long? total)
    {
        if (current == value && memoryTotal == total) return;
        current = value; memoryTotal = total; Raise(null);
    }
}
