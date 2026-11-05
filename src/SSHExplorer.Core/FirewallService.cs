using System.Globalization;
using System.Text;
using Renci.SshNet;

namespace SSHExplorer.Core;

/// <summary>How a safe apply ended.</summary>
public enum ApplyOutcome { Confirmed, RolledBack, Failed }

public sealed record ApplyResult(ApplyOutcome Outcome, string Message, string Output);

/// <summary>
/// The guard against locking yourself out: every change runs as one root script that first saves ufw's state and
/// schedules its restore in <c>N</c> seconds (systemd-run timer, or a detached sleep). Then the app opens a brand-new SSH
/// connection along the same route. Only if that works is the restore cancelled. If the check fails the restore runs
/// at once over the still-open session (ufw keeps established connections); if the app loses the server entirely,
/// the timer restores the old rules by itself.
/// </summary>
public static class FirewallScripts
{
    public static string Directory(string id) => "/var/tmp/sshx-ufw-" + id;

    public static string SafeApply(string id, IReadOnlyList<string> commands, int seconds)
    {
        if (!IdOk(id)) throw new ArgumentException("Недопустимый идентификатор.", nameof(id));
        if (commands.Count == 0) throw new ArgumentException("Нет команд.", nameof(commands));
        foreach (var c in commands) if (!c.StartsWith("ufw ", StringComparison.Ordinal) || c.Contains('\n') || c.Contains(';') || c.Contains('&') || c.Contains('|') || c.Contains('`') || c.Contains('$'))
            throw new ArgumentException("Недопустимая команда ufw: " + c, nameof(commands));
        var d = Directory(id); var unit = "sshx-ufw-rollback-" + id;
        var sb = new StringBuilder();
        sb.Append("PATH=$PATH:/usr/sbin:/sbin; export PATH; LC_ALL=C; export LC_ALL; umask 077\n");
        sb.Append($"B='{d}'\nmkdir -m 700 \"$B\" || exit 90\n");
        sb.Append("for f in /etc/ufw/user.rules /etc/ufw/user6.rules /etc/ufw/ufw.conf /etc/default/ufw; do [ -f \"$f\" ] && cp -p \"$f\" \"$B/$(basename \"$f\")\"; done\n");
        sb.Append("if ufw status | head -n 1 | grep -q 'Status: active'; then echo active > \"$B/was\"; else echo inactive > \"$B/was\"; fi\n");
        // The restore script: copy the saved files back, then return ufw to its previous on/off state and reload.
        sb.Append("cat > \"$B/rollback.sh\" <<'SSHX_EOF'\n");
        sb.Append("PATH=$PATH:/usr/sbin:/sbin; export PATH\n");
        sb.Append($"B='{d}'\n[ -d \"$B\" ] || exit 0\n");
        sb.Append("cp -p \"$B/user.rules\" /etc/ufw/user.rules 2>/dev/null; cp -p \"$B/user6.rules\" /etc/ufw/user6.rules 2>/dev/null; cp -p \"$B/ufw.conf\" /etc/ufw/ufw.conf 2>/dev/null; cp -p \"$B/ufw\" /etc/default/ufw 2>/dev/null\n");
        sb.Append("if [ \"$(cat \"$B/was\")\" = active ]; then ufw --force enable >/dev/null 2>&1; ufw reload >/dev/null 2>&1; else ufw --force disable >/dev/null 2>&1; fi\n");
        sb.Append("rm -rf \"$B\"\nSSHX_EOF\n");
        sb.Append($"if command -v systemd-run >/dev/null 2>&1 && [ -d /run/systemd/system ] && systemd-run --quiet --unit='{unit}' --on-active={seconds.ToString(CultureInfo.InvariantCulture)} /bin/sh \"$B/rollback.sh\" >/dev/null 2>&1; then echo '#timer systemd'; \n");
        sb.Append($"else (setsid nohup sh -c 'sleep {seconds.ToString(CultureInfo.InvariantCulture)}; sh \"$0\"' \"$B/rollback.sh\" >/dev/null 2>&1 &) ; echo '#timer sleep'; fi\n");
        // A failing command restores at once: a half-applied set of rules is never left behind.
        foreach (var c in commands) sb.Append(c).Append(" 2>&1 || { echo '#failed'; sh \"$B/rollback.sh\"; exit 91; }\n");
        sb.Append("echo '#applied'\n");
        return sb.ToString();
    }

