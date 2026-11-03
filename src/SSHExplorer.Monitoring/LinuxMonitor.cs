using System.Globalization;

namespace SSHExplorer.Monitoring;

/// <summary>No server installation: one bounded exec per sample; no overlapping executions.</summary>
public sealed class LinuxMonitor
{
    private readonly Func<string, CancellationToken, Task<ExecResult>> _execute;
    private readonly string _command;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly MonitorCalculator _calculator = new();
    private readonly Queue<HistoryPoint> _history = new();
    private readonly int _historyCapacity;
    private readonly object _historyLock = new();
    private readonly Func<DateTimeOffset> _clock;
    private int _generation, _resetBeforeSample;
    private long _expectedIntervalTicks = TimeSpan.TicksPerSecond * 2;
    public TimeSpan ExpectedInterval
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _expectedIntervalTicks));
        set { if(value <= TimeSpan.Zero || value > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(value)); Interlocked.Exchange(ref _expectedIntervalTicks,value.Ticks); }
    }

    public LinuxMonitor(Func<string, CancellationToken, Task<ExecResult>> execute,
        int maxProcesses = 2048, int historyCapacity = 120, Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        if (historyCapacity is < 2 or > 3600) throw new ArgumentOutOfRangeException(nameof(historyCapacity));
        _execute = execute;
        _command = CollectorCommand.Create(maxProcesses);
        _historyCapacity = historyCapacity;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<HistoryPoint> History
    {
        get { lock (_historyLock) return _history.ToArray(); }
    }

    public async Task<MonitorSnapshot> SampleAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        int generation = Volatile.Read(ref _generation);
        var started = _clock();
        try
        {
            if (Interlocked.Exchange(ref _resetBeforeSample,0) != 0) _calculator.Reset("После паузы или ошибки накапливается новая база измерений.");
            var r = await _execute(_command, cancellationToken).ConfigureAwait(false);
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
                    var previous = _history.LastOrDefault();
                    if (snapshot.CollectionDuration > ExpectedInterval || previous is { IsGap:false } && snapshot.Timestamp - previous.Timestamp > ExpectedInterval * 1.5)
                        Enqueue(new HistoryPoint(started,null,null,null,null) { IsGap=true,Reason="Пропущен ожидаемый интервал или SSH-запрос занял дольше одного интервала." });
                    Enqueue(new HistoryPoint(snapshot.Timestamp, snapshot.System.CpuPercent,
                        snapshot.System.MemoryPercent, receive, send));
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
            Enqueue(new HistoryPoint(timestamp,null,null,null,null) { IsGap=true,Reason=reason });
        }
    }
    private void Enqueue(HistoryPoint point)
    { _history.Enqueue(point); while(_history.Count > _historyCapacity) _history.Dequeue(); }
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
            lock (_historyLock) _history.Clear();
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
