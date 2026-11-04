using System.Globalization;

namespace SSHExplorer.Monitoring;

/// <summary>Which part of the monitor is visible. Hidden parts are not collected.</summary>
public enum MonitorScope { System, Processes }

/// <summary>
/// Wall-clock time that advances with the monotonic Stopwatch. Durations and history positions
/// are immune to system clock changes; labels may differ from a changed system clock until the
/// application restarts (documented in docs/MONITOR_API.md).
/// </summary>
public static class SteadyClock
{
    static readonly DateTimeOffset AnchorWall = DateTimeOffset.UtcNow;
    static readonly long AnchorTicks = System.Diagnostics.Stopwatch.GetTimestamp();
    public static DateTimeOffset Now => AnchorWall + System.Diagnostics.Stopwatch.GetElapsedTime(AnchorTicks);
}

/// <summary>
/// No server installation: one bounded exec per sample; no overlapping executions.
/// </summary>
/// <remarks>
/// History contract (shared with <c>MetricGraph</c>):
/// <list type="bullet">
/// <item>Every successful sample is a point with its real completion time.</item>
/// <item>A point continues the series when it belongs to the same collection generation and
/// follows the previous successful point within <see cref="ContinuityLimit"/>. A slow but
/// successful collection is NOT a discontinuity (beta.5 inserted a gap whenever collection took
/// longer than one interval, which turned a slow server into isolated dots).</item>
/// <item>Failures, pause, hidden sections and silence longer than the limit insert an explicit gap
/// point with a reason. Unknown metric values stay null in their own point.</item>
/// <item>History is bounded by time (<see cref="HistoryWindow"/>) and by a hard point cap.</item>
/// </list>
/// </remarks>
public sealed class LinuxMonitor
{
    private readonly Func<string, CancellationToken, Task<ExecResult>> _execute;
    private readonly int _maxProcesses;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly MonitorCalculator _calculator = new();
    private readonly Queue<HistoryPoint> _history = new();
    private readonly int _historyCapacity;
    private readonly object _historyLock = new();
    private readonly Func<DateTimeOffset> _clock;
    private int _generation, _resetBeforeSample, _lastSuccessGeneration = -1;
    private DateTimeOffset? _lastSuccess;
    private TimeSpan _lastDuration;
    private long _expectedIntervalTicks = TimeSpan.TicksPerSecond * 2;
    private int _scope = (int)MonitorScope.Processes;
    public TimeSpan ExpectedInterval
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _expectedIntervalTicks));
        set { if(value <= TimeSpan.Zero || value > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(value)); Interlocked.Exchange(ref _expectedIntervalTicks,value.Ticks); }
    }
    /// <summary>How long history is kept; at least the longest graph window.</summary>
    public TimeSpan HistoryWindow { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>What the next sample collects. The Performance page needs no process table.</summary>
    public MonitorScope Scope { get => (MonitorScope)Volatile.Read(ref _scope); set => Volatile.Write(ref _scope, (int)value); }
    /// <summary>Largest distance between two successful points that still forms one line.</summary>
    public TimeSpan ContinuityLimit
    {
        get
        {
            var interval = ExpectedInterval;
            var byInterval = interval * 3;
            var byDuration = interval + _lastDuration * 2;
            var floor = TimeSpan.FromSeconds(3);
            return new[] { byInterval, byDuration, floor }.Max();
        }
    }
    /// <summary>Last executed command (diagnostics/tests). Contains no user data.</summary>
    public string? LastCommand { get; private set; }
    public MonitorSnapshot? LastSnapshot { get; private set; }

    public LinuxMonitor(Func<string, CancellationToken, Task<ExecResult>> execute,
        int maxProcesses = 2048, int historyCapacity = 3600, Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        if (historyCapacity is < 2 or > 100_000) throw new ArgumentOutOfRangeException(nameof(historyCapacity));
        if (maxProcesses is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(maxProcesses));
        _execute = execute;
        _maxProcesses = maxProcesses;
        _historyCapacity = historyCapacity;
        _clock = clock ?? (() => SteadyClock.Now);
    }

    public IReadOnlyList<HistoryPoint> History
    {
        get { lock (_historyLock) return _history.ToArray(); }
    }

    CollectorRequest NextRequest() => Scope == MonitorScope.Processes
        ? new CollectorRequest(true, _calculator.UsersRefreshNeeded, _calculator.PendingCommandLines, _maxProcesses)
        : CollectorRequest.SystemOnly;

    public async Task<MonitorSnapshot> SampleAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        int generation = Volatile.Read(ref _generation);
        var started = _clock();
        try
        {
            if (Interlocked.Exchange(ref _resetBeforeSample,0) != 0) _calculator.Reset("После паузы или ошибки накапливается новая база измерений.");
            var command = CollectorCommand.Create(NextRequest()); LastCommand = command;
            var r = await _execute(command, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (r.ExitStatus != 0)
            {
                if (r.Output.StartsWith("SSHXERROR:missing:", StringComparison.Ordinal))
                    throw new MonitoringUnavailableException($"Мониторингу нужна уже установленная утилита {r.Output[18..].Trim()}. На сервер ничего не устанавливается.");
                if (r.Output.StartsWith("SSHXERROR:proc-unavailable", StringComparison.Ordinal))
                    throw new MonitoringUnavailableException("На сервере недоступен /proc либо это не Linux. Терминал и SFTP работают независимо.");
                throw new MonitoringUnavailableException($"Пакетный запрос мониторинга отклонён сервером (код {r.ExitStatus}). Проверьте разрешение SSH exec.");
            }
            MonitorSnapshot snapshot;
            var ended = _clock();
            try { snapshot = _calculator.Calculate(r.Output, ended) with { CollectionDuration = MaxZero(ended - started) }; }
            catch (FormatException) { throw new MonitoringUnavailableException("Ответ мониторинга повреждён или прерван."); }
            double? receive = SumMeasured(snapshot.Networks.Select(x => x.ReceiveBytesPerSecond));
            double? send = SumMeasured(snapshot.Networks.Select(x => x.SendBytesPerSecond));
            lock (_historyLock)
            {
                if (generation == _generation)
                {
                    var limit = ContinuityLimit;
                    bool continues = _lastSuccessGeneration == generation && _lastSuccess is { } previous
                        && snapshot.Timestamp > previous && snapshot.Timestamp - previous <= limit
                        && _history.LastOrDefault() is { IsGap: false };
                    if (!continues && _lastSuccessGeneration == generation && _lastSuccess is { } last && _history.LastOrDefault() is { IsGap: false })
                        Enqueue(new HistoryPoint(last + limit, null, null, null, null) { IsGap = true, Reason = $"Нет данных дольше {limit.TotalSeconds:0} с: замер не поступил вовремя." });
                    Enqueue(new HistoryPoint(snapshot.Timestamp, snapshot.System.CpuPercent,
                        snapshot.System.MemoryPercent, receive, send) { ContinuesSeries = continues, CollectionDuration = snapshot.CollectionDuration });
                    _lastSuccess = snapshot.Timestamp; _lastSuccessGeneration = generation; _lastDuration = snapshot.CollectionDuration;
                    Trim(snapshot.Timestamp);
                    LastSnapshot = snapshot;
                }
                else _calculator.Reset("Замер завершился после паузы; его база не используется.");
            }
            return snapshot;
        }
        catch { MarkGap(_clock(),"Замер не завершён; в истории оставлен разрыв."); throw; }
        finally { _gate.Release(); }
    }

    public void MarkGap(DateTimeOffset timestamp, string reason)
    {
        lock (_historyLock)
        {
            Interlocked.Increment(ref _generation); Interlocked.Exchange(ref _resetBeforeSample,1);
            if (_history.LastOrDefault() is { IsGap: true } last && last.Reason == reason) return;
            Enqueue(new HistoryPoint(timestamp,null,null,null,null) { IsGap=true,Reason=reason });
        }
    }
    private void Enqueue(HistoryPoint point)
    { _history.Enqueue(point); while(_history.Count > _historyCapacity) _history.Dequeue(); }
    private void Trim(DateTimeOffset now)
    {
        var oldest = now - HistoryWindow;
        while (_history.Count > 2 && _history.Peek().Timestamp < oldest) _history.Dequeue();
    }
    private static TimeSpan MaxZero(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;

    public async Task<MonitorCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var r = await _execute("command -v htop >/dev/null 2>&1 && printf 'htop\\n'; command -v top >/dev/null 2>&1 && printf 'top\\n'; true", cancellationToken).ConfigureAwait(false);
            var names = r.Output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return new MonitorCapabilities(names.Contains("htop", StringComparer.Ordinal), names.Contains("top", StringComparer.Ordinal));
        }
        finally { _gate.Release(); }
    }

    /// <summary>Caller MUST obtain explicit user confirmation. Remaining check-to-kill race is not atomic.</summary>
    public async Task SignalAsync(ProcessIdentity identity, int signal, CancellationToken cancellationToken = default)
    {
        if (ProcessSignalPolicy.DisabledReason(identity) is { } reason) throw new InvalidOperationException(reason);
        if (signal is not (9 or 15)) throw new ArgumentOutOfRangeException(nameof(signal));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Only validated numbers are substituted; no process names or user strings become shell code.
            var cmd = SignalCommand
                .Replace("__PID__", identity.Pid.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("__START__", identity.StartTimeTicks.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("__UID__", identity.UserId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("__SIGNAL__", signal.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
            var r = await _execute(cmd, cancellationToken).ConfigureAwait(false);
            if (r.ExitStatus == 74) throw new ProcessChangedException("Процесс завершился или его PID, время запуска либо владелец изменились. Сигнал не отправлен.");
            if (r.ExitStatus != 0) throw new InvalidOperationException("Сервер отказал в отправке сигнала. Недостаточно прав либо процесс уже завершён. Автоматический sudo не применяется.");
        }
        finally { _gate.Release(); }
    }

    public void Reset()
    {
        if (!_gate.Wait(0)) throw new InvalidOperationException("Дождитесь завершения текущего замера перед сбросом.");
        try
        {
            _calculator.Reset();
            Interlocked.Exchange(ref _resetBeforeSample,0);
            lock (_historyLock) { _history.Clear(); _lastSuccess = null; _lastSuccessGeneration = -1; }
        }
        finally { _gate.Release(); }
    }

    private static double? SumMeasured(IEnumerable<double?> values)
    {
        var a = values.ToArray();
        return a.Length == 0 || a.Any(x => x is null) ? null : a.Sum(x => x!.Value);
    }

    private const string SignalCommand = """
        LC_ALL=C; export LC_ALL
        p=__PID__
        s=$(cat "/proc/$p/stat" 2>/dev/null) || exit 74
        r=${s##*) }
        [ "$r" != "$s" ] || exit 74
        set -f
        set -- $r
        [ "$#" -ge 20 ] || exit 74
        shift 19
        [ "$1" = '__START__' ] || exit 74
        actual_uid=''
        while IFS=: read -r k v; do
          if [ "$k" = Uid ]; then set -- $v; actual_uid=$1; break; fi
        done < "/proc/$p/status"
        [ "$actual_uid" = '__UID__' ] || exit 74
        kill -__SIGNAL__ "$p"
        """;
}
