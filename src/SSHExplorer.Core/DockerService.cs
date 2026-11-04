using Renci.SshNet;
using Renci.SshNet.Common;

namespace SSHExplorer.Core;

/// <summary>
/// Docker on one server through the session's SSH exec: nothing is installed on the server. All commands quote their
/// arguments; environment values go through a short-lived 0600 env-file written over SFTP, never through the command line.
/// </summary>
public sealed class DockerService(ServerSession session)
{
    string? cli;
    public string? Version { get; private set; }
    public string? Problem { get; private set; }
    public bool Available => cli is not null;
    public ServerSession Session => session;
    /// <summary>Replaces SSH exec in UI fixtures (no server); production always uses the session.</summary>
    internal Func<string, CancellationToken, Task<CommandResult>>? ExecuteOverride { get; set; }
    Task<CommandResult> Exec(string command, CancellationToken ct) => ExecuteOverride?.Invoke(command, ct) ?? session.ExecuteAsync(command, ct);

    public async Task<bool> ProbeAsync(CancellationToken ct = default)
    {
        var (found, version, problem) = DockerCommands.ParseProbe((await Exec(DockerCommands.Probe, ct).ConfigureAwait(false)).Output);
        cli = found; Version = version; Problem = problem; return found is not null;
    }

    string Cli => cli ?? throw new InvalidOperationException(Problem ?? "Docker ещё не проверен.");

    async Task<CommandResult> RunChecked(string command, CancellationToken ct)
    {
        var result = await Exec(command, ct).ConfigureAwait(false);
        if (result.ExitStatus != 0) throw new IOException(FirstLine(result.Error) ?? FirstLine(result.Output) ?? $"Docker завершился с кодом {result.ExitStatus}.");
        return result;
    }
    static string? FirstLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    public async Task<IReadOnlyList<DockerContainer>> ListAsync(CancellationToken ct = default) => DockerCommands.ParseList((await RunChecked(DockerCommands.List(Cli), ct).ConfigureAwait(false)).Output);
    public Task StartAsync(string id, CancellationToken ct = default) => RunChecked(DockerCommands.Action(Cli, "start", id), ct);
    public Task StopAsync(string id, CancellationToken ct = default) => RunChecked(DockerCommands.Action(Cli, "stop", id), ct);
    public Task RestartAsync(string id, CancellationToken ct = default) => RunChecked(DockerCommands.Action(Cli, "restart", id), ct);
    public Task RemoveAsync(string id, bool force, CancellationToken ct = default) => RunChecked(DockerCommands.Remove(Cli, id, force), ct);
    public async Task<string> LogsAsync(string id, int tail, CancellationToken ct = default) => (await Exec(DockerCommands.Logs(Cli, id, tail), ct).ConfigureAwait(false)).Output;

    /// <summary>Takes the run parameters of an existing container as a template (image env defaults left out).</summary>
    public async Task<DockerTemplate> TemplateFromAsync(string id, CancellationToken ct = default)
    {
        var inspect = (await RunChecked(DockerCommands.Inspect(Cli, id), ct).ConfigureAwait(false)).Output;
        try
        {
            var template = DockerCommands.FromInspect(inspect, null);
            var image = (await Exec(DockerCommands.InspectImage(Cli, template.Image), ct).ConfigureAwait(false)).Output;
            return image.TrimStart().StartsWith('[') ? DockerCommands.FromInspect(inspect, image) : template;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        { throw new IOException("Docker вернул неожиданный ответ на inspect: параметры контейнера не прочитаны.", ex); }
    }

    /// <summary>
    /// Runs the template. With <paramref name="replaceExisting"/> an existing container of that name is removed first
    /// (stop + remove; its anonymous volumes are kept). Returns the new container id.
    /// </summary>
    public async Task<string> RunAsync(DockerTemplate template, bool replaceExisting, CancellationToken ct = default)
    {
        var errors = DockerCommands.Validate(template, DockerSecrets.ValueOf);
        if (errors.Count > 0) throw new ArgumentException(string.Join("\n", errors));
        string? envFile = null; SftpClient? sftp = null; IDisposable? lease = null;
        try
        {
            if (template.Env.Count > 0)
            {
                if (!session.SftpAvailable) throw new InvalidOperationException("Для передачи переменных окружения нужен SFTP: значения не передаются в командной строке, чтобы их не было видно в списке процессов сервера.");
                var l = await session.LeaseSftpAsync(ct).ConfigureAwait(false); lease = l; sftp = l.Client;
                var directory = PosixPath.Combine(session.Files.HomeDirectory, ".cache/sshexplorer");
                await RunChecked("umask 077 && mkdir -p " + PosixPath.Quote(directory), ct).ConfigureAwait(false);
                envFile = PosixPath.Combine(directory, "env-" + Guid.NewGuid().ToString("N"));
                // Create empty, restrict to the owner, then write: the values are never readable by others.
                using (sftp.Open(envFile, FileMode.CreateNew, FileAccess.Write)) { }
                sftp.ChangePermissions(envFile, Convert.ToInt16("600", 8));
                using (var stream = sftp.Open(envFile, FileMode.Open, FileAccess.Write)) // just created empty; SSH.NET does not support Truncate
                using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
                    await writer.WriteAsync(DockerCommands.EnvFile(template, DockerSecrets.ValueOf).AsMemory(), ct).ConfigureAwait(false);
            }
            if (replaceExisting) await Exec(DockerCommands.Remove(Cli, template.ContainerName, force: true) + " 2>/dev/null || true", ct).ConfigureAwait(false);
            var result = await RunChecked(DockerCommands.Run(Cli, template, envFile), ct).ConfigureAwait(false);
            return result.Output.Trim();
        }
        finally
        {
            if (envFile is not null && sftp is not null) { try { sftp.DeleteFile(envFile); } catch (SshException) { } }
            lease?.Dispose();
        }
    }
}
