using Renci.SshNet.Common;

namespace SSHExplorer.Core;

/// <summary>
/// Runs saved scripts on one server and fires automatic ones while the session is connected. The script text is uploaded
/// as a 0700 file over SFTP (not passed on the command line, so it is not visible in the process list), run on its own
/// SSH connection, and deleted afterwards. A script never runs twice at the same time.
/// </summary>
public sealed class ScriptRunner : IDisposable
{
    readonly ServerSession session;
    readonly Func<IReadOnlyList<SavedScript>> scripts;
    readonly Func<bool> enabled;
    readonly HashSet<Guid> running = [], ranThisSession = [];
    readonly List<ScriptRun> history = [];
    readonly CancellationTokenSource lifetime;
    readonly DateTime connectedUtc;
    Timer? timer;

    /// <summary>Raised (on a worker thread) when a run starts and when it finishes.</summary>
    public event EventHandler? Changed;
    /// <summary>Raised after every finished run with its result; the host saves LastRun and may show a notice.</summary>
    public event EventHandler<ScriptRun>? Finished;
    /// <summary>Replaces the real execution in UI fixtures.</summary>
    internal Func<SavedScript, CancellationToken, Task<(int ExitCode, string Output, bool TimedOut)>>? ExecuteOverride { get; set; }
    public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;
    /// <summary>Shorter timeout for integration tests (the user setting is in whole minutes).</summary>
    internal TimeSpan? TimeoutOverride { get; set; }

    public ScriptRunner(ServerSession session, Func<IReadOnlyList<SavedScript>> scripts, Func<bool> enabled)
    {
        this.session = session; this.scripts = scripts; this.enabled = enabled;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(session.Lifetime);
        connectedUtc = DateTime.UtcNow;
    }

    public IReadOnlyList<ScriptRun> History { get { lock (history) return history.ToArray(); } }
    public bool IsRunning(Guid id) { lock (running) return running.Contains(id); }

    /// <summary>Starts the automatic triggers (checked every 20 s; OnConnect scripts are due at once).</summary>
    public void StartSchedule(TimeSpan? period = null)
    {
        var every = period ?? TimeSpan.FromSeconds(20);
        timer = new Timer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(1), every);
    }

    public async Task TickAsync()
    {
        if (lifetime.IsCancellationRequested || !enabled() || session.State != SessionState.Connected) return;
        foreach (var script in scripts().Where(s => s.Trigger != ScriptTrigger.Manual).ToArray())
        {
            bool ran; lock (running) ran = ranThisSession.Contains(script.Id);
            if (IsRunning(script.Id) || !ScriptRules.IsDue(script, UtcNow(), connectedUtc, script.LastRunUtc, ran)) continue;
            _ = RunAsync(script, script.Schedule);
        }
        await Task.CompletedTask;
    }

    /// <summary>Runs a script now. Null when it is already running.</summary>
    public async Task<ScriptRun?> RunAsync(SavedScript script, string reason = "Вручную")
    {
        lock (running) { if (!running.Add(script.Id)) return null; ranThisSession.Add(script.Id); }
        var started = UtcNow(); script.LastRunUtc = started;
        Changed?.Invoke(this, EventArgs.Empty);
        ScriptRun result;
        try
        {
            var (code, output, timedOut) = ExecuteOverride is { } fake ? await fake(script, lifetime.Token).ConfigureAwait(false) : await ExecuteAsync(script, lifetime.Token).ConfigureAwait(false);
            result = new ScriptRun(script.Id, script.Name, reason, started, UtcNow() - started, code, output, timedOut);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !lifetime.IsCancellationRequested)
        {
            result = new ScriptRun(script.Id, script.Name, reason, started, UtcNow() - started, -1, "Не удалось выполнить: " + ErrorMessages.Describe(ex), false);
        }
        finally { lock (running) running.Remove(script.Id); }
        script.LastExitCode = result.TimedOut ? -1 : result.ExitCode;
        lock (history) { history.Insert(0, result); if (history.Count > 50) history.RemoveAt(history.Count - 1); }
        Changed?.Invoke(this, EventArgs.Empty); Finished?.Invoke(this, result);
        return result;
    }

    async Task<(int, string, bool)> ExecuteAsync(SavedScript script, CancellationToken ct)
    {
        var errors = ScriptRules.Validate(script);
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));
        var timeout = TimeoutOverride ?? TimeSpan.FromMinutes(script.TimeoutMinutes);
        if (!session.SftpAvailable) throw new InvalidOperationException("Для запуска скриптов нужен SFTP: текст скрипта передаётся файлом, а не командной строкой, чтобы его не было видно в списке процессов сервера.");
        var directory = PosixPath.Combine(session.Files.HomeDirectory, ".cache/sshexplorer");
        await session.ExecuteAsync("umask 077 && mkdir -p " + PosixPath.Quote(directory), ct).ConfigureAwait(false);
        var path = PosixPath.Combine(directory, "script-" + Guid.NewGuid().ToString("N") + ".sh");
        using (var lease = await session.LeaseSftpAsync(ct).ConfigureAwait(false))
        {
            using (lease.Client.Open(path, FileMode.CreateNew, FileAccess.Write)) { }
            lease.Client.ChangePermissions(path, 700); // SSH.NET reads the digits as octal
            using var stream = lease.Client.Open(path, FileMode.Open, FileAccess.Write);
            using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
            await writer.WriteAsync(ScriptRules.FileContent(script).AsMemory(), ct).ConfigureAwait(false);
        }
        // stderr is merged so the log shows messages in order; the file is removed whatever the outcome.
        // Without its own #! the file is run by sh, so a noexec home still works.
        var launcher = script.Body.TrimStart().StartsWith("#!", StringComparison.Ordinal) ? PosixPath.Quote(path) : "sh " + PosixPath.Quote(path);
        var command = $"{launcher} 2>&1 </dev/null; code=$?; rm -f {PosixPath.Quote(path)}; exit $code";
        try
        {
            var result = await session.ExecuteLongAsync(command, timeout, ct).ConfigureAwait(false);
            var output = result.Output.Length > ScriptRules.MaxOutputBytes ? result.Output[^ScriptRules.MaxOutputBytes..] : result.Output;
            return (result.ExitStatus, output, false);
        }
        catch (SshOperationTimeoutException)
        {
            await session.ExecuteAsync("rm -f " + PosixPath.Quote(path), CancellationToken.None).ConfigureAwait(false);
            return (-1, $"Превышен таймаут {script.TimeoutMinutes} мин: соединение с командой закрыто, процесс на сервере мог продолжить работу.", true);
        }
    }

    public void Dispose() { timer?.Dispose(); lifetime.Cancel(); }
}
