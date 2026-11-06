namespace SSHExplorer.Core;

/// <summary>A custom metric: a command on the server that prints a number or lines <c>name=value</c>.</summary>
public sealed class CustomMetricDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Name in series and charts: latin letters, digits, _ and -.</summary>
    public string Name { get; set; } = "";
    /// <summary>sh on Linux, PowerShell on Windows. Runs with the SSH user's rights; the app never adds sudo.</summary>
    public string Command { get; set; } = "";
    public string Unit { get; set; } = "";
    public int IntervalSeconds { get; set; } = 60;
    /// <summary>The user has seen this exact command. Commands imported from a file stay off until confirmed.</summary>
    public bool Approved { get; set; }
    /// <summary>Template it was made from (informational).</summary>
    public string Template { get; set; } = "";
    public CustomMetricDefinition Clone() => (CustomMetricDefinition)MemberwiseClone();
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 40 || !Name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) return "Имя метрики — латиница, цифры, _ и - (до 40 знаков).";
        if (string.IsNullOrWhiteSpace(Command)) return "Укажите команду.";
        if (Command.Length > 2000) return "Команда длиннее 2000 знаков.";
        if (IntervalSeconds is < 60 or > 86400) return "Интервал — от 60 секунд до суток.";
        if (Unit.Length > 12) return "Единица — до 12 знаков.";
        return null;
    }
}

/// <summary>An alert on history: a series beyond a threshold for some minutes.</summary>
public sealed class MetricAlertRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Server the rule is for; null — every recorded server.</summary>
    public Guid? ServerId { get; set; }
    /// <summary>Series name or a prefix with «*» (fs:* — every disk).</summary>
    public string Series { get; set; } = "cpu";
    public bool Above { get; set; } = true;
    public double Threshold { get; set; } = 90;
    public int Minutes { get; set; } = 15;
    public bool Enabled { get; set; } = true;
    public MetricAlertRule Clone() => (MetricAlertRule)MemberwiseClone();
}

public sealed class MetricsSettings
{
    /// <summary>Record servers whose tab is open (one short request a minute).</summary>
    public bool RecordOpenTabs { get; set; } = true;
    public bool RecordProcesses { get; set; } = true;
    public int IntervalSeconds { get; set; } = 60;
    public int RawDays { get; set; } = 7;
    public int FiveMinuteDays { get; set; } = 30;
    public int HourDays { get; set; } = 365;
    /// <summary>Warn when a disk is expected to fill within this many days at its current trend; 0 — off.</summary>
    public int ForecastDays { get; set; } = 14;
    public List<MetricAlertRule> Rules { get; set; } = [];
    /// <summary>GET /metrics on 127.0.0.1 for Prometheus; off by default.</summary>
    public bool PrometheusEnabled { get; set; }
    public int PrometheusPort { get; set; } = 9469;
}

/// <summary>What a profile records, with folder inheritance.</summary>
public static class MetricsProfile
{
    public static bool RecordsInBackground(ServerProfile profile, IReadOnlyCollection<FolderDefaults> folders) =>
        !profile.IsRdp && (profile.RecordHistory || ProfileInheritance.Chain(profile.Group, folders).Any(f => f.RecordHistory));

    /// <summary>The server's own metrics, then those of its folders (nearest first); one per name.</summary>
    public static IReadOnlyList<CustomMetricDefinition> CustomMetrics(ServerProfile profile, IReadOnlyCollection<FolderDefaults> folders) =>
        profile.CustomMetrics.Concat(ProfileInheritance.Chain(profile.Group, folders).SelectMany(f => f.CustomMetrics))
            .GroupBy(m => m.Name, StringComparer.Ordinal).Select(g => g.First()).ToArray();
}

/// <summary>The recording state of one server, shown in «История» and on «Главная».</summary>
public sealed record MetricsStatus(DateTimeOffset? LastSample, string? Problem, bool Background);

/// <summary>
/// Decides when and over which connection each server is recorded. An open tab's session is used when there is one;
/// servers marked for background recording get their own connection without a terminal, with the saved password or
/// key and the trusted host key only — nothing is ever asked from the background. A server that cannot be reached is
/// retried later, less often.
/// </summary>
public sealed class MetricsScheduler : IAsyncDisposable
{
    public sealed record Target(ServerProfile Profile, ServerSession Session, bool Background);

    sealed class State
    {
        public DateTimeOffset NextDue;
        public ServerSession? Owned;
        public int Failures;
        public DateTimeOffset? LastSample;
        public string? Problem;
        public bool Busy;
        public bool Unreachable;
    }

