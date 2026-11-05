using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace SSHExplorer.Core;

public sealed partial class ServerSession : IAsyncDisposable
{
    private readonly AppStore _store;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<IDisposable, byte> _resources = new();
    private readonly ConcurrentDictionary<BaseClient, ClientResource> _clients = new();
    private readonly HashSet<string> _sessionTrusted = [];
    private readonly SemaphoreSlim _execGate = new(1, 1);
    private readonly object _lifecycle = new();
    private SessionCredentials? _credentials;
    private Func<HostKeyPrompt, CancellationToken, Task<TrustDecision>>? _prompt;
    private SshClient? _exec;
    private volatile bool _disposed;
    private int _connecting;
    private Task? _disposeTask;
    private Guid _connectOperationId;
    public Guid Id { get; } = Guid.NewGuid();
    public ServerProfile Profile { get; }
    public string? VerifiedHostKey { get; private set; }
    public SessionState State { get; private set; } = SessionState.Closed;
    public string? Error { get; private set; }
    public string? SftpError { get; private set; }
    public string? ShellError { get; private set; }
    public bool ShellAvailable { get; private set; }
    public bool SftpAvailable => Files.Available;
    public RemoteFileService Files { get; }
    public CancellationToken Lifetime => _lifetime.Token;
    public event EventHandler? StateChanged;
    public event EventHandler<ConnectionStageEvent>? StageChanged;
    public ServerSession(ServerProfile profile, AppStore store) { Profile = profile; _store = store; Files = new RemoteFileService(this); }
    private void SetState(SessionState value, string? error = null)
    {
        lock (_lifecycle)
        {
            if (_disposed && value is not SessionState.Closing and not SessionState.Closed) return;
            State = value; Error = error;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
    private void Report(Guid operation, Guid? parent, ConnectionStage stage, ConnectionStageOutcome outcome, Exception? error = null)
    {
        var value = ConnectionStageEvent.Create(Id, operation, parent, stage, outcome, error);
        // Diagnostic sinks must not turn a transport callback into an authentication failure.
        if (StageChanged is { } handlers)
            foreach (EventHandler<ConnectionStageEvent> handler in handlers.GetInvocationList())
                try { handler(this, value); } catch { }
    }
    private static ConnectionStageOutcome Outcome(Exception error) => error is OperationCanceledException ? ConnectionStageOutcome.Cancelled : ConnectionStageOutcome.Failed;

    public Task ConnectAsync(SessionCredentials credentials, Func<HostKeyPrompt, Task<TrustDecision>> prompt, CancellationToken ct = default, Guid? operationId = null)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return ConnectAsync(credentials, (key, _) => prompt(key), ct, operationId);
    }

    public async Task ConnectAsync(SessionCredentials credentials, Func<HostKeyPrompt, CancellationToken, Task<TrustDecision>> prompt, CancellationToken ct = default, Guid? operationId = null)
    {
        ArgumentNullException.ThrowIfNull(credentials); ArgumentNullException.ThrowIfNull(prompt);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.CompareExchange(ref _connecting, 1, 0) != 0) throw new InvalidOperationException("Подключение этой сессии уже выполняется.");
        Guid operation = operationId ?? Guid.NewGuid();
        bool started = false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, Lifetime);
        try
        {
            if (State == SessionState.Connected) throw new InvalidOperationException("Сессия уже подключена.");
            Report(operation, null, ConnectionStage.Connect, ConnectionStageOutcome.Started); started = true;
            linked.Token.ThrowIfCancellationRequested(); Profile.Validate();
            _credentials = credentials; _prompt = prompt; _connectOperationId = operation;
            ShellAvailable = false; ShellError = null; SftpError = null;
            SetState(SessionState.Connecting);
            _exec = await CreateSshClientAsync(linked.Token).ConfigureAwait(false);
            Report(operation, null, ConnectionStage.ExecProbe, ConnectionStageOutcome.Started);
            try
            {
                var probe = await ExecuteAsync("printf 'SSHExplorer-probe'", linked.Token).ConfigureAwait(false);
                ShellAvailable = probe.ExitStatus == 0 && probe.Output == "SSHExplorer-probe";
                if (!ShellAvailable) throw new IOException("SSH exec недоступен для этой учётной записи.");
                Report(operation, null, ConnectionStage.ExecProbe, ConnectionStageOutcome.Succeeded);
            }
            catch (Exception ex)
            {
                Report(operation, null, ConnectionStage.ExecProbe, Outcome(ex), ex);
                if (ex is OperationCanceledException or HostKeyChangedException) throw;
                ShellError = ErrorMessages.Describe(ex);
            }
            Report(operation, null, ConnectionStage.Sftp, ConnectionStageOutcome.Started);
            try
            {
                await Files.ConnectAsync(linked.Token).ConfigureAwait(false);
                Report(operation, null, ConnectionStage.Sftp, ConnectionStageOutcome.Succeeded);
            }
            catch (Exception ex)
            {
                Report(operation, null, ConnectionStage.Sftp, Outcome(ex), ex);
                if (ex is OperationCanceledException or HostKeyChangedException) throw;
                SftpError = ErrorMessages.Describe(ex);
            }
            linked.Token.ThrowIfCancellationRequested();
            if (!ShellAvailable && !SftpAvailable) throw new IOException("Нет доступных SSH-подсистем для этой учётной записи.");
            SetState(SessionState.Connected);
            Report(operation, null, ConnectionStage.Connect, ConnectionStageOutcome.Succeeded);
        }
        catch (Exception ex)
        {
            if (started)
            {
                await Task.Run(() => { foreach (var resource in _resources.Keys) Release(resource); }).ConfigureAwait(false);
                _exec = null; _credentials = null; _prompt = null; ShellAvailable = false;
                SetState(SessionState.Disconnected, ErrorMessages.Describe(ex));
                Report(operation, null, ConnectionStage.Connect, Outcome(ex), ex);
            }
            throw;
        }
        finally { Volatile.Write(ref _connecting, 0); }
    }

    private ConnectionInfo BuildConnectionInfo(ConnectionContext context, (string Host, int Port) endpoint, CancellationToken ct, Action<IDisposable> registerKey)
    {
        var credentials = _credentials ?? throw new InvalidOperationException("Сессия не подключена.");
        var methods = SessionAuthentication.CreateMethods(Profile, credentials, registerKey, prompt =>
        {
            Report(context.Id, context.Parent, ConnectionStage.InteractivePrompt, ConnectionStageOutcome.Started);
            try
            {
                var response = SessionAuthentication.RespondAsync(credentials, prompt, ct).GetAwaiter().GetResult();
                Report(context.Id, context.Parent, ConnectionStage.InteractivePrompt, ConnectionStageOutcome.Succeeded);
                return response;
            }
            catch (Exception ex)
            {
                context.RecordError(ex);
                Report(context.Id, context.Parent, ConnectionStage.InteractivePrompt, Outcome(ex), ex); throw;
            }
        });
        return new ConnectionInfo(endpoint.Host, endpoint.Port, Profile.Username, methods)
        { Timeout = TimeSpan.FromSeconds(Profile.TimeoutSeconds), Encoding = new UTF8Encoding(false, true) };
    }
    private async Task<T> ConnectClientAsync<T>(Func<ConnectionInfo, T> factory, CancellationToken caller, Guid? operationId = null) where T : BaseClient
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Profile.TimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, Lifetime, timeout.Token);
        var ct = linked.Token;
        ct.ThrowIfCancellationRequested();
        var context = new ConnectionContext(operationId ?? Guid.NewGuid(), _connectOperationId == Guid.Empty ? null : _connectOperationId);
        Report(context.Id, context.Parent, ConnectionStage.Transport, ConnectionStageOutcome.Started);
        T? client = null;
        List<IDisposable> privateKeys = [];
        ConnectionInfo? connection = null;
        Task? connect = null;
        ClientResource? ownership = null;
        bool transportComplete = false, authenticationStarted = false, authenticationComplete = false;
        try
        {
            // Key file decoding/decryption is local I/O and CPU work; it also belongs off the UI.
            // With ProxyJump this opens (or reuses) the session's tunnel; trust is still checked for Profile.Host:Port.
            var endpoint = await ConnectEndpointAsync(ct).ConfigureAwait(false);
            connection = await Task.Run(() => BuildConnectionInfo(context, endpoint, ct, privateKeys.Add), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            client = factory(connection);
            ownership = new ClientResource(client);
            _clients.TryAdd(client, ownership); Register(ownership);
            client.HostKeyReceived += (_, e) =>
            {
                e.CanTrust = false;
                try
                {
                    ct.ThrowIfCancellationRequested();
                    if (!transportComplete) { transportComplete = true; Report(context.Id, context.Parent, ConnectionStage.Transport, ConnectionStageOutcome.Succeeded); }
                    bool initial = State is SessionState.Connecting or SessionState.AwaitingTrust;
                    if (initial) SetState(SessionState.AwaitingTrust);
                    Report(context.Id, context.Parent, ConnectionStage.HostTrust, ConnectionStageOutcome.Started);
                    e.CanTrust = _store.Trust.VerifyAsync(Profile.Host, Profile.Port, e.HostKeyName, e.HostKey, _sessionTrusted,
                        key => _prompt!(key, ct), ct).GetAwaiter().GetResult();
                    if (!e.CanTrust) throw new OperationCanceledException("Доверие ключу сервера не подтверждено.", ct);
                    lock (_lifecycle) VerifiedHostKey ??= e.HostKeyName + ":SHA256:" + Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(e.HostKey)).TrimEnd('=');
                    Report(context.Id, context.Parent, ConnectionStage.HostTrust, ConnectionStageOutcome.Succeeded);
                    if (initial && State == SessionState.AwaitingTrust) SetState(SessionState.Connecting);
                    if (!authenticationStarted) { authenticationStarted = true; Report(context.Id, context.Parent, ConnectionStage.Authentication, ConnectionStageOutcome.Started); }
                }
                catch (Exception ex)
                {
                    context.RecordError(ex);
                    Report(context.Id, context.Parent, ConnectionStage.HostTrust, Outcome(ex), ex);
                }
            };
            // An auxiliary transfer/PTY transport failing must not disconnect the independent main transports.
            client.ErrorOccurred += (_, e) =>
            {
                if (!_disposed && State == SessionState.Connected && ReferenceEquals(client, _exec))
                    SetState(SessionState.Disconnected, ErrorMessages.Describe(e.Exception));
            };
            client.KeepAliveInterval = TimeSpan.FromSeconds(25);
            using var cancelConnection = ct.Register(() => ThreadPool.QueueUserWorkItem(_ => Release(client)));
            // SSH.NET authentication events are synchronous callbacks; run them away from the UI thread.
            connect = Task.Run(() => client.ConnectAsync(ct), ct);
            _ = connect.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            await connect.WaitAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(_disposed, this);
            authenticationComplete = true;
            Report(context.Id, context.Parent, ConnectionStage.Authentication, ConnectionStageOutcome.Succeeded);
            return client;
        }
        catch (Exception caught)
        {
            Exception error = context.Error ?? caught;
            if (error is not SshOperationTimeoutException && timeout.IsCancellationRequested && !caller.IsCancellationRequested && !Lifetime.IsCancellationRequested)
                error = new TimeoutException("Истекло время ожидания подключения или авторизации.", error);
            // SSH.NET's kernel wait can time out before the CTS timer callback runs.
            // Disposing a CTS does not cancel it: revoke prompts on every failed handshake,
            // while retaining the failure chosen before cancellation can unwind callbacks.
            try { await linked.CancelAsync().ConfigureAwait(false); }
            catch (AggregateException) { /* Caller cancellation registrations cannot replace the primary handshake failure. */ }
            if (client is not null)
            {
                // SFTP OnConnected can fail after SSH authentication has already succeeded.
                if (!authenticationComplete && connection?.IsAuthenticated == true)
                { authenticationComplete = true; Report(context.Id, context.Parent, ConnectionStage.Authentication, ConnectionStageOutcome.Succeeded); }
                Release(client);
            }
            if (!transportComplete) Report(context.Id, context.Parent, ConnectionStage.Transport, Outcome(error), error);
            else if (authenticationStarted && !authenticationComplete) Report(context.Id, context.Parent, ConnectionStage.Authentication, Outcome(error), error);
            ExceptionDispatchInfo.Capture(error).Throw(); throw;
        }
        finally
        {
            // Cancellation can return before vendor authentication has unwound. Do not dispose key
            // material while that worker can still be using it. No reconnect uses these parsed keys.
            if (connect is null || connect.IsCompleted) FinishAuthentication();
            else _ = connect.ContinueWith(_ => FinishAuthentication(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            void FinishAuthentication()
            {
                ownership?.FinishConnecting();
                foreach (var key in privateKeys) try { key.Dispose(); } catch { }
            }
        }
    }
    internal Task<SftpClient> CreateSftpClientAsync(CancellationToken ct) => ConnectClientAsync(info => new SftpClient(info)
        { OperationTimeout = TimeSpan.FromSeconds(Profile.TimeoutSeconds), BufferSize = 64 * 1024 }, ct);
    internal async Task<SftpLease> LeaseSftpAsync(CancellationToken ct) => new(this, await CreateSftpClientAsync(ct).ConfigureAwait(false));
    private void Register(IDisposable resource)
    {
        lock (_lifecycle)
        {
            if (!_disposed) { _resources.TryAdd(resource, 0); return; }
        }
        resource.Dispose(); throw new OperationCanceledException(Lifetime);
    }
    private void Release(IDisposable resource)
    {
        if (resource is BaseClient client)
        {
            if (_clients.TryRemove(client, out var ownership)) Release(ownership);
            return;
        }
        if (resource is ClientResource connection) _clients.TryRemove(connection.Client, out _);
        if (_resources.TryRemove(resource, out _)) { try { resource.Dispose(); } catch { } }
    }
    internal Task<SshClient> CreateSshClientAsync(CancellationToken ct) => ConnectClientAsync(info => new SshClient(info), ct);
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
    /// <summary>
    /// A command that may run long (docker stop/run with an image pull) on its own SSH connection with its own timeout,
    /// so it never holds the shared exec channel that monitoring and quick commands use.
    /// </summary>
    public async Task<CommandResult> ExecuteLongAsync(string command, TimeSpan timeout, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, Lifetime);
        var client = await CreateSshClientAsync(linked.Token).ConfigureAwait(false);
        try
        {
            using var cmd = client.CreateCommand(command, Encoding.UTF8);
            cmd.CommandTimeout = timeout;
            await cmd.ExecuteAsync(linked.Token).ConfigureAwait(false);
            if (cmd.Result.Length > 16 * 1024 * 1024 || cmd.Error.Length > 1024 * 1024) throw new IOException("Ответ сервера превысил ограничение размера.");
            return new CommandResult(cmd.Result, cmd.Error, cmd.ExitStatus ?? -1);
        }
        finally { Release(client); }
    }
    /// <summary>
    /// Runs a long-lived command (journalctl -f, tail -F) on its own SSH connection and hands every output line to
    /// <paramref name="onLine"/> as it arrives (on a background thread). Cancelling closes the channel; commands built with
    /// <see cref="LogFollow.Wrap"/> also end on the server when the channel's input closes. Returns the exit status.
    /// </summary>
    public async Task<int?> StreamLinesAsync(string command, Action<string> onLine, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, Lifetime);
        var client = await CreateSshClientAsync(linked.Token).ConfigureAwait(false);
        try
        {
            using var cmd = client.CreateCommand(command, Encoding.UTF8);
            var run = cmd.ExecuteAsync(linked.Token);
            var output = cmd.OutputStream;
            var reader = Task.Run(() =>
            {
                try
                {
                    using var lines = new StreamReader(output, Encoding.UTF8, false, 8192, leaveOpen: true);
                    string? line; while ((line = lines.ReadLine()) is not null) onLine(line);
                }
                catch (Exception ex) when (ex is ObjectDisposedException or IOException) { }
            });
            try { await run.ConfigureAwait(false); }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            try { await reader.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch (TimeoutException) { }
            linked.Token.ThrowIfCancellationRequested();
            return cmd.ExitStatus;
        }
        finally { Release(client); }
    }
    public async Task<ShellStream> CreateShellAsync(uint columns = 100, uint rows = 30, string? directory = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, Lifetime);
        var operation = Guid.NewGuid();
        Report(operation, _connectOperationId, ConnectionStage.Pty, ConnectionStageOutcome.Started);
        SshClient? client = null; ShellStream? stream = null;
        try
        {
            client = await ConnectClientAsync(info => new SshClient(info), linked.Token, operation).ConfigureAwait(false);
            using var cancelPty = linked.Token.Register(() => ThreadPool.QueueUserWorkItem(_ => Release(client)));
            stream = await Task.Run(() =>
            {
                linked.Token.ThrowIfCancellationRequested();
                var shell = client.CreateShellStream("xterm-256color", Math.Clamp(columns, 2, 1000), Math.Clamp(rows, 2, 1000), 0, 0, 65536);
                Register(shell);
                shell.Closed += (_, _) => { _ = Task.Run(() => { Release(shell); Release(client); }); };
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    var path = PosixPath.Normalize(directory, Files.HomeDirectory);
                    var data = Encoding.UTF8.GetBytes("cd " + PosixPath.Quote(path) + "\r");
                    shell.Write(data, 0, data.Length); shell.Flush();
                }
                return shell;
            }, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            Report(operation, _connectOperationId, ConnectionStage.Pty, ConnectionStageOutcome.Succeeded);
            return stream;
        }
        catch (Exception ex)
        {
            if (stream is not null) Release(stream); if (client is not null) Release(client);
            Report(operation, _connectOperationId, ConnectionStage.Pty, Outcome(ex), ex); throw;
        }
    }
    public ValueTask DisposeAsync()
    {
        lock (_lifecycle)
        {
            if (_disposeTask is null) { _disposed = true; _disposeTask = Task.Run(DisposeCoreAsync); }
            return new ValueTask(_disposeTask);
        }
    }
    private async Task DisposeCoreAsync()
    {
        SetState(SessionState.Closing);
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await Task.Run(() => { foreach (var resource in _resources.Keys) Release(resource); }).ConfigureAwait(false);
        _credentials = null; _prompt = null; lock (_sessionTrusted) _sessionTrusted.Clear();
        ShellAvailable = false; SetState(SessionState.Closed); StateChanged = null;
        // Keep diagnostic observers until the in-flight async calls unwind: they still owe
        // their cancellation result after DisposeAsync has revoked every session resource.
    }
    private sealed class ConnectionContext(Guid id, Guid? parent)
    {
        private Exception? _error;
        public Guid Id { get; } = id;
        public Guid? Parent { get; } = parent;
        public Exception? Error => _error;
        public void RecordError(Exception error) => Interlocked.CompareExchange(ref _error, error, null);
    }
    private sealed class ClientResource(BaseClient client) : IDisposable
    {
        private readonly object _gate = new();
        private bool _connecting = true, _disposeRequested, _released;
        public BaseClient Client { get; } = client;
        public void FinishConnecting()
        {
            bool release;
            lock (_gate) { _connecting = false; release = _disposeRequested && !_released; if (release) _released = true; }
            if (release) try { Client.Dispose(); } catch { }
        }
        public void Dispose()
        {
            bool release;
            lock (_gate) { _disposeRequested = true; release = !_connecting && !_released; if (release) _released = true; }
            // SSH.NET assigns BaseClient.Session only after authentication. Disposing earlier can
            // miss its still-local Session and allow late success to attach an orphaned transport.
            if (release) Client.Dispose();
        }
    }
    internal sealed class SftpLease(ServerSession owner, SftpClient client) : IDisposable
    {
        public SftpClient Client { get; } = client;
        public void Dispose() => owner.Release(Client);
    }
}
