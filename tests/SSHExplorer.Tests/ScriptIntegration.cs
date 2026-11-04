#if CORE_TESTS
using SSHExplorer.Core;

/// <summary>Real script runs over SSH (agent sign-in). Arguments: host port user.</summary>
internal static class ScriptIntegration
{
    public static async Task RunAsync(TestSuite t, string host, int port, string user)
    {
        var store = new AppStore(Path.Combine(Path.GetTempPath(), "sshx-scripts-" + Guid.NewGuid().ToString("N")));
        await using var session = new ServerSession(new ServerProfile { Name = "Скрипты · стенд", Host = host, Port = port, Username = user, Authentication = AuthenticationKind.Agent }, store);
        await session.ConnectAsync(new SessionCredentials(""), _ => Task.FromResult(TrustDecision.Session));
        var scripts = new List<SavedScript>();
        using var runner = new ScriptRunner(session, () => scripts, () => true);
        async Task<string> Count(string command) => (await session.ExecuteAsync(command)).Output.Trim();

        await t.Run("scripts integration: commands run in order and the log has their output; the temporary file is removed", async () =>
        {
            var run = await runner.RunAsync(new SavedScript { Name = "Проверка", Body = "echo one\necho 'два и три'\nprintf 'x=%s\\n' \"$((2+3))\"" });
            TestSuite.True(run is { ExitCode: 0, Succeeded: true } && run.Output == "one\nдва и три\nx=5\n", run?.Output ?? "null");
            TestSuite.Equal("0", await Count("ls -A ~/.cache/sshexplorer 2>/dev/null | grep -c '^script-' || true"));
        });
        await t.Run("scripts integration: «stop at the first error» stops there with that exit code; without it the rest runs", async () =>
        {
            var strict = await runner.RunAsync(new SavedScript { Name = "Строгий", Body = "echo before\nfalse\necho after", StopOnError = true });
            TestSuite.True(strict is { ExitCode: 1 } && strict.Output == "before\n", strict?.Output ?? "null");
            var lenient = await runner.RunAsync(new SavedScript { Name = "Мягкий", Body = "echo before\nfalse\necho after", StopOnError = false });
            TestSuite.True(lenient is { ExitCode: 0 } && lenient.Output == "before\nafter\n", lenient?.Output ?? "null");
        });
        await t.Run("scripts integration: an own #! interpreter is used and stderr appears in the log", async () =>
        {
            var run = await runner.RunAsync(new SavedScript { Name = "bash", Body = "#!/usr/bin/env bash\nset -u\n[[ 2 -gt 1 ]] && echo bash-ok\necho warn >&2\nexit 3" });
            TestSuite.True(run is { ExitCode: 3 } && run.Output.Contains("bash-ok", StringComparison.Ordinal) && run.Output.Contains("warn", StringComparison.Ordinal), run?.Output ?? "null");
        });
        await t.Run("scripts integration: the script text is not visible in the server's process list while it runs; no overlap", async () =>
        {
            var marker = "sshx-marker-" + Guid.NewGuid().ToString("N")[..8];
            var script = new SavedScript { Name = "Долгий", Body = $"# {marker}\nsleep 3\necho done" };
            var running = runner.RunAsync(script);
            await Task.Delay(1200);
            var second = await runner.RunAsync(script);
            // The marker is split in this probe's own text and joined by the shell, so only a real leak matches.
            var probe = "M=\"" + marker[..12] + "\"\"" + marker[12..] + "\"; export M; for f in /proc/[0-9]*/cmdline; do tr '\\0' '\\n' < \"$f\" 2>/dev/null | awk 'index($0, ENVIRON[\"M\"]) { hit = 1 } END { exit !hit }' && echo \"$f\"; done | wc -l";
            var leaks = await Count(probe);
            var first = await running;
            TestSuite.True(second is null && leaks == "0" && first is { ExitCode: 0, Output: "done\n" }, $"second={second?.ExitCode} leaks={leaks} first={first?.Output}");
        });
        await t.Run("scripts integration: a timeout ends the wait with an explicit result", async () =>
        {
            runner.TimeoutOverride = TimeSpan.FromSeconds(2);
            var run = await runner.RunAsync(new SavedScript { Name = "Таймаут", Body = "sleep 20" });
            runner.TimeoutOverride = null;
            TestSuite.True(run is { TimedOut: true, Succeeded: false } && run.Output.Contains("таймаут", StringComparison.OrdinalIgnoreCase), run?.Output ?? "null");
        });
        await t.Run("scripts integration: an OnConnect script runs from the schedule once", async () =>
        {
            var auto = new SavedScript { Name = "Авто", Body = "echo auto", Trigger = ScriptTrigger.OnConnect }; scripts.Add(auto);
            await runner.TickAsync(); for (int i = 0; i < 50 && runner.History.All(h => h.ScriptId != auto.Id); i++) await Task.Delay(100);
            await runner.TickAsync(); await Task.Delay(500);
            TestSuite.True(runner.History.Count(h => h.ScriptId == auto.Id) == 1 && runner.History.First(h => h.ScriptId == auto.Id).Output == "auto\n", string.Join(" | ", runner.History.Select(h => h.ScriptName)));
        });
    }
}
#endif
