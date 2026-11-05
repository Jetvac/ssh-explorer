using System.Text;
using Renci.SshNet;

namespace SSHExplorer.Core;

/// <summary>sshd on one server: read the effective settings, and change them only with the safe apply.</summary>
public sealed class SshdService(ServerSession session)
{
    public const int RollbackSeconds = 90;
    public ServerSession Session => session;
    public string Prefix { get; private set; } = "";
    public string Rights { get; private set; } = "";
    public Func<string, CancellationToken, Task<CommandResult>>? ExecuteOverride { get; set; }
    public Func<CancellationToken, Task<bool>>? VerifyOverride { get; set; }
    public Func<string, string, CancellationToken, Task>? UploadOverride { get; set; }

    Task<CommandResult> Exec(string command, CancellationToken ct) => ExecuteOverride?.Invoke(command, ct) ?? session.ExecuteLongAsync(command, TimeSpan.FromSeconds(60), ct);

    /// <summary>Root, passwordless sudo, or none (then settings are read from the files and cannot be changed).</summary>
    public async Task ProbeAsync(CancellationToken ct = default)
    {
        Rights = (await Exec(Ufw.RightsCommand, ct).ConfigureAwait(false)).Output.Trim();
        Prefix = Rights == "sudo" ? "sudo -n " : "";
    }

    public bool CanChange => Rights is "root" or "sudo";

    public async Task<SshdSnapshot> ReadAsync(CancellationToken ct = default) => Sshd.Parse((await Exec(Sshd.ReadCommand(Prefix), ct).ConfigureAwait(false)).Output);

    /// <summary>Writes the changed settings with a scheduled restore, checks a new SSH connection, then confirms or restores.</summary>
    public async Task<ApplyResult> ApplyAsync(SshdSnapshot snapshot, IReadOnlyDictionary<string, string> changes, Action<string>? progress = null, CancellationToken ct = default)
    {
        if (!CanChange) throw new InvalidOperationException("Для изменения настроек SSH нужны права root или sudo без пароля.");
        var id = Guid.NewGuid().ToString("N")[..16];
        var script = Sshd.SafeApply(id, Sshd.Block(snapshot.Ours, changes), snapshot.IncludesDropIns, RollbackSeconds);
        progress?.Invoke("Сохранение настроек SSH и запуск таймера отката…");
        string path;
        if (UploadOverride is not null) { path = "/tmp/.sshx-sshd-" + id + ".sh"; await UploadOverride(path, script, ct).ConfigureAwait(false); }
        else
        {
            if (!session.SftpAvailable) throw new InvalidOperationException("Для изменения настроек нужен SFTP: сценарий с откатом передаётся файлом.");
            var directory = PosixPath.Combine(session.Files.HomeDirectory, ".cache/sshexplorer");
            await Exec("umask 077 && mkdir -p " + PosixPath.Quote(directory), ct).ConfigureAwait(false);
            path = PosixPath.Combine(directory, "sshd-" + id + ".sh");
            using var lease = await session.LeaseSftpAsync(ct).ConfigureAwait(false);
            using (lease.Client.Open(path, FileMode.CreateNew, FileAccess.Write)) { }
            lease.Client.ChangePermissions(path, 600);
            using var stream = lease.Client.Open(path, FileMode.Open, FileAccess.Write);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteAsync(script.AsMemory(), ct).ConfigureAwait(false);
        }
        CommandResult run;
        try { run = await Exec($"{Prefix}sh {PosixPath.Quote(path)} 2>&1", ct).ConfigureAwait(false); }
        finally { try { await Exec("rm -f " + PosixPath.Quote(path), CancellationToken.None).ConfigureAwait(false); } catch (Exception ex) when (ex is not OperationCanceledException) { } }
        var output = run.Output.Trim();
        if (!output.Contains("#applied", StringComparison.Ordinal))
            return new ApplyResult(ApplyOutcome.Failed,
                output.Contains("#invalid", StringComparison.Ordinal) ? "sshd отклонил новые настройки (проверка sshd -t); прежние восстановлены."
                : output.Contains("#reload-failed", StringComparison.Ordinal) ? "Не удалось перезагрузить sshd; прежние настройки восстановлены."
                : $"Изменение не применено (код {run.ExitStatus}).", Clean(output));
        progress?.Invoke("Настройки применены. Проверка нового SSH-подключения…");
        bool reachable;
        try { reachable = await (VerifyOverride?.Invoke(ct) ?? VerifyAsync(ct)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { reachable = false; }
        if (reachable)
        {
            try { await Exec($"{Prefix}sh -c {PosixPath.Quote(Sshd.Confirm(id))}", CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { return new ApplyResult(ApplyOutcome.Confirmed, $"Новое подключение работает, но отменить таймер не удалось ({ErrorMessages.Describe(ex)}): через {RollbackSeconds} с настройки вернутся к прежним.", Clean(output)); }
            return new ApplyResult(ApplyOutcome.Confirmed, "Применено и подтверждено: новое SSH-подключение проходит.", Clean(output));
        }
        progress?.Invoke("Новое подключение не прошло. Откат…");
        try
        {
            await Exec($"{Prefix}sh -c {PosixPath.Quote(Sshd.RollbackNow(id))}", CancellationToken.None).ConfigureAwait(false);
            return new ApplyResult(ApplyOutcome.RolledBack, "Новое SSH-подключение после изменения не прошло, поэтому прежние настройки восстановлены. Сервер доступен как раньше.", Clean(output));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return new ApplyResult(ApplyOutcome.RolledBack, $"Новое подключение не прошло, а связь потеряна. Прежние настройки восстановятся автоматически не позже чем через {RollbackSeconds} с.", Clean(output)); }
    }

    static string Clean(string output) => string.Join("\n", output.Split('\n').Where(l => !l.StartsWith('#')));

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
