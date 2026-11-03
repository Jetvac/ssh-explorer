using System.Collections.Concurrent;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace SSHExplorer.Core;

public sealed class ServerSession : IAsyncDisposable
{
    private readonly AppStore _store;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<IDisposable, byte> _resources = new();
    private readonly HashSet<string> _sessionTrusted = [];
    private readonly SemaphoreSlim _execGate = new(1, 1);
    private SessionCredentials? _credentials;
    private Func<HostKeyPrompt, Task<TrustDecision>>? _prompt;
    private SshClient? _exec;
    private bool _disposed;
    public Guid Id { get; } = Guid.NewGuid();
    public ServerProfile Profile { get; }
    public SessionState State { get; private set; } = SessionState.Closed;
    public string? Error { get; private set; }
    public string? SftpError { get; private set; }
    public string? ShellError { get; private set; }
    public bool ShellAvailable { get; private set; }
    public bool SftpAvailable => Files.Available;
    public RemoteFileService Files { get; }
    public CancellationToken Lifetime => _lifetime.Token;
    public event EventHandler? StateChanged;
    public ServerSession(ServerProfile profile, AppStore store) { Profile = profile; _store = store; Files = new RemoteFileService(this); }
    private void SetState(SessionState value, string? error = null) { State = value; Error = error; StateChanged?.Invoke(this, EventArgs.Empty); }
    public async Task ConnectAsync(SessionCredentials credentials, Func<HostKeyPrompt, Task<TrustDecision>> prompt, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Profile.Validate(); _credentials = credentials; _prompt = prompt;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, Lifetime);
        SetState(SessionState.Connecting);
        try
        {
            _exec = await CreateSshClientAsync(linked.Token).ConfigureAwait(false);
            try
            {
                var probe = await ExecuteAsync("printf 'SSHExplorer-probe'", linked.Token).ConfigureAwait(false);
                ShellAvailable = probe.ExitStatus == 0 && probe.Output == "SSHExplorer-probe";
                if (!ShellAvailable) ShellError = "SSH exec недоступен для этой учётной записи.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not HostKeyChangedException) { ShellError = ErrorMessages.Describe(ex); }
            try { await Files.ConnectAsync(linked.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not HostKeyChangedException) { SftpError = ErrorMessages.Describe(ex); }
            if (!ShellAvailable && !SftpAvailable) throw new IOException($"Нет доступных подсистем. Терминал: {ShellError}; SFTP: {SftpError}");
            SetState(SessionState.Connected);
        }
        catch (Exception ex) { SetState(SessionState.Disconnected, ErrorMessages.Describe(ex)); throw; }
    }
    private ConnectionInfo BuildConnectionInfo()
    {
        if (_credentials is null) throw new InvalidOperationException("Сессия не подключена.");
        var secret = _credentials.Secret;
        AuthenticationMethod[] methods;
        if (Profile.Authentication == AuthenticationKind.PrivateKey)
        {
            var key = string.IsNullOrEmpty(secret) ? new PrivateKeyFile(Profile.PrivateKeyPath) : new PrivateKeyFile(Profile.PrivateKeyPath, secret);
            _resources.TryAdd(key, 0);
            methods = [new PrivateKeyAuthenticationMethod(Profile.Username, key)];
        }
        else
        {
            var interactive = new KeyboardInteractiveAuthenticationMethod(Profile.Username);
            interactive.AuthenticationPrompt += (_, args) =>
            {
                foreach (var p in args.Prompts)
                {
                    if (_credentials.Prompt is not null)
                        p.Response = _credentials.Prompt(new InteractivePrompt(args.Instruction, p.Request, p.IsEchoed)).WaitAsync(Lifetime).GetAwaiter().GetResult() ?? throw new OperationCanceledException("Интерактивный вход отменён.");
                    // A static password can satisfy password-style prompts only; never reuse it as an OTP.
                    else if (p.Request.Contains("password", StringComparison.OrdinalIgnoreCase) || p.Request.Contains("пароль", StringComparison.OrdinalIgnoreCase)) p.Response = secret;
                    else throw new SshAuthenticationException("Сервер запросил дополнительный интерактивный ответ, не являющийся паролем. Используйте настроенный парольный или ключевой вход.");
                }
            };
            methods = Profile.Authentication == AuthenticationKind.KeyboardInteractive ? [interactive] : [new PasswordAuthenticationMethod(Profile.Username, secret), interactive];
        }
        return new ConnectionInfo(Profile.Host.Trim('[', ']'), Profile.Port, Profile.Username, methods)
        { Timeout = TimeSpan.FromSeconds(Math.Clamp(Profile.TimeoutSeconds, 3, 120)), Encoding = new UTF8Encoding(false, true) };
    }
    private async Task<T> ConnectClientAsync<T>(T client, CancellationToken ct) where T : BaseClient
    {
        _resources.TryAdd(client, 0);
        var privateKeys = client.ConnectionInfo.AuthenticationMethods.OfType<PrivateKeyAuthenticationMethod>().SelectMany(x => x.KeyFiles).OfType<IDisposable>().ToArray();
        Exception? trustError = null;
        client.HostKeyReceived += (_, e) =>
        {
            e.CanTrust = false;
            try
            {
                bool initial = State is SessionState.Connecting or SessionState.AwaitingTrust;
                if (initial) SetState(SessionState.AwaitingTrust);
                e.CanTrust = _store.Trust.VerifyAsync(Profile.Host, Profile.Port, e.HostKeyName, e.HostKey, _sessionTrusted, _prompt!, ct).GetAwaiter().GetResult();
                if (!e.CanTrust) trustError = new OperationCanceledException("Доверие ключу сервера не подтверждено.");
                if (initial && State == SessionState.AwaitingTrust) SetState(SessionState.Connecting);
            }
            catch (Exception ex) { trustError = ex; }
        };
        client.ErrorOccurred += (_, e) => { if (!_disposed && State == SessionState.Connected) SetState(SessionState.Disconnected, ErrorMessages.Describe(e.Exception)); };
        client.KeepAliveInterval = TimeSpan.FromSeconds(25);
        try
        {
            // SSH.NET raises HostKeyReceived before invoking the authentication methods.
            await Task.Run(async () => await client.ConnectAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            if (_disposed) { client.Dispose(); throw new OperationCanceledException(Lifetime); }
            return client;
        }
        catch { Release(client); if (trustError is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(trustError).Throw(); throw; }
        finally
        {
            // Private key material is only needed while authenticating this connection. Rekeying
            // an established SSH transport does not repeat user authentication.
            foreach (var key in privateKeys) Release(key);
        }
    }
    internal Task<SftpClient> CreateSftpClientAsync(CancellationToken ct) => ConnectClientAsync(new SftpClient(BuildConnectionInfo()) { OperationTimeout = TimeSpan.FromSeconds(Math.Clamp(Profile.TimeoutSeconds, 5, 120)), BufferSize = 64 * 1024 }, ct);
    internal async Task<SftpLease> LeaseSftpAsync(CancellationToken ct) => new(this, await CreateSftpClientAsync(ct).ConfigureAwait(false));
    private void Release(IDisposable resource)
    { if (_resources.TryRemove(resource, out _)) { try { resource.Dispose(); } catch { } } }
    internal Task<SshClient> CreateSshClientAsync(CancellationToken ct) => ConnectClientAsync(new SshClient(BuildConnectionInfo()), ct);
    public async Task<CommandResult> ExecuteAsync(string command, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, Lifetime);
        await _execGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (_exec?.IsConnected != true) throw new IOException("SSH exec не подключён.");
            using var cmd = _exec.CreateCommand(command, Encoding.UTF8);
            cmd.CommandTimeout = TimeSpan.FromSeconds(10);
            await cmd.ExecuteAsync(linked.Token).ConfigureAwait(false);
            if (cmd.Result.Length > 16 * 1024 * 1024 || cmd.Error.Length > 1024 * 1024) throw new IOException("Ответ сервера превысил ограничение размера.");
            return new CommandResult(cmd.Result, cmd.Error, cmd.ExitStatus ?? -1);
        }
        finally { _execGate.Release(); }
    }
    public async Task<ShellStream> CreateShellAsync(uint columns = 100, uint rows = 30, string? directory = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, Lifetime);
        var client = await CreateSshClientAsync(linked.Token).ConfigureAwait(false);
        try { return await Task.Run(() =>
        {
            linked.Token.ThrowIfCancellationRequested();
            var stream = client.CreateShellStream("xterm-256color", Math.Clamp(columns, 2, 1000), Math.Clamp(rows, 2, 1000), 0, 0, 65536);
            _resources.TryAdd(stream, 0);
            stream.Closed += (_, _) => { _ = Task.Run(() => { Release(stream); Release(client); }); };
            if (!string.IsNullOrWhiteSpace(directory))
            {
                var path = PosixPath.Normalize(directory, Files.HomeDirectory);
                var data = Encoding.UTF8.GetBytes("cd " + PosixPath.Quote(path) + "\r");
                stream.Write(data, 0, data.Length); stream.Flush();
            }
            return stream;
        }, linked.Token).ConfigureAwait(false); }
        catch { Release(client); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true; SetState(SessionState.Closing);
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await Task.Run(() => { foreach (var resource in _resources.Keys) Release(resource); }).ConfigureAwait(false);
        _credentials = null; _prompt = null; lock (_sessionTrusted) _sessionTrusted.Clear();
        SetState(SessionState.Closed); StateChanged = null;
    }
    internal sealed class SftpLease(ServerSession owner, SftpClient client) : IDisposable
    {
        public SftpClient Client { get; } = client;
        public void Dispose() => owner.Release(Client);
    }
}