    /// <summary>Cancels the pending restore (the change is confirmed).</summary>
    public static string Confirm(string id) => $"systemctl stop 'sshx-ufw-rollback-{Check(id)}.timer' >/dev/null 2>&1; rm -rf '{Directory(id)}'; echo '#confirmed'";

    /// <summary>Restores now (the check failed but the session is still alive).</summary>
    public static string RollbackNow(string id) => $"PATH=$PATH:/usr/sbin:/sbin; systemctl stop 'sshx-ufw-rollback-{Check(id)}.timer' >/dev/null 2>&1; [ -f '{Directory(id)}/rollback.sh' ] && sh '{Directory(id)}/rollback.sh'; echo '#rolledback'";

    static bool IdOk(string id) => id.Length is > 0 and <= 32 && id.All(char.IsAsciiLetterOrDigit);
    static string Check(string id) => IdOk(id) ? id : throw new ArgumentException("Недопустимый идентификатор.", nameof(id));
}

/// <summary>ufw on one server: read, then change only through <see cref="ApplyAsync"/> (safe apply with re-check).</summary>
public sealed class FirewallService(ServerSession session)
{
    public const int RollbackSeconds = 90;
    public ServerSession Session => session;
    public string Prefix { get; private set; } = "";
    public string Rights { get; private set; } = "";
    internal Func<string, CancellationToken, Task<CommandResult>>? ExecuteOverride { get; set; }
    /// <summary>Replaces the fresh-connection check in fixtures.</summary>
    internal Func<CancellationToken, Task<bool>>? VerifyOverride { get; set; }
    /// <summary>Replaces SFTP upload of the apply script in fixtures (path, text).</summary>
    internal Func<string, string, CancellationToken, Task>? UploadOverride { get; set; }

    Task<CommandResult> Exec(string command, CancellationToken ct) => ExecuteOverride?.Invoke(command, ct) ?? session.ExecuteLongAsync(command, TimeSpan.FromSeconds(60), ct);

    /// <summary>Null when ufw can be managed; otherwise why not (no root and no passwordless sudo).</summary>
    public async Task<string?> ProbeAsync(CancellationToken ct = default)
    {
        if (ExecuteOverride is null && session.State != SessionState.Connected) return "Сервер не подключён.";
        Rights = (await Exec(Ufw.RightsCommand, ct).ConfigureAwait(false)).Output.Trim();
        Prefix = Rights == "sudo" ? "sudo -n " : "";
        return Rights is "root" or "sudo" ? null
            : "Для управления ufw нужны права root: подключитесь как root или разрешите этой учётной записи sudo без пароля. Пароль sudo приложение не запрашивает и не отправляет.";
    }

    public async Task<FirewallSnapshot> ReadAsync(CancellationToken ct = default) => Ufw.Parse((await Exec(Ufw.ReadCommand(Prefix), ct).ConfigureAwait(false)).Output);

