using System.Runtime.CompilerServices;
using System.Windows;
using SSHExplorer.Core;
using SSHExplorer.Monitoring;

namespace SSHExplorer.App;

/// <summary>
/// Metric history of the application: one store under the settings folder, one scheduler for open tabs and background
/// servers, history alerts, events and the optional Prometheus endpoint.
/// </summary>
internal static class MetricsHub
{
    static AppStore? owner;
    static MetricsStore? store;
    static MetricsScheduler? scheduler;
    static PrometheusEndpoint? endpoint;
    static System.Threading.Timer? compaction;
    static readonly ConditionalWeakTable<ServerSession, MetricsSampler> samplers = new();
    static readonly Dictionary<Guid, (DateTimeOffset At, IReadOnlyList<HealthIssue> Issues)> forecasts = [];
    static readonly Dictionary<Guid, IReadOnlyDictionary<Guid, string>> customErrors = [];
    static readonly object sync = new();

    /// <summary>A server got new points or its recording state changed (raised on the UI thread).</summary>
    public static event Action<Guid>? Updated;
    /// <summary>Test hook: the clock used for alert windows.</summary>
    internal static Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;
    internal static string? EndpointError { get; private set; }

    static void Ensure()
    {
        var current = App.Store;
        if (ReferenceEquals(current, owner) && store is not null) return;
        lock (sync)
        {
            if (ReferenceEquals(current, owner) && store is not null) return;
            if (scheduler is { } old) _ = old.DisposeAsync().AsTask();
            endpoint?.Dispose(); endpoint = null;
            owner = current;
            store = new MetricsStore(System.IO.Path.Combine(current.DirectoryPath, "metrics"));
            scheduler = new MetricsScheduler(current, SampleAsync);
            scheduler.Event += (server, kind, text) => Note(server, kind, text);
            scheduler.StatusChanged += server => Raise(server);
            forecasts.Clear(); customErrors.Clear();
        }
    }

    public static MetricsStore Store { get { Ensure(); return store!; } }
    public static MetricsScheduler Scheduler { get { Ensure(); return scheduler!; } }

    /// <summary>Starts background recording, compaction and (when enabled) the Prometheus endpoint. Desktop start only.</summary>
    public static void Start()
    {
        Ensure();
        scheduler!.Start();
        compaction ??= new System.Threading.Timer(_ => Compact(), null, TimeSpan.FromMinutes(1), TimeSpan.FromHours(1));
        ApplySettings();
    }

