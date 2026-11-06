namespace SSHExplorer.Monitoring;

/// <summary>A custom metric to run: its name in series, the command, unit and how often it runs.</summary>
public sealed record CustomMetricSpec(Guid Id, string Name, string Command, string Unit, int IntervalSeconds);

/// <summary>What one recording pass stored.</summary>
public sealed record SampleOutcome(DateTimeOffset Time, IReadOnlyList<MetricValue> Values, IReadOnlyList<MetricEvent> Events, IReadOnlyDictionary<Guid, string> CustomErrors);

/// <summary>
/// Records one server: a monitor sample (the same collector as «Сервер», nothing installed on the server), the busiest
/// processes, reboots and custom metrics, written to a <see cref="MetricsStore"/>. One instance per connection.
/// </summary>
public sealed class MetricsSampler
{
    readonly Guid server;
    readonly MetricsStore store;
    readonly Func<string, TimeSpan, CancellationToken, Task<ExecResult>> execute;
    readonly LinuxMonitor monitor;
    readonly Func<DateTimeOffset> clock;
    readonly Dictionary<Guid, DateTimeOffset> customRuns = [];
    readonly Dictionary<Guid, string> customErrors = [];
    bool primed;
    TimeSpan? lastUptime;
    public bool Windows { get; }
    /// <summary>Also store the busiest processes of each sample.</summary>
    public bool RecordProcesses { get; set; } = true;
    /// <summary>Pause between the two first samples on Linux (CPU and rates need two readings).</summary>
    public TimeSpan PrimeDelay { get; set; } = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    public MetricsSampler(Guid server, MetricsStore store, Func<string, TimeSpan, CancellationToken, Task<ExecResult>> execute, bool windows, Func<DateTimeOffset>? clock = null)
    {
        this.server = server; this.store = store; this.execute = execute; Windows = windows;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        monitor = new LinuxMonitor((command, ct) => execute(command, CommandTimeout, ct), maxProcesses: 4096) { Windows = windows, ExpectedInterval = TimeSpan.FromMinutes(1) };
    }

    /// <summary>Errors of custom metrics at their last run (metric id → text).</summary>
    public IReadOnlyDictionary<Guid, string> CustomErrors { get { lock (customErrors) return new Dictionary<Guid, string>(customErrors); } }

    public async Task<SampleOutcome> SampleAsync(IReadOnlyList<CustomMetricSpec> custom, CancellationToken ct)
    {
        monitor.Scope = RecordProcesses ? MonitorScope.Processes : MonitorScope.System;
        if (!primed && !Windows)
        {
            await monitor.SampleAsync(ct).ConfigureAwait(false);
            await Task.Delay(PrimeDelay, ct).ConfigureAwait(false);
        }
        var snapshot = await monitor.SampleAsync(ct).ConfigureAwait(false);
        primed = true;
        var time = clock();
        var values = new List<MetricValue>(MetricSeries.FromSnapshot(snapshot));
        var events = new List<MetricEvent>();
        if (snapshot.System.Uptime is { } uptime)
        {
            if (lastUptime is { } previous && uptime + TimeSpan.FromMinutes(1) < previous)
                events.Add(new MetricEvent(time - uptime, "reboot", "Сервер перезагружен"));
            lastUptime = uptime;
        }
        store.Append(server, time, values);
        if (RecordProcesses) store.AppendProcesses(server, time, MetricSeries.TopProcesses(snapshot));
        foreach (var e in events) store.AddEvent(server, e);

        var customValues = new List<MetricValue>();
        foreach (var spec in custom)
        {
            if (customRuns.TryGetValue(spec.Id, out var last) && time - last < TimeSpan.FromSeconds(Math.Max(30, spec.IntervalSeconds) - 5)) continue;
            customRuns[spec.Id] = time;
            var result = await RunCustomAsync(spec, ct).ConfigureAwait(false);
            lock (customErrors) { if (result.Error is null) customErrors.Remove(spec.Id); else customErrors[spec.Id] = result.Error; }
            customValues.AddRange(result.Values);
        }
        if (customValues.Count > 0) store.Append(server, time, customValues);
        values.AddRange(customValues);
        return new SampleOutcome(time, values, events, CustomErrors);
    }

    /// <summary>The first line of an error; PowerShell's CLIXML error stream is reduced to its error texts.</summary>
    internal static string? FirstError(string text)
    {
        if (text.Contains("#< CLIXML", StringComparison.Ordinal))
        {
            var found = System.Text.RegularExpressions.Regex.Matches(text, "<S S=\"Error\">(.*?)</S>")
                .Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Replace("_x000D__x000A_", " ").Trim()).FirstOrDefault(x => x.Length > 0);
            if (found is not null) return found;
        }
        return text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith("#< CLIXML", StringComparison.Ordinal) && !l.StartsWith("<Objs", StringComparison.Ordinal));
    }

    /// <summary>Runs one custom metric now (also «Проверить» in its editor). The command runs with the SSH user's rights.</summary>
    public async Task<CustomMetricResult> RunCustomAsync(CustomMetricSpec spec, CancellationToken ct)
    {
        var command = Windows ? WindowsCollector.PowerShell(spec.Command) : spec.Command;
        try
        {
            var r = await execute(command, CommandTimeout, ct).ConfigureAwait(false);
            if (r.ExitStatus != 0)
            {
                var error = FirstError(r.Error.Length > 0 ? r.Error : r.Output);
                return new([], $"Код завершения {r.ExitStatus}" + (error is null ? "." : ": " + (error.Length > 160 ? error[..160] + "…" : error)));
            }
            return CustomMetricParser.Parse(r.Output, spec.Name, spec.Unit);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new([], "Команда не ответила за 30 с."); }
        catch (TimeoutException) { return new([], "Команда не ответила за 30 с."); }
    }
}