    readonly AppStore store;
    readonly Func<Target, CancellationToken, Task> sample;
    readonly Func<DateTimeOffset> clock;
    readonly Dictionary<Guid, State> states = [];
    readonly Dictionary<Guid, ServerSession> attached = [];
    readonly SemaphoreSlim tick = new(1, 1);
    readonly CancellationTokenSource lifetime = new();
    Task? loop;

    /// <summary>Raised for outages and recoveries: server, kind («offline», «online»), text.</summary>
    public event Action<Guid, string, string>? Event;
    public event Action<Guid>? StatusChanged;
    /// <summary>Test hook: replaces the background connection.</summary>
    public Func<ServerProfile, CancellationToken, Task<ServerSession>>? ConnectOverride { get; set; }

    public MetricsScheduler(AppStore store, Func<Target, CancellationToken, Task> sample, Func<DateTimeOffset>? clock = null)
    {
        this.store = store; this.sample = sample; this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    MetricsSettings Settings => store.Settings.Metrics;
    TimeSpan Interval => TimeSpan.FromSeconds(Math.Clamp(Settings.IntervalSeconds, 30, 3600));

    public void Attach(ServerSession session) { lock (states) { attached[session.Profile.Id] = session; if (states.TryGetValue(session.Profile.Id, out var s)) s.NextDue = DateTimeOffset.MinValue; } }
    public void Detach(ServerSession session) { lock (states) { if (attached.TryGetValue(session.Profile.Id, out var s) && ReferenceEquals(s, session)) attached.Remove(session.Profile.Id); } }

    public MetricsStatus? StatusOf(Guid server)
    {
        lock (states)
        {
            bool background = store.Profiles.FirstOrDefault(p => p.Id == server) is { } p && MetricsProfile.RecordsInBackground(p, store.Settings.Folders);
            return states.TryGetValue(server, out var s) ? new MetricsStatus(s.LastSample, s.Problem, background) : background ? new MetricsStatus(null, null, true) : null;
        }
    }

    /// <summary>Records once more soon (after a profile changed, or «Обновить»).</summary>
    public void Wake(Guid server) { lock (states) if (states.TryGetValue(server, out var s)) { s.NextDue = DateTimeOffset.MinValue; s.Failures = 0; } }

    public void Start() => loop ??= Task.Run(async () =>
    {
        while (!lifetime.IsCancellationRequested)
        {
            try { await TickAsync(lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (Exception) { /* one failed pass must not stop recording */ }
            try { await Task.Delay(TimeSpan.FromSeconds(10), lifetime.Token).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
        }
    });

    /// <summary>One pass: every server that is due is recorded (up to 6 at once).</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        if (!await tick.WaitAsync(0, ct).ConfigureAwait(false)) return;
        try
        {
            var now = clock();
            var work = new List<(ServerProfile Profile, ServerSession? Session, bool Background, State State)>();
            lock (states)
            {
                var folders = store.Settings.Folders;
                var ids = new HashSet<Guid>();
                if (Settings.RecordOpenTabs) foreach (var (id, s) in attached) if (s.State == SessionState.Connected && !s.Profile.IsRdp) ids.Add(id);
                foreach (var p in store.Profiles) if (MetricsProfile.RecordsInBackground(p, folders)) ids.Add(p.Id);
                foreach (var gone in states.Keys.Where(k => !ids.Contains(k)).ToArray())
                {
                    if (states[gone].Owned is { } owned) _ = owned.DisposeAsync().AsTask();
                    states.Remove(gone);
                }
                foreach (var id in ids)
                {
                    if (!states.TryGetValue(id, out var state)) states[id] = state = new State();
                    if (state.Busy || now < state.NextDue) continue;
                    var profile = store.Profiles.FirstOrDefault(p => p.Id == id) ?? (attached.TryGetValue(id, out var a) ? a.Profile : null);
                    if (profile is null) continue;
                    var tab = attached.TryGetValue(id, out var t) && t.State == SessionState.Connected && Settings.RecordOpenTabs ? t : null;
                    bool background = MetricsProfile.RecordsInBackground(profile, folders);
                    if (tab is null && !background) continue;
                    state.Busy = true;
                    work.Add((profile, tab, background, state));
                }
            }
            using var gate = new SemaphoreSlim(6);
            await Task.WhenAll(work.Select(async w =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try { await RecordAsync(w.Profile, w.Session, w.State, ct).ConfigureAwait(false); }
                finally { gate.Release(); lock (states) w.State.Busy = false; StatusChanged?.Invoke(w.Profile.Id); }
            })).ConfigureAwait(false);
        }
        finally { tick.Release(); }
    }

    async Task RecordAsync(ServerProfile profile, ServerSession? tab, State state, CancellationToken ct)
    {
        var now = clock();
        ServerSession session;
        bool background = tab is null;
        if (tab is not null)
        {
            session = tab;
            if (state.Owned is { } extra) { state.Owned = null; await extra.DisposeAsync().ConfigureAwait(false); }
        }
        else
        {
            if (state.Owned is not { State: SessionState.Connected })
            {
                if (state.Owned is { } stale) { state.Owned = null; await stale.DisposeAsync().ConfigureAwait(false); }
                var (connected, problem, retryLater) = await ConnectAsync(profile, ct).ConfigureAwait(false);
                if (connected is null) { Fail(profile, state, problem ?? "Не удалось подключиться.", retryLater, now); return; }
                state.Owned = connected;
            }
            session = state.Owned!;
        }
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(120));
            await sample(new Target(profile, session, background), limit.Token).ConfigureAwait(false);
            if (state.Unreachable) Event?.Invoke(profile.Id, "online", "Сервер снова доступен");
            state.Unreachable = false; state.Failures = 0; state.Problem = null; state.LastSample = clock();
            state.NextDue = now + Interval;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            if (background && state.Owned is { } owned) { state.Owned = null; await owned.DisposeAsync().ConfigureAwait(false); }
            Fail(profile, state, "Замер не удался: " + (ex is OperationCanceledException ? "сервер не ответил за 2 минуты." : ErrorMessages.Describe(ex)), true, now);
        }
    }

