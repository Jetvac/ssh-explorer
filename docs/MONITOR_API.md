# Monitoring API (фиксированный контракт)

Проект `src/SSHExplorer.Monitoring`, namespace `SSHExplorer.Monitoring`, без зависимости от Core/UI.

```csharp
var monitor = new LinuxMonitor(async (command, ct) => {
    var r = await session.ExecuteAsync(command, ct);
    return new ExecResult(r.Output, r.Error, r.ExitStatus);
});
MonitorSnapshot snapshot = await monitor.SampleAsync(ct);
await monitor.SignalAsync(selected.Identity, 15, ct); // UI СНАЧАЛА подтверждает SIGTERM
await monitor.SignalAsync(selected.Identity, 9, ct);  // отдельное явное подтверждение SIGKILL
MonitorCapabilities caps = await monitor.GetCapabilitiesAsync(ct);
```

`LinuxMonitor`: constructor `(Func<string,CancellationToken,Task<ExecResult>> execute, int maxProcesses=2048, int historyCapacity=120)`; `SampleAsync(ct)`, `SignalAsync(ProcessIdentity,int,ct)`, `GetCapabilitiesAsync(ct)`, `History` (bounded list), `Reset()`.

`MonitorSnapshot`: `Timestamp`, `System`, `Processes`, `Networks`, `Disks`, `Storage`, `Limitations`, `CpuScale`, `ObservationScope`.

`SystemMetric`: nullable `CpuPercent,MemoryPercent,MemoryTotalBytes,MemoryAvailableBytes,MemoryUsedBytes,SwapTotalBytes,SwapUsedBytes,Uptime,Load1,Load5,Load15,ClockTicksPerSecond`, int `CpuCount`, bool `MemoryIsEstimate`.

`ProcessMetric`: `Identity` (`Pid,StartTimeTicks,UserId`), `Pid`, `Name`, `User` (имя или UID), `State`, nullable `CpuPercent,MemoryBytes,ReadBytesPerSecond,WriteBytesPerSecond,CommandLine`. Значение `null` показывать «Недоступно» с причиной, первый CPU замер — ожидание второго. Масштаб CPU процесса — 0–100% всех видимых CPU. Сеть дана только для интерфейсов, не PID.

`NetworkMetric(Name,ReceivedBytes,SentBytes,ReceiveBytesPerSecond,SendBytesPerSecond)`, `DiskMetric(Name,ReadBytes,WrittenBytes,ReadBytesPerSecond,WriteBytesPerSecond)`, `StorageMetric(FileSystem,MountPoint,TotalBytes,UsedBytes,AvailableBytes)`.

UI опрашивает только видимый монитор активной вкладки раз в 2 секунды, ждёт завершения запроса, отменяет при закрытии. Нужен показ времени последнего успешного замера и пометка устаревших данных при ошибке. `SampleAsync` дополнительно сериализует запросы; UI не должен копить задачи.

Коллектор — один ограниченный пакетный SSH exec, читает только известные `/proc` поля; не устанавливает компонентов. Требуются существующие `/bin/sh`, `dd`, `base64`, `tr`; отсутствие сообщается точно. Cmdline считывается ограниченно, не журналируется. При превышении лимита PID/размера показывается ограничение. Полное содержимое команды пользователю раскрывается осмысленно, без автоматического журнала.

Перед сигналом повторно проверяются starttime, UID и PID внутри одного короткого shell exec. Это уменьшает риск PID reuse, но между проверкой и kill остаётся гонка без pidfd; атомарность не заявляется. Автоматического sudo нет.
