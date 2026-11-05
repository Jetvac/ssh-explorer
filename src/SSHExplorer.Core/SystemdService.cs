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
        var list = Windows ? WindowsServices.ListCommand : Systemd.ListCommand;
        var result = await (ExecuteOverride?.Invoke(list, ct) ?? session.ExecuteLongAsync(list, TimeSpan.FromSeconds(45), ct)).ConfigureAwait(false);
        var snapshot = Windows ? WindowsServices.Parse(result.Output) : Systemd.Parse(result.Output);
        if (Windows) displayNames = snapshot.Units.ToDictionary(u => u.Name, u => u.Description, StringComparer.OrdinalIgnoreCase);
        IsRoot = snapshot.IsRoot; Sudo = snapshot.Sudo;
        return snapshot;
    }

    public async Task<IReadOnlyDictionary<string, string>> ShowAsync(string unit, CancellationToken ct = default) =>
        Systemd.ParseShow((await Exec(Windows ? WindowsServices.ShowCommand(unit) : Systemd.ShowCommand(unit), ct).ConfigureAwait(false)).Output);
    /// <summary>A Windows server: services through PowerShell, the journal from the System event log.</summary>
    public bool Windows => WindowsOverride ?? session.IsWindows;
    internal bool? WindowsOverride { get; set; }
    Dictionary<string, string> displayNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Runs the action; a non-zero exit is reported with systemctl's own first line (access denied, unit not found).</summary>
    public async Task ActAsync(string unit, ServiceAction action, CancellationToken ct = default)
    {
        var command = Windows ? WindowsServices.ActionCommand(unit, action) : Systemd.ActionCommand(unit, action, IsRoot, Sudo);
        var result = await (ExecuteOverride?.Invoke(command, ct) ?? session.ExecuteLongAsync(command, ActionTimeout, ct)).ConfigureAwait(false);
        if (result.ExitStatus == 0) return;
        var first = (result.Output + "\n" + result.Error).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        var denied = first is not null && (first.Contains("Access denied", StringComparison.OrdinalIgnoreCase) || first.Contains("Interactive authentication required", StringComparison.OrdinalIgnoreCase) || first.Contains("password is required", StringComparison.OrdinalIgnoreCase));
        if (Windows)
        {
            var reason = (result.Error + (char)10 + result.Output).Split((char)10, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault(l => !l.StartsWith("#<", StringComparison.Ordinal) && !l.StartsWith("<", StringComparison.Ordinal));
            throw new IOException(IsRoot ? $"Windows не выполнил «{Systemd.Label(action)}» для «{unit}»: {reason ?? "без сообщения"}" : $"Недостаточно прав: управлять службами Windows может только администратор. {reason}");
        }
        throw new IOException(denied
            ? $"Недостаточно прав, чтобы {Systemd.Label(action).ToLowerInvariant()} «{unit}». Нужен root или sudo без пароля для systemctl; пароль приложение не запрашивает. Ответ сервера: {first}"
            : $"systemctl завершился с кодом {result.ExitStatus}: {first ?? "без сообщения"}");
    }

    /// <summary>The journal of a service; follows until cancelled. The command ends on the server when the viewer closes.</summary>
    public Task<int?> JournalAsync(string unit, int lines, bool follow, Action<string> onLine, CancellationToken ct)
    {
        if (Windows)
        {
            var events = WindowsServices.EventsCommand(unit, displayNames.GetValueOrDefault(unit) ?? unit, lines, follow);
            return StreamOverride?.Invoke(events, onLine, ct) ?? session.StreamLinesAsync(events, onLine, ct);
        }
        var command = Systemd.JournalCommand(unit, lines, follow, IsRoot, Sudo);
        return StreamOverride?.Invoke(command, onLine, ct) ?? session.StreamLinesAsync(follow ? LogFollow.Wrap(command) : command, onLine, ct);
    }
}