    void Fail(ServerProfile profile, State state, string problem, bool unreachable, DateTimeOffset now)
    {
        state.Problem = problem;
        state.Failures++;
        // 1, 2, 5, 10, 30 minutes; a problem only the user can fix (no password, untrusted key) waits 30 minutes.
        int[] steps = [1, 2, 5, 10, 30];
        state.NextDue = now + TimeSpan.FromMinutes(unreachable ? steps[Math.Min(state.Failures - 1, steps.Length - 1)] : 30);
        if (unreachable && !state.Unreachable && state.LastSample is not null) Event?.Invoke(profile.Id, "offline", "Сервер недоступен: " + problem);
        if (unreachable) state.Unreachable = true;
    }

    async Task<(ServerSession? Session, string? Problem, bool RetryLater)> ConnectAsync(ServerProfile stored, CancellationToken ct)
    {
        var resolved = ProfileInheritance.Resolve(stored, store.Settings.Folders);
        if (resolved.Missing.Count > 0) return (null, "В папке не задано: " + string.Join(", ", resolved.Missing) + ".", false);
        var profile = resolved.Profile;
        string? secret = null;
        try
        {
            if (resolved.PasswordFolder is { } folder) secret = await store.ReadSecretAsync(folder, ct).ConfigureAwait(false);
            else if (profile.SaveSecret) secret = await store.ReadSecretAsync(profile.Id, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or System.Security.Cryptography.CryptographicException or UnauthorizedAccessException or FormatException or PlatformNotSupportedException) { }
        if (profile.Authentication is AuthenticationKind.Password or AuthenticationKind.KeyboardInteractive && secret is null)
            return (null, "Пароль не сохранён: для записи в фоне сохраните пароль в профиле.", false);
        if (ConnectOverride is { } fake)
        {
            try { return (await fake(profile, ct).ConfigureAwait(false), null, true); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { return (null, ErrorMessages.Describe(ex), true); }
        }
        bool untrusted = false;
        var session = new ServerSession(profile, store);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(profile.TimeoutSeconds, 3, 120) + 30));
            await session.ConnectAsync(new SessionCredentials(secret ?? ""), (_, _) => { untrusted = true; return Task.FromResult(TrustDecision.Reject); }, limit.Token).ConfigureAwait(false);
            return (session, null, true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { await session.DisposeAsync().ConfigureAwait(false); throw; }
        catch (Exception ex)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            return untrusted ? (null, "Ключ сервера не подтверждён: подключитесь один раз во вкладке и сверьте отпечаток.", false)
                : (null, "Не удалось подключиться: " + ErrorMessages.Describe(ex), true);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        if (loop is not null) try { await loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        ServerSession[] owned;
        lock (states) { owned = states.Values.Select(s => s.Owned).OfType<ServerSession>().ToArray(); states.Clear(); }
        foreach (var s in owned) await s.DisposeAsync().ConfigureAwait(false);
    }
}