    public static void Compact()
    {
        try
        {
            var m = App.Store.Settings.Metrics;
            Store.Compact(DateTimeOffset.UtcNow, new MetricsRetention(m.RawDays, m.FiveMinuteDays, m.HourDays));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { _ = LocalDiagnostics.RecordAsync("metrics.compact.failed", ex); }
    }

    /// <summary>Applies the Prometheus switch and port after «Параметры».</summary>
    public static void ApplySettings()
    {
        Ensure();
        var m = App.Store.Settings.Metrics;
        lock (sync)
        {
            if (!m.PrometheusEnabled) { endpoint?.Dispose(); endpoint = null; EndpointError = null; return; }
            if (endpoint is { } running && running.Port == m.PrometheusPort) return;
            endpoint?.Dispose(); endpoint = null;
            try { endpoint = new PrometheusEndpoint(Math.Clamp(m.PrometheusPort, 1024, 65535), PrometheusText); EndpointError = null; }
            catch (System.Net.Sockets.SocketException ex) { EndpointError = $"Порт {m.PrometheusPort} занят или недоступен: {ex.Message}"; }
        }
    }

    internal static int? EndpointPort => endpoint?.Port;

    internal static string PrometheusText()
    {
        var profiles = App.Store.Profiles.ToArray();
        var now = DateTimeOffset.UtcNow;
        return MetricsExport.Prometheus(Store.Servers().Select(id => (Name: profiles.FirstOrDefault(p => p.Id == id)?.Name, Id: id)).Where(x => x.Name is not null)
            .Select(x => (x.Name!, Store.Latest(x.Id, now))), now, TimeSpan.FromMinutes(5));
    }

    public static void Attach(ServerSession session) { if (!session.Profile.IsRdp) Scheduler.Attach(session); }
    public static void Detach(ServerSession session) => scheduler?.Detach(session);

    /// <summary>Adds an event to a server's history (connections, changes made from the app, the user's own marks).</summary>
    public static void Note(Guid server, string kind, string text)
    {
        try { Store.AddEvent(server, new MetricEvent(DateTimeOffset.UtcNow, kind, text)); Raise(server); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { _ = LocalDiagnostics.RecordAsync("metrics.event.failed", ex); }
    }

    public static IReadOnlyDictionary<Guid, string> CustomErrors(Guid server) { lock (customErrors) return customErrors.TryGetValue(server, out var e) ? e : new Dictionary<Guid, string>(); }

    static void Raise(Guid server)
    {
        if (Application.Current?.Dispatcher is { } d && !d.HasShutdownStarted) d.BeginInvoke(() => Updated?.Invoke(server));
    }

    internal static MetricsSampler SamplerFor(ServerSession session) =>
        samplers.GetValue(session, s => new MetricsSampler(s.Profile.Id, Store, (command, timeout, ct) => ExecuteAsync(s, command, timeout, ct), s.IsWindows));

    static async Task<ExecResult> ExecuteAsync(ServerSession session, string command, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var r = await session.ExecuteLongAsync(command, timeout, ct).ConfigureAwait(false);
            return new ExecResult(r.Output, r.Error, r.ExitStatus);
        }
        catch (Renci.SshNet.Common.SshOperationTimeoutException ex) { throw new TimeoutException(ex.Message, ex); }
    }

    /// <summary>The approved custom metrics of a server (its own and its folders').</summary>
    internal static IReadOnlyList<CustomMetricSpec> Specs(Guid server)
    {
        var profile = App.Store.Profiles.FirstOrDefault(p => p.Id == server);
        if (profile is null) return [];
        return MetricsProfile.CustomMetrics(profile, App.Store.Settings.Folders).Where(m => m.Approved && m.Validate() is null)
            .Select(m => new CustomMetricSpec(m.Id, m.Name, m.Command, m.Unit, m.IntervalSeconds)).ToArray();
    }

    static async Task SampleAsync(MetricsScheduler.Target target, CancellationToken ct)
    {
        var sampler = SamplerFor(target.Session);
        sampler.RecordProcesses = App.Store.Settings.Metrics.RecordProcesses;
        var outcome = await sampler.SampleAsync(Specs(target.Profile.Id), ct).ConfigureAwait(false);
        lock (customErrors) customErrors[target.Profile.Id] = outcome.CustomErrors;
        var issues = Evaluate(target.Profile.Id, Clock());
        var name = App.Store.Profiles.FirstOrDefault(p => p.Id == target.Profile.Id)?.Name ?? target.Profile.Name;
        if (Application.Current?.Dispatcher is { } d && !d.HasShutdownStarted)
            await d.InvokeAsync(() =>
            {
                foreach (var fresh in AlertCenter.Report(target.Profile.Id, name, issues, "metrics")) Note(target.Profile.Id, "alert", fresh.Text);
                Updated?.Invoke(target.Profile.Id);
            });
    }

    /// <summary>The history alerts of a server now: its rules and the disk forecast.</summary>
    internal static IReadOnlyList<HealthIssue> Evaluate(Guid server, DateTimeOffset now)
    {
        var settings = App.Store.Settings.Metrics;
        var interval = TimeSpan.FromSeconds(Math.Clamp(settings.IntervalSeconds, 30, 3600));
        var series = Store.SeriesOf(server);
        var issues = new List<HealthIssue>();
        foreach (var rule in settings.Rules.Where(r => r.Enabled && (r.ServerId is null || r.ServerId == server)))
            foreach (var (name, unit) in series.Where(s => MetricAlerts.Matches(rule.Series, s.Key)))
            {
                var minutes = Math.Clamp(rule.Minutes, 1, 24 * 60);
                var points = Store.Read(server, [name], now - TimeSpan.FromMinutes(minutes) - interval * 3, now, MetricResolution.Raw)[name];
                if (!MetricAlerts.Sustained(points, now, rule.Above, rule.Threshold, minutes, interval)) continue;
                issues.Add(new HealthIssue($"metric:{rule.Id:N}:{name}",
                    $"{MetricSeries.Label(name)} {(rule.Above ? "выше" : "ниже")} {MetricSeries.Format(rule.Threshold, unit)} дольше {minutes} мин (сейчас {MetricSeries.Format(points[^1].Average, unit)})"));
            }
        if (settings.ForecastDays > 0)
        {
            IReadOnlyList<HealthIssue> forecast;
            lock (forecasts)
            {
                if (forecasts.TryGetValue(server, out var cached) && now - cached.At < TimeSpan.FromHours(1)) forecast = cached.Issues;
                else
                {
                    var disks = series.Keys.Where(MetricSeries.IsFileSystem).ToArray();
                    var data = Store.Read(server, disks, now - TimeSpan.FromDays(7), now, MetricResolution.Hour);
                    forecast = disks.Select(d => (Disk: d, Days: MetricAlerts.DaysUntilFull(data[d])))
                        .Where(x => x.Days is { } days && days <= settings.ForecastDays)
                        .Select(x => new HealthIssue("forecast:" + x.Disk, $"{MetricSeries.Label(x.Disk)} заполнится примерно через {Math.Max(0, Math.Round(x.Days!.Value)):0} дн. при нынешнем росте")).ToArray();
                    forecasts[server] = (now, forecast);
                }
            }
            issues.AddRange(forecast);
        }
        return issues;
    }

    /// <summary>Deletes the stored history of a deleted profile.</summary>
    public static void Delete(Guid server)
    {
        try { Store.DeleteServer(server); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { _ = LocalDiagnostics.RecordAsync("metrics.delete.failed", ex); }
    }

    public static async Task StopAsync()
    {
        compaction?.Dispose(); compaction = null;
        endpoint?.Dispose(); endpoint = null;
        if (scheduler is { } s) await s.DisposeAsync();
    }
}
