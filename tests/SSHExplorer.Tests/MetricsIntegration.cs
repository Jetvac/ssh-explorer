#if CORE_TESTS
using SSHExplorer.Core;
using SSHExplorer.Monitoring;

/// <summary>
/// 0.6 on a real Linux server (agent sign-in): background recording refuses an unconfirmed host key, records system
/// values, processes and custom metrics over its own connection, and uses an open tab's connection when there is one.
/// Arguments: host port user.
/// </summary>
internal static class MetricsIntegration
{
    public static async Task RunAsync(TestSuite t, string host, int port, string user)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sshx-metrics-it-" + Guid.NewGuid().ToString("N"));
        var store = new AppStore(dir);
        var metrics = new MetricsStore(Path.Combine(dir, "metrics"));
        var profile = new ServerProfile
        {
            Name = "история · стенд", Host = host, Port = port, Username = user, Authentication = AuthenticationKind.Agent, RecordHistory = true,
            CustomMetrics =
            [
                new CustomMetricDefinition { Name = "answer", Command = "echo $((6*7))", Approved = true },
                new CustomMetricDefinition { Name = "pair", Command = "printf 'a=1\\nb=2.5\\n'", Unit = "шт", Approved = true },
                new CustomMetricDefinition { Name = "broken", Command = "echo nope >&2; exit 3", Approved = true },
                new CustomMetricDefinition { Name = "unconfirmed", Command = "echo 1", Approved = false },
            ],
        };
        store.Profiles.Add(profile);
        var samplers = new Dictionary<ServerSession, MetricsSampler>();
        var targets = new List<MetricsScheduler.Target>();
        async Task Sample(MetricsScheduler.Target target, CancellationToken ct)
        {
            targets.Add(target);
            if (!samplers.TryGetValue(target.Session, out var sampler))
                samplers[target.Session] = sampler = new MetricsSampler(target.Profile.Id, metrics, async (command, timeout, c) => { var r = await target.Session.ExecuteLongAsync(command, timeout, c); return new ExecResult(r.Output, r.Error, r.ExitStatus); }, target.Session.IsWindows);
            var stored = store.Profiles.First(p => p.Id == target.Profile.Id);
            var specs = MetricsProfile.CustomMetrics(stored, store.Settings.Folders).Where(m => m.Approved).Select(m => new CustomMetricSpec(m.Id, m.Name, m.Command, m.Unit, m.IntervalSeconds)).ToArray();
            await sampler.SampleAsync(specs, ct);
        }
        await using var scheduler = new MetricsScheduler(store, Sample);

        await t.Run("metrics integration: background recording never trusts an unknown host key", async () =>
        {
            await scheduler.TickAsync(CancellationToken.None);
            TestSuite.True(scheduler.StatusOf(profile.Id) is { Problem: { } p, LastSample: null } && p.Contains("Ключ сервера не подтверждён", StringComparison.Ordinal) && targets.Count == 0 && store.Trust.Records.Count == 0, scheduler.StatusOf(profile.Id)?.Problem ?? "no problem");
        });

        await t.Run("metrics integration: with the key confirmed, a background connection records system values, processes and custom metrics", async () =>
        {
            await using (var first = new ServerSession(profile, store)) await first.ConnectAsync(new SessionCredentials(""), _ => Task.FromResult(TrustDecision.Permanent));
            scheduler.Wake(profile.Id);
            await scheduler.TickAsync(CancellationToken.None);
            var status = scheduler.StatusOf(profile.Id);
            TestSuite.True(status is { Problem: null, LastSample: not null, Background: true } && targets.Count == 1 && targets[0].Background, status?.Problem ?? "status " + status);
            var now = DateTimeOffset.UtcNow;
            var latest = metrics.Latest(profile.Id, now);
            TestSuite.True(latest.ContainsKey(MetricSeries.Cpu) && latest.ContainsKey(MetricSeries.Memory) && latest.ContainsKey(MetricSeries.Load1) && latest.ContainsKey(MetricSeries.FileSystem("/")) && latest[MetricSeries.Cpu].Value is >= 0 and <= 100,
                "series: " + string.Join(", ", latest.Keys));
            TestSuite.True(latest["custom:answer"].Value == 42 && latest["custom:pair.a"].Value == 1 && latest["custom:pair.b"].Value == 2.5 && !latest.Keys.Any(k => k.StartsWith("custom:broken", StringComparison.Ordinal) || k.StartsWith("custom:unconfirmed", StringComparison.Ordinal)),
                "custom: " + string.Join(", ", latest.Keys.Where(k => k.StartsWith("custom:", StringComparison.Ordinal))));
            var errors = samplers.Values.Single().CustomErrors;
            TestSuite.True(errors.Count == 1 && errors.Values.Single().StartsWith("Код завершения 3: nope", StringComparison.Ordinal), string.Join(" | ", errors.Values));
            TestSuite.True(metrics.ProcessesAt(profile.Id, now, TimeSpan.FromMinutes(2)) is { Processes.Count: > 0 } top && top.Processes.Any(p => p.Name.Contains("sshd", StringComparison.Ordinal) || p.Cpu is not null), "processes recorded");
        });

        await t.Run("metrics integration: an open tab's connection is used instead of a second one", async () =>
        {
            var tabProfile = new ServerProfile { Name = "вкладка", Host = host, Port = port, Username = user, Authentication = AuthenticationKind.Agent };
            store.Profiles.Add(tabProfile);
            await using var tab = new ServerSession(tabProfile, store);
            await tab.ConnectAsync(new SessionCredentials(""), _ => Task.FromResult(TrustDecision.Reject));
            scheduler.Attach(tab);
            int before = targets.Count;
            await scheduler.TickAsync(CancellationToken.None);
            var used = targets.Skip(before).Where(x => x.Profile.Id == tabProfile.Id).ToArray();
            TestSuite.True(used.Length == 1 && ReferenceEquals(used[0].Session, tab) && !used[0].Background && metrics.Latest(tabProfile.Id, DateTimeOffset.UtcNow).ContainsKey(MetricSeries.Cpu), $"used={used.Length}");
            scheduler.Detach(tab);
            int after = targets.Count; scheduler.Wake(tabProfile.Id);
            await scheduler.TickAsync(CancellationToken.None);
            TestSuite.True(targets.Skip(after).All(x => x.Profile.Id != tabProfile.Id) && scheduler.StatusOf(tabProfile.Id) is null, "a closed tab without background recording stops recording");
        });
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }
}
#endif
