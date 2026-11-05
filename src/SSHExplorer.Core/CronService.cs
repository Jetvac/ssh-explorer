using System.Text;

namespace SSHExplorer.Core;

public enum CronSaveOutcome { Saved, Changed }

/// <summary>A server's crontab: this user's, or root's through passwordless sudo.</summary>
public sealed class CronService(ServerSession session)
{
    public ServerSession Session => session;
    public bool AsRoot { get; set; }
    string Prefix => AsRoot ? "sudo -n " : "";
    public Func<string, CancellationToken, Task<CommandResult>>? ExecuteOverride { get; set; }
    public Func<string, string, CancellationToken, Task>? UploadOverride { get; set; }

    Task<CommandResult> Exec(string command, CancellationToken ct) => ExecuteOverride?.Invoke(command, ct) ?? session.ExecuteLongAsync(command, TimeSpan.FromSeconds(30), ct);

    public async Task<CronTab> ReadAsync(CancellationToken ct = default) => Cron.Parse((await Exec(Cron.ReadCommand(Prefix), ct).ConfigureAwait(false)).Output);

    /// <summary>Writes <paramref name="text"/> unless the crontab changed since <paramref name="read"/>; the previous one is kept as a backup.</summary>
    public async Task<CronSaveOutcome> SaveAsync(CronTab read, string text, CancellationToken ct = default)
    {
        foreach (var line in text.Split('\n')) if (line.Contains('\r') || line.Contains('\0')) throw new ArgumentException("Недопустимые символы в crontab.");
        var id = Guid.NewGuid().ToString("N")[..12];
        string directory, path;
        if (UploadOverride is not null) { directory = "/tmp"; path = "/tmp/.sshx-crontab-" + id; await UploadOverride(path, text, ct).ConfigureAwait(false); }
        else
        {
            if (!session.SftpAvailable) throw new InvalidOperationException("Для изменения crontab нужен SFTP: новый текст передаётся файлом.");
            directory = PosixPath.Combine(session.Files.HomeDirectory, ".cache/sshexplorer");
            await Exec("umask 077 && mkdir -p " + PosixPath.Quote(directory), ct).ConfigureAwait(false);
            path = PosixPath.Combine(directory, "crontab-" + id + ".txt");
            using var lease = await session.LeaseSftpAsync(ct).ConfigureAwait(false);
            using (lease.Client.Open(path, FileMode.CreateNew, FileAccess.Write)) { }
            lease.Client.ChangePermissions(path, 600);
            using var stream = lease.Client.Open(path, FileMode.Open, FileAccess.Write);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteAsync(text.AsMemory(), ct).ConfigureAwait(false);
        }
        var backup = PosixPath.Combine(directory, $"crontab-{(AsRoot ? "root" : "user")}-{DateTime.UtcNow:yyyyMMddHHmmss}-{id[..6]}.bak");
        var r = await Exec(Cron.InstallCommand(Prefix, path, read.Text, backup), ct).ConfigureAwait(false);
        if (r.Output.Contains("#changed", StringComparison.Ordinal)) return CronSaveOutcome.Changed;
        if (r.Output.Contains("#saved", StringComparison.Ordinal)) return CronSaveOutcome.Saved;
        throw new IOException("crontab не принял изменения: " + (r.Output + " " + r.Error).Trim());
    }
}
