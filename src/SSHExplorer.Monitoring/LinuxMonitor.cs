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

    public LinuxMonitor(Func<string, CancellationToken, Task<ExecResult>> execute,
        int maxProcesses = 2048, int historyCapacity = 120)
    {
        ArgumentNullException.ThrowIfNull(execute);
        if (historyCapacity is < 2 or > 3600) throw new ArgumentOutOfRangeException(nameof(historyCapacity));
        _execute = execute;
        _command = CollectorCommand.Create(maxProcesses);
        _historyCapacity = historyCapacity;
    }

    public IReadOnlyList<HistoryPoint> History
    {
        get { lock (_historyLock) return _history.ToArray(); }
    }

    public async Task<MonitorSnapshot> SampleAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var r = await _execute(_command, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (r.ExitStatus != 0)
            {
                if (r.Output.StartsWith("SSHXERROR:missing:", StringComparison.Ordinal))
                    throw new MonitoringUnavailableException($"Мониторингу нужна уже установленная утилита {r.Output[18..].Trim()}. На сервер ничего не устанавливается.");
                if (r.Output.StartsWith("SSHXERROR:proc-unavailable", StringComparison.Ordinal))
                    throw new MonitoringUnavailableException("На сервере недоступен /proc/stat либо это не Linux. Терминал и SFTP работают независимо.");
                throw new MonitoringUnavailableException($"Пакетный запрос мониторинга отклонён сервером (код {r.ExitStatus}). Проверьте разрешение SSH exec.");
            }
            MonitorSnapshot snapshot;
            try { snapshot = _calculator.Calculate(r.Output, DateTimeOffset.UtcNow); }
            catch (FormatException) { throw new MonitoringUnavailableException("Ответ мониторинга повреждён или прерван."); }
            double? receive = SumMeasured(snapshot.Networks.Select(x => x.ReceiveBytesPerSecond));
            double? send = SumMeasured(snapshot.Networks.Select(x => x.SendBytesPerSecond));
            lock (_historyLock)
            {
                _history.Enqueue(new HistoryPoint(snapshot.Timestamp, snapshot.System.CpuPercent,
                    snapshot.System.MemoryPercent, receive, send));
                while (_history.Count > _historyCapacity) _history.Dequeue();
            }
            return snapshot;
        }
        finally { _gate.Release(); }
    }

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
        if (identity.Pid <= 1 || identity.UserId == uint.MaxValue || identity.StartTimeTicks == 0)
            throw new InvalidOperationException("Нельзя завершить PID 1 или процесс без проверяемой идентичности.");
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
