using System.Text.Json;

namespace SSHExplorer.Core;

/// <summary>
/// Key login for one connected server: list and edit the user's <c>authorized_keys</c>, and prove that a key works
/// by a brand-new SSH connection over the same route before the profile is switched to it. The new connection
/// accepts only the host key this session already verified.
/// </summary>
public sealed class KeyLoginService(ServerSession session, AppStore store)
{
    public ServerSession Session => session;
    /// <summary>Test hooks: run a command, or check a key login, without a server.</summary>
    public Func<string, CancellationToken, Task<CommandResult>>? ExecuteOverride { get; set; }
    public Func<string, string?, CancellationToken, Task<string?>>? VerifyOverride { get; set; }

    Task<CommandResult> Run(string command, CancellationToken ct) =>
        ExecuteOverride?.Invoke(command, ct) ?? session.ExecuteLongAsync(command, TimeSpan.FromSeconds(30), ct);

    public async Task<IReadOnlyList<AuthorizedKey>> ListAsync(CancellationToken ct = default)
    {
        var r = await Run(SshKeys.ListCommand, ct).ConfigureAwait(false);
        if (!r.Output.Contains("#end", StringComparison.Ordinal)) throw new IOException("Не удалось прочитать ~/.ssh/authorized_keys: " + (r.Error.Trim().Length > 0 ? r.Error.Trim() : "код " + r.ExitStatus));
        return SshKeys.ParseList(r.Output);
    }

    /// <summary>True when the key was added, false when it was already there.</summary>
    public async Task<bool> InstallAsync(AuthorizedKey key, CancellationToken ct = default)
    {
        var r = await Run(SshKeys.InstallCommand(key), ct).ConfigureAwait(false);
        if (r.Output.Contains("#added", StringComparison.Ordinal)) return true;
        if (r.Output.Contains("#present", StringComparison.Ordinal)) return false;
        throw new IOException("Не удалось добавить ключ в ~/.ssh/authorized_keys: " + (r.Error.Trim().Length > 0 ? r.Error.Trim() : "код " + r.ExitStatus));
    }

    public async Task RemoveAsync(AuthorizedKey key, CancellationToken ct = default)
    {
        var r = await Run(SshKeys.RemoveCommand(key), ct).ConfigureAwait(false);
        if (r.Output.Contains("#removed", StringComparison.Ordinal)) return;
        if (r.Output.Contains("#missing", StringComparison.Ordinal)) throw new IOException("Этой строки уже нет в ~/.ssh/authorized_keys — список обновлён.");
        throw new IOException("Не удалось изменить ~/.ssh/authorized_keys: " + (r.Error.Trim().Length > 0 ? r.Error.Trim() : "код " + r.ExitStatus));
    }

    /// <summary>The profile as it would be with key login.</summary>
    public static ServerProfile WithKey(ServerProfile profile, string privateKeyPath)
    {
        var copy = JsonSerializer.Deserialize<ServerProfile>(JsonSerializer.Serialize(profile))!;
        copy.Authentication = AuthenticationKind.PrivateKey; copy.PrivateKeyPath = privateKeyPath;
        return copy;
    }

    /// <summary>Null when a new connection logs in with this key; otherwise why not.</summary>
    public async Task<string?> VerifyAsync(string privateKeyPath, string? passphrase, CancellationToken ct = default)
    {
        if (VerifyOverride is not null) return await VerifyOverride(privateKeyPath, passphrase, ct).ConfigureAwait(false);
        var expected = session.VerifiedHostKey;
        await using var probe = new ServerSession(WithKey(session.Profile, privateKeyPath), store);
        try
        {
            await probe.ConnectAsync(new SessionCredentials(passphrase ?? ""),
                (key, _) => Task.FromResult(expected is not null && expected == key.Algorithm + ":" + key.Fingerprint ? TrustDecision.Session : TrustDecision.Reject), ct).ConfigureAwait(false);
            return probe.State == SessionState.Connected ? null : probe.Error ?? "Подключение не установлено.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { return ErrorMessages.Describe(ex); }
    }
}
