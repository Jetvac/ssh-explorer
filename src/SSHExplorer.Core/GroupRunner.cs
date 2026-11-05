namespace SSHExplorer.Core;

public enum GroupRunState { Waiting, Connecting, Running, Succeeded, Failed, Skipped, Cancelled }

/// <summary>What happened on one server of a group run.</summary>
public sealed record GroupRunResult(Guid ProfileId, GroupRunState State, int? ExitCode, string Output, TimeSpan Duration, string? Problem)
{
    public bool Retryable => State is GroupRunState.Failed or GroupRunState.Cancelled;
}

/// <summary>
/// Runs one script on several servers at once, each over its own new connection, at most <see cref="Parallelism"/> at a
/// time. Only saved credentials are used (saved password or key passphrase, SSH agent, an unencrypted key): a server
/// that would need a password typed in is skipped with the reason. A host key that is not trusted yet is never
/// accepted here — such a server is reported, and is connected once in a tab to check its fingerprint.
/// </summary>
public sealed class GroupRunner(AppStore store)
{
    public int Parallelism { get; set; } = 4;
    /// <summary>Test hook: replaces connecting and running for one server.</summary>
    public Func<ServerProfile, SavedScript, CancellationToken, Task<GroupRunResult>>? RunOverride { get; set; }

    public static string? Validate(SavedScript script) { var errors = ScriptRules.Validate(script); return errors.Count > 0 ? string.Join(" ", errors) : null; }

    public async Task RunAsync(IReadOnlyList<ServerProfile> targets, SavedScript script, Action<GroupRunResult> report, CancellationToken ct = default)
    {
        if (Validate(script) is { } error) throw new ArgumentException(error);
        using var gate = new SemaphoreSlim(Math.Clamp(Parallelism, 1, 16));
        foreach (var t in targets) report(new GroupRunResult(t.Id, GroupRunState.Waiting, null, "", TimeSpan.Zero, null));
        var tasks = targets.Select(async target =>
        {
            try { await gate.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { report(new GroupRunResult(target.Id, GroupRunState.Cancelled, null, "", TimeSpan.Zero, "Остановлено до запуска.")); return; }
            try { report(await RunOneAsync(target, script, report, ct).ConfigureAwait(false)); }
            finally { gate.Release(); }
        }).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    async Task<GroupRunResult> RunOneAsync(ServerProfile profile, SavedScript script, Action<GroupRunResult> report, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        GroupRunResult Done(GroupRunState state, string? problem, int? code = null, string output = "") => new(profile.Id, state, code, output, DateTime.UtcNow - started, problem);
        string? secret = null;
        if (profile.SaveSecret) { try { secret = await store.ReadSecretAsync(profile.Id, ct).ConfigureAwait(false); } catch (Exception ex) when (ex is IOException or System.Security.Cryptography.CryptographicException or UnauthorizedAccessException) { } }
        if (profile.Authentication is AuthenticationKind.Password or AuthenticationKind.KeyboardInteractive && secret is null)
            return Done(GroupRunState.Skipped, "Пароль не сохранён: подключитесь вкладкой или сохраните пароль в профиле.");
        if (RunOverride is not null)
        {
            report(Done(GroupRunState.Running, null));
            try { return await RunOverride(profile, script, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return Done(GroupRunState.Cancelled, "Остановлено."); }
        }
        report(Done(GroupRunState.Connecting, null));
        bool untrusted = false;
        await using var session = new ServerSession(profile, store);
        try
        {
            await session.ConnectAsync(new SessionCredentials(secret ?? ""), (_, _) => { untrusted = true; return Task.FromResult(TrustDecision.Reject); }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Done(GroupRunState.Cancelled, "Остановлено."); }
        catch (Exception ex)
        {
            return Done(GroupRunState.Failed, untrusted ? "Ключ этого сервера ещё не подтверждён: подключитесь к нему один раз во вкладке и сверьте отпечаток." : "Не удалось подключиться: " + ErrorMessages.Describe(ex));
        }
        report(Done(GroupRunState.Running, null));
        using var runner = new ScriptRunner(session, () => [script], () => false);
        using var stop = ct.Register(runner.Dispose);
        ScriptRun? run;
        try { run = await runner.RunAsync(script.Clone(), "На группе").ConfigureAwait(false); }
        catch (OperationCanceledException) { return Done(GroupRunState.Cancelled, "Остановлено: команда на сервере могла продолжить работу."); }
        if (ct.IsCancellationRequested) return Done(GroupRunState.Cancelled, "Остановлено: команда на сервере могла продолжить работу.", run?.ExitCode, run?.Output ?? "");
        if (run is null) return Done(GroupRunState.Failed, "Скрипт не запустился.");
        return run.Succeeded ? Done(GroupRunState.Succeeded, null, run.ExitCode, run.Output)
            : Done(GroupRunState.Failed, run.TimedOut ? "Превышен таймаут." : $"Код завершения {run.ExitCode}.", run.ExitCode, run.Output);
    }
}
