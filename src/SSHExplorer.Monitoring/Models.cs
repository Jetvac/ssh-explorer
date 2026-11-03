namespace SSHExplorer.Monitoring;

public sealed record ExecResult(string Output, string Error, int ExitStatus);

public sealed record ProcessIdentity(int Pid, ulong StartTimeTicks, uint UserId);

public enum MetricState { Available, Initial, NewProcess, Unavailable, Reset, Stale }
public sealed record MetricStatus(MetricState State, string Reason)
{
    public static readonly MetricStatus Measured = new(MetricState.Available, "Доступен текущий замер.");
}

public static class ProcessSignalPolicy
{
    public static bool CanSignal(ProcessIdentity identity) => DisabledReason(identity) is null;
    public static string? DisabledReason(ProcessIdentity identity) => identity.Pid <= 1 ? "PID 1 и системные идентификаторы защищены."
        : identity.StartTimeTicks == 0 ? "Время запуска процесса недоступно; идентичность нельзя проверить."
        : identity.UserId == uint.MaxValue ? "Владелец процесса недоступен; идентичность нельзя проверить." : null;
}

public sealed record ProcessMetric(
    ProcessIdentity Identity,
    string Name,
    string User,
    string State,
    double? CpuPercent,
    long? MemoryBytes,
    double? ReadBytesPerSecond,
    double? WriteBytesPerSecond,
    string? CommandLine)
{
    public int Pid => Identity.Pid;
    public bool CanSignal => ProcessSignalPolicy.CanSignal(Identity);
    MetricStatus? cpuStatus, memoryStatus, readStatus, writeStatus;
    public MetricStatus CpuStatus { get => cpuStatus ?? (CpuPercent.HasValue ? MetricStatus.Measured : new(MetricState.Initial,"Для CPU нужны два согласованных замера процесса.")); init => cpuStatus = value; }
    public MetricStatus MemoryStatus { get => memoryStatus ?? (MemoryBytes.HasValue ? MetricStatus.Measured : new(MetricState.Unavailable,"Память процесса недоступна в /proc/PID/status и stat.")); init => memoryStatus = value; }
    public MetricStatus ReadStatus { get => readStatus ?? (ReadBytesPerSecond.HasValue ? MetricStatus.Measured : new(MetricState.Unavailable,"Нет измерения чтения /proc/PID/io.")); init => readStatus = value; }
    public MetricStatus WriteStatus { get => writeStatus ?? (WriteBytesPerSecond.HasValue ? MetricStatus.Measured : new(MetricState.Unavailable,"Нет измерения записи /proc/PID/io.")); init => writeStatus = value; }
    public string UnavailableReason => string.Join(" ", new[] { CpuStatus, MemoryStatus, ReadStatus, WriteStatus }.Where(s => s.State != MetricState.Available).Select(s => s.Reason).Distinct());
}

public sealed record SystemMetric(
    double? CpuPercent,
    int CpuCount,
    long? MemoryTotalBytes,
    long? MemoryAvailableBytes,
    long? MemoryUsedBytes,
    long? SwapTotalBytes,
    long? SwapUsedBytes,
    TimeSpan? Uptime,
    double? Load1,
    double? Load5,
    double? Load15,
    int? ClockTicksPerSecond,
    bool MemoryIsEstimate)
{
    MetricStatus? cpuStatus, memoryStatus;
    public MetricStatus CpuStatus { get => cpuStatus ?? (CpuPercent.HasValue ? MetricStatus.Measured : new(MetricState.Initial,"Для CPU нужны два замера /proc/stat.")); init => cpuStatus = value; }
    public MetricStatus MemoryStatus { get => memoryStatus ?? (MemoryPercent.HasValue ? MetricStatus.Measured : new(MetricState.Unavailable,"Полные данные памяти из /proc/meminfo недоступны.")); init => memoryStatus = value; }
    public double? MemoryPercent => MemoryTotalBytes is > 0 && MemoryUsedBytes is { } used
        ? used * 100d / MemoryTotalBytes.Value : null;
    public const string CpuScale = "CPU процесса: доля видимых CPU, 0–100% всей машины";
    public const string ObservationScope = "Видимое пространство имён Linux (хост или контейнер)";
}

public sealed record NetworkMetric(string Name, ulong ReceivedBytes, ulong SentBytes,
    double? ReceiveBytesPerSecond, double? SendBytesPerSecond)
{
    MetricStatus? receiveStatus, sendStatus;
    public MetricStatus ReceiveStatus { get => receiveStatus ?? (ReceiveBytesPerSecond.HasValue ? MetricStatus.Measured : new(MetricState.Unavailable,"Нет измерения скорости получения.")); init => receiveStatus=value; }
    public MetricStatus SendStatus { get => sendStatus ?? (SendBytesPerSecond.HasValue ? MetricStatus.Measured : new(MetricState.Unavailable,"Нет измерения скорости отправки.")); init => sendStatus=value; }
}

public sealed record DiskMetric(string Name, ulong ReadBytes, ulong WrittenBytes,
    double? ReadBytesPerSecond, double? WriteBytesPerSecond)
{
    MetricStatus? readStatus, writeStatus;
    public MetricStatus ReadStatus { get => readStatus ?? (ReadBytesPerSecond.HasValue ? MetricStatus.Measured : new(MetricState.Unavailable,"Нет измерения скорости чтения.")); init => readStatus=value; }
    public MetricStatus WriteStatus { get => writeStatus ?? (WriteBytesPerSecond.HasValue ? MetricStatus.Measured : new(MetricState.Unavailable,"Нет измерения скорости записи.")); init => writeStatus=value; }
}

public sealed record StorageMetric(string FileSystem, string MountPoint, long TotalBytes,
    long UsedBytes, long AvailableBytes);

public sealed record MonitorSnapshot(
    DateTimeOffset Timestamp,
    SystemMetric System,
    IReadOnlyList<ProcessMetric> Processes,
    IReadOnlyList<NetworkMetric> Networks,
    IReadOnlyList<DiskMetric> Disks,
    IReadOnlyList<StorageMetric> Storage,
    IReadOnlyList<string> Limitations)
{
    public const string MissingValueText = "—";
    public TimeSpan CollectionDuration { get; init; }
    public string CpuScale => SystemMetric.CpuScale;
    public string ObservationScope => SystemMetric.ObservationScope;
}

public sealed record HistoryPoint(DateTimeOffset Timestamp, double? CpuPercent,
    double? MemoryPercent, double? ReceiveBytesPerSecond, double? SendBytesPerSecond)
{
    public bool IsGap { get; init; }
    public string? Reason { get; init; }
}

public sealed record MonitorCapabilities(bool HasHtop, bool HasTop);

public sealed class MonitoringUnavailableException(string message) : Exception(message);

public sealed class ProcessChangedException(string message) : Exception(message);
