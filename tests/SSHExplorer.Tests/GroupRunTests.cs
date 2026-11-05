#if CORE_TESTS
using SSHExplorer.Core;

/// <summary>Group runs: parallel limit, skipping servers without saved credentials, stop, invalid scripts.</summary>
internal static class GroupRunTests
{
    public static async Task RunAsync(TestSuite t)
    {
        var store = new AppStore(Path.Combine(Path.GetTempPath(), "sshx-group-" + Guid.NewGuid().ToString("N")));
        var servers = Enumerable.Range(1, 6).Select(i => new ServerProfile { Name = "s" + i, Host = "s" + i + ".invalid", Username = "u", Authentication = AuthenticationKind.Agent }).ToList();
        var noPassword = new ServerProfile { Name = "p", Host = "p.invalid", Username = "u", Authentication = AuthenticationKind.Password };
        var script = new SavedScript { Name = "x", Body = "uptime" };

        await t.Run("group run: at most N servers at once; every server reports; a server needing a typed password is skipped", async () =>
        {
            int now = 0, peak = 0; var results = new List<GroupRunResult>();
            var runner = new GroupRunner(store) { Parallelism = 2, RunOverride = async (p, _, ct) =>
            {
                var n = Interlocked.Increment(ref now); lock (results) peak = Math.Max(peak, n);
                await Task.Delay(60, ct); Interlocked.Decrement(ref now);
                return new GroupRunResult(p.Id, p.Name == "s3" ? GroupRunState.Failed : GroupRunState.Succeeded, p.Name == "s3" ? 2 : 0, "out " + p.Name, TimeSpan.FromMilliseconds(60), p.Name == "s3" ? "Код завершения 2." : null);
            } };
            await runner.RunAsync([.. servers, noPassword], script, r => { lock (results) results.Add(r); });
            var last = results.GroupBy(r => r.ProfileId).ToDictionary(g => g.Key, g => g.Last());
            TestSuite.True(peak == 2 && last.Count == 7 && last.Values.Count(r => r.State == GroupRunState.Succeeded) == 5 && last[servers[2].Id] is { State: GroupRunState.Failed, Retryable: true, ExitCode: 2 }
                && last[noPassword.Id] is { State: GroupRunState.Skipped, Retryable: false } && last[noPassword.Id].Problem!.Contains("Пароль не сохранён", StringComparison.Ordinal)
                && results.Where(r => r.ProfileId == servers[0].Id).Select(r => r.State).SequenceEqual([GroupRunState.Waiting, GroupRunState.Running, GroupRunState.Succeeded]), $"peak={peak} count={last.Count}");
        });

        await t.Run("group run: stopping cancels the waiting servers and the running ones", async () =>
        {
            using var cts = new CancellationTokenSource(); var results = new List<GroupRunResult>();
            var runner = new GroupRunner(store) { Parallelism = 1, RunOverride = async (p, _, ct) => { await Task.Delay(5000, ct); return new GroupRunResult(p.Id, GroupRunState.Succeeded, 0, "", TimeSpan.Zero, null); } };
            var run = runner.RunAsync(servers.Take(3).ToArray(), script, r => { lock (results) results.Add(r); }, cts.Token);
            await Task.Delay(100); cts.Cancel(); await run;
            var last = results.GroupBy(r => r.ProfileId).Select(g => g.Last()).ToArray();
            TestSuite.True(last.Length == 3 && last.All(r => r.State == GroupRunState.Cancelled && r.Retryable), string.Join(",", last.Select(r => r.State)));
        });

        await t.Run("group run: an invalid script is refused before anything connects", async () =>
        {
            bool called = false; var runner = new GroupRunner(store) { RunOverride = (p, _, _) => { called = true; return Task.FromResult(new GroupRunResult(p.Id, GroupRunState.Succeeded, 0, "", TimeSpan.Zero, null)); } };
            bool threw = false; try { await runner.RunAsync(servers, new SavedScript { Name = "", Body = "" }, _ => { }); } catch (ArgumentException) { threw = true; }
            TestSuite.True(threw && !called && GroupRunner.Validate(script) is null, "validation");
        });
    }
}
#endif