    /// <summary>
    /// Applies <paramref name="commands"/> with a scheduled restore, checks a brand-new SSH connection along the same
    /// route, then confirms or restores. <paramref name="progress"/> gets each stage for the UI.
    /// </summary>
    public async Task<ApplyResult> ApplyAsync(IReadOnlyList<string> commands, Action<string>? progress = null, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("N")[..16];
        var script = FirewallScripts.SafeApply(id, commands, RollbackSeconds);
        progress?.Invoke("Сохранение текущих правил и запуск таймера отката…");
        string path;
        if (UploadOverride is not null) { path = "/tmp/.sshx-ufw-" + id + ".sh"; await UploadOverride(path, script, ct).ConfigureAwait(false); }
        else
        {
            if (!session.SftpAvailable) throw new InvalidOperationException("Для изменения правил нужен SFTP: сценарий с откатом передаётся файлом.");
            var directory = PosixPath.Combine(session.Files.HomeDirectory, ".cache/sshexplorer");
            await Exec("umask 077 && mkdir -p " + PosixPath.Quote(directory), ct).ConfigureAwait(false);
            path = PosixPath.Combine(directory, "ufw-" + id + ".sh");
            using var lease = await session.LeaseSftpAsync(ct).ConfigureAwait(false);
            using (lease.Client.Open(path, FileMode.CreateNew, FileAccess.Write)) { }
            lease.Client.ChangePermissions(path, 600); // SSH.NET reads the digits as octal
            using var stream = lease.Client.Open(path, FileMode.Open, FileAccess.Write);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteAsync(script.AsMemory(), ct).ConfigureAwait(false);
        }
        CommandResult run;
        try { run = await Exec($"{Prefix}sh {PosixPath.Quote(path)} 2>&1", ct).ConfigureAwait(false); }
        finally { try { await Exec("rm -f " + PosixPath.Quote(path), CancellationToken.None).ConfigureAwait(false); } catch (Exception ex) when (ex is not OperationCanceledException) { } }
        var output = run.Output.Trim();
        if (!output.Contains("#applied", StringComparison.Ordinal))
            return new ApplyResult(ApplyOutcome.Failed, output.Contains("#failed", StringComparison.Ordinal) ? "ufw отклонил команду; прежние правила восстановлены." : $"Изменение не применено (код {run.ExitStatus}).", Clean(output));
        progress?.Invoke("Изменение применено. Проверка нового SSH-подключения…");
        bool reachable;
        try { reachable = await (VerifyOverride?.Invoke(ct) ?? VerifyAsync(ct)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { reachable = false; }
        if (reachable)
        {
            try { await Exec($"{Prefix}sh -c {PosixPath.Quote(FirewallScripts.Confirm(id))}", CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { return new ApplyResult(ApplyOutcome.Confirmed, $"Новое подключение работает, но отменить таймер не удалось ({ErrorMessages.Describe(ex)}): через {RollbackSeconds} с правила вернутся к прежним.", Clean(output)); }
            return new ApplyResult(ApplyOutcome.Confirmed, "Применено и подтверждено: новое SSH-подключение проходит.", Clean(output));
        }
        progress?.Invoke("Новое подключение не прошло. Откат…");
        try
        {
            await Exec($"{Prefix}sh -c {PosixPath.Quote(FirewallScripts.RollbackNow(id))}", CancellationToken.None).ConfigureAwait(false);
            return new ApplyResult(ApplyOutcome.RolledBack, "Новое SSH-подключение после изменения не прошло, поэтому прежние правила восстановлены. Сервер доступен как раньше.", Clean(output));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return new ApplyResult(ApplyOutcome.RolledBack, $"Новое подключение не прошло, а связь с сервером потеряна. Прежние правила восстановятся автоматически не позже чем через {RollbackSeconds} с.", Clean(output)); }
    }

    static string Clean(string output) => string.Join("\n", output.Split('\n').Where(l => !l.StartsWith('#')));

    /// <summary>A brand-new connection along the profile's route (jump hosts included), exactly like the next login would be.</summary>
    async Task<bool> VerifyAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(25));
        SshClient? client = null;
        try
        {
            client = await session.CreateSshClientAsync(timeout.Token).ConfigureAwait(false);
            using var cmd = client.CreateCommand("true"); cmd.CommandTimeout = TimeSpan.FromSeconds(10);
            await cmd.ExecuteAsync(timeout.Token).ConfigureAwait(false);
            return cmd.ExitStatus == 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { return false; }
        finally { if (client is not null) session.ReleaseClient(client); }
    }
}
