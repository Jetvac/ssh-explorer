namespace SSHExplorer.Monitoring;

public sealed record ExecResult(string Output, string Error, int ExitStatus);

public sealed record ProcessIdentity(int Pid, ulong StartTimeTicks, uint UserId);

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
    public bool CanSignal => Identity.UserId != uint.MaxValue;
    public string UnavailableReason => "Недостаточно прав, процесс завершился или ещё нет двух замеров";
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
    public double? MemoryPercent => MemoryTotalBytes is > 0 && MemoryUsedBytes is { } used
        ? used * 100d / MemoryTotalBytes.Value : null;
    public const string CpuScale = "CPU процесса: доля видимых CPU, 0–100% всей машины";
    public const string ObservationScope = "Видимое пространство имён Linux (хост или контейнер)";
}

public sealed record NetworkMetric(string Name, ulong ReceivedBytes, ulong SentBytes,
    double? ReceiveBytesPerSecond, double? SendBytesPerSecond);

public sealed record DiskMetric(string Name, ulong ReadBytes, ulong WrittenBytes,
    double? ReadBytesPerSecond, double? WriteBytesPerSecond);

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
    public const string MissingValueText = "Недоступно";
    public string CpuScale => SystemMetric.CpuScale;
    public string ObservationScope => SystemMetric.ObservationScope;
}

public sealed record HistoryPoint(DateTimeOffset Timestamp, double? CpuPercent,
    double? MemoryPercent, double? ReceiveBytesPerSecond, double? SendBytesPerSecond);

public sealed record MonitorCapabilities(bool HasHtop, bool HasTop);

public sealed class MonitoringUnavailableException(string message) : Exception(message);

public sealed class ProcessChangedException(string message) : Exception(message);
