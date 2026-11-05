using Renci.SshNet;

namespace SSHExplorer.Core;

public sealed partial class ServerSession
{
    private readonly SemaphoreSlim _jumpGate = new(1, 1);
    private JumpTunnel? _jump;
    private ISet<string>? _jumpVisited;

    /// <summary>The address SSH.NET connects to: the server itself, or the local end of this session's jump tunnel.</summary>
    private async Task<(string Host, int Port)> ConnectEndpointAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Profile.ProxyJump) || ProxyJumpSpec.Parse(Profile.ProxyJump).Count == 0) return (Profile.Host.Trim('[', ']'), Profile.Port);
        await _jumpGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_jump is { IsAlive: true }) return ("127.0.0.1", _jump.Port);
            if (_jump is not null) { Release(_jump); _jump = null; }
            var tunnel = await JumpTunnel.OpenAsync(this, ct).ConfigureAwait(false);
            Register(tunnel); _jump = tunnel;
            return ("127.0.0.1", tunnel.Port);
        }
        finally { _jumpGate.Release(); }
    }

    /// <summary>
    /// Remote desktop through SSH: only the jump tunnel to <c>Profile.Host:Profile.Port</c> (3389) is opened and its
    /// loopback port returned. Nothing of the target's credentials goes over SSH; saved hop profiles sign in with their
    /// own secrets, and an address-only hop is refused for a password profile (see <see cref="ProxyJumpSpec.ResolveLast"/>).
    /// </summary>
    public async Task<int> OpenForwardOnlyAsync(Func<HostKeyPrompt, CancellationToken, Task<TrustDecision>> prompt, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ProxyJumpSpec.Parse(Profile.ProxyJump).Count == 0) throw new InvalidOperationException("Промежуточный сервер не задан.");
        _credentials ??= new SessionCredentials(""); _prompt = prompt; if (_connectOperationId == Guid.Empty) _connectOperationId = Guid.NewGuid();
        var (_, port) = await ConnectEndpointAsync(ct).ConfigureAwait(false);
        return port;
    }

    /// <summary>True while the tunnel opened by <see cref="OpenForwardOnlyAsync"/> is up.</summary>
    public bool ForwardAlive => _jump is { IsAlive: true };

    /// <summary>Description of the route for the status line, e.g. "через bastion → jump2".</summary>
    public string? Route => _jump?.Route;
    internal int? JumpPort => _jump?.Port;

    /// <summary>
    /// A connection to a jump host and a loopback-only local port forwarded to the next host. Every client of the
    /// owning session (exec, SFTP, terminals, monitor, transfers) opens its own SSH channel through the same tunnel;
    /// the target's host key is still verified against the target's name and port.
    /// </summary>
    private sealed class JumpTunnel : IDisposable
    {
        readonly ServerSession hop; readonly SshClient client; readonly ForwardedPortLocal forward;
        public int Port => (int)forward.BoundPort;
        public bool IsAlive => client.IsConnected && forward.IsStarted;
        public string Route { get; }

        JumpTunnel(ServerSession hop, SshClient client, ForwardedPortLocal forward)
        { this.hop = hop; this.client = client; this.forward = forward; Route = (hop.Route is { } inner ? inner + " → " : "") + hop.Profile.Name; }

        public static async Task<JumpTunnel> OpenAsync(ServerSession owner, CancellationToken ct)
        {
            var credentials = owner._credentials ?? throw new InvalidOperationException("Сессия не подключена.");
            owner._jumpVisited ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase) { owner.Profile.Host.Trim('[', ']').ToLowerInvariant() + ":" + owner.Profile.Port };
            var resolved = ProxyJumpSpec.ResolveLast(owner.Profile, owner._store.Profiles, owner._jumpVisited);
            SessionCredentials hopCredentials;
            if (!resolved.FromSavedProfile) hopCredentials = credentials with { };                        // same agent/key file as the target
            else if (!resolved.NeedsSavedSecret && resolved.Profile.Authentication == AuthenticationKind.Agent) hopCredentials = new SessionCredentials("", credentials.Prompt, credentials.CancellablePrompt);
            else
            {
                // A saved hop profile signs in with its own saved secret; the target's password is never reused.
                var secret = resolved.Profile.SaveSecret ? await owner._store.ReadSecretAsync(resolved.Profile.Id, ct).ConfigureAwait(false) : null;
                if (secret is null && resolved.NeedsSavedSecret)
                    throw new InvalidOperationException($"Для промежуточного узла «{resolved.Profile.Name}» нужен сохранённый пароль: откройте его профиль и сохраните секрет, или используйте ключ/агент.");
                hopCredentials = new SessionCredentials(secret ?? "", credentials.Prompt, credentials.CancellablePrompt);
            }
            var hop = new ServerSession(resolved.Profile, owner._store)
            {
                _credentials = hopCredentials, _prompt = owner._prompt, _connectOperationId = owner._connectOperationId, _jumpVisited = owner._jumpVisited,
            };
            hop.StageChanged += (_, e) => owner.StageChanged?.Invoke(owner, e);
            SshClient? client = null; ForwardedPortLocal? forward = null;
            try
            {
                // A bastion may forbid shell and SFTP: only the transport and port forwarding are required.
                client = await hop.CreateSshClientAsync(ct).ConfigureAwait(false);
                forward = new ForwardedPortLocal("127.0.0.1", 0, owner.Profile.Host.Trim('[', ']'), (uint)owner.Profile.Port);
                client.AddForwardedPort(forward); forward.Start();
                return new JumpTunnel(hop, client, forward);
            }
            catch (Exception ex)
            {
                try { forward?.Stop(); } catch { }
                await hop.DisposeAsync().ConfigureAwait(false);
                if (ex is OperationCanceledException or HostKeyChangedException or InvalidOperationException) throw;
                throw new IOException($"Промежуточный узел {resolved.Profile.Name} ({resolved.Profile.Endpoint}): {ErrorMessages.Describe(ex)}", ex);
            }
        }

        public void Dispose()
        {
            try { if (forward.IsStarted) forward.Stop(); } catch { }
            try { forward.Dispose(); } catch { }
            _ = hop.DisposeAsync().AsTask();
        }
    }
}
