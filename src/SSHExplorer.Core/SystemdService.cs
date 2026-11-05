namespace SSHExplorer.Core;

/// <summary>systemd services of one server over the session's SSH exec; actions and journals on their own connection.</summary>
public sealed class SystemdService(ServerSession session)
{
    static readonly TimeSpan ActionTimeout = TimeSpan.FromMinutes(3);
    public ServerSession Session => session;
    public bool IsRoot { get; private set; }
    public bool Sudo { get; private set; }
    /// <summary>Replaces SSH exec in UI fixtures (no server); production always uses the session.</summary>
    internal Func<string, CancellationToken, Task<CommandResult>>? ExecuteOverride { get; set; }
    internal Func<string, Action<string>, CancellationToken, Task<int?>>? StreamOverride { get; set; }
    Task<CommandResult> Exec(string command, CancellationToken ct) => ExecuteOverride?.Invoke(command, ct) ?? session.ExecuteAsync(command, ct);

    public async Task<ServicesSnapshot> ListAsync(CancellationToken ct = default)
    {
        // Without a live session the long-command path would try to open a new connection: say so at once instead.
        if (ExecuteOverride is null && session.State != SessionState.Connected) throw new IOException("Сервер не подключён.");
        var result = await (ExecuteOverride?.Invoke(Systemd.ListCommand, ct) ?? session.ExecuteLongAsync(Systemd.ListCommand, TimeSpan.FromSeconds(45), ct)).ConfigureAwait(false);
        var snapshot = Systemd.Parse(result.Output);
        IsRoot = snapshot.IsRoot; Sudo = snapshot.Sudo;
        return snapshot;
    }

    public async Task<IReadOnlyDictionary<string, string>> ShowAsync(string unit, CancellationToken ct = default) =>
        Systemd.ParseShow((await Exec(Systemd.ShowCommand(unit), ct).ConfigureAwait(false)).Output);

    /// <summary>Runs the action; a non-zero exit is reported with systemctl's own first line (access denied, unit not found).</summary>
    public async Task ActAsync(string unit, ServiceAction action, CancellationToken ct = default)
    {
        var command = Systemd.ActionCommand(unit, action, IsRoot, Sudo);
        var result = await (ExecuteOverride?.Invoke(command, ct) ?? session.ExecuteLongAsync(command, ActionTimeout, ct)).ConfigureAwait(false);
        if (result.ExitStatus == 0) return;
        var first = (result.Output + "\n" + result.Error).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        var denied = first is not null && (first.Contains("Access denied", StringComparison.OrdinalIgnoreCase) || first.Contains("Interactive authentication required", StringComparison.OrdinalIgnoreCase) || first.Contains("password is required", StringComparison.OrdinalIgnoreCase));
        throw new IOException(denied
            ? $"Недостаточно прав, чтобы {Systemd.Label(action).ToLowerInvariant()} «{unit}». Нужен root или sudo без пароля для systemctl; пароль приложение не запрашивает. Ответ сервера: {first}"
            : $"systemctl завершился с кодом {result.ExitStatus}: {first ?? "без сообщения"}");
    }

    /// <summary>The journal of a service; follows until cancelled. The command ends on the server when the viewer closes.</summary>
    public Task<int?> JournalAsync(string unit, int lines, bool follow, Action<string> onLine, CancellationToken ct)
    {
        var command = Systemd.JournalCommand(unit, lines, follow, IsRoot, Sudo);
        return StreamOverride?.Invoke(command, onLine, ct) ?? session.StreamLinesAsync(follow ? LogFollow.Wrap(command) : command, onLine, ct);
    }
}
