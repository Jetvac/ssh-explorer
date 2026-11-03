using System.Windows;
using SSHExplorer.Core;

namespace SSHExplorer.App;

/// <summary>One explicit connection command; de-duplicates only unfinished commands for the same profile.</summary>
internal sealed class ConnectionAttempt : IDisposable
{
    readonly CancellationTokenSource cancelled;
    readonly CancellationTokenSource deadline = new();
    readonly CancellationToken token;
    readonly Action release;
    readonly List<Task> writes = [];
    volatile bool finished;
    public Guid Id { get; } = Guid.NewGuid();
    public CancellationToken Token => token;
    public bool IsFinished => finished;
    public bool TimedOut => deadline.IsCancellationRequested;
    internal ConnectionAttempt(CancellationToken owner, Action release)
    { this.release = release; cancelled = CancellationTokenSource.CreateLinkedTokenSource(owner, deadline.Token); token = cancelled.Token; }
    public void BeginNetworkTimeout(int seconds) => deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(seconds, 3, 120)));
    public void Cancel() { if (!finished) cancelled.Cancel(); }
    public Task RecordAsync(string stage, string outcome, Exception? error = null)
    { var write = LocalDiagnostics.RecordOperationAsync(Id, stage, outcome, error); lock (writes) writes.Add(write); return write; }
    public void Observe(ConnectionStageEvent value) { var write = LocalDiagnostics.RecordStageAsync(value); lock (writes) writes.Add(write); }
    public Task FlushAsync() { lock (writes) return Task.WhenAll(writes.ToArray()); }
    public void Dispose()
    { if (finished) return; finished = true; deadline.CancelAfter(Timeout.InfiniteTimeSpan); release(); cancelled.Dispose(); deadline.Dispose(); }
}

internal sealed class ConnectionAttemptRegistry(CancellationToken owner)
{
    readonly Dictionary<Guid, ConnectionAttempt> pending = [];
    public ConnectionAttempt? TryBegin(Guid profile, bool independent = false)
    {
        if (!independent && pending.ContainsKey(profile)) return null;
        Guid key = independent ? Guid.NewGuid() : profile;
        var attempt = new ConnectionAttempt(owner, () => pending.Remove(key)); pending.Add(key, attempt); return attempt;
    }
    public void CancelAll() { foreach (var value in pending.Values.ToArray()) value.Cancel(); }
}

/// <summary>Serializes legitimate prompts per session and rejects cancelled/stale dispatcher requests.</summary>
internal sealed class ConnectionInteraction : IDisposable
{
    readonly Window owner;
    readonly ConnectionAttempt initial;
    readonly SemaphoreSlim prompts = new(1, 1);
    readonly CancellationTokenSource stop;
    readonly CancellationToken stopToken;
    volatile bool initializing = true, disposed;
    public ConnectionInteraction(Window owner, ServerSession session, ConnectionAttempt initial)
    {
        this.owner = owner; this.initial = initial;
        stop = CancellationTokenSource.CreateLinkedTokenSource(session.Lifetime); stopToken = stop.Token;
    }
    public void InitialFinished() => initializing = false;
    async Task<T> InvokeAsync<T>(Func<CancellationToken, T> show, CancellationToken request)
    {
        if (disposed) throw new OperationCanceledException();
        using var linked = initializing ? CancellationTokenSource.CreateLinkedTokenSource(stopToken, request, initial.Token) : CancellationTokenSource.CreateLinkedTokenSource(stopToken, request);
        var token = linked.Token;
        await prompts.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            if (owner.Dispatcher.HasShutdownStarted) throw new OperationCanceledException(token);
            return await owner.Dispatcher.InvokeAsync(() => { token.ThrowIfCancellationRequested(); return show(token); }, System.Windows.Threading.DispatcherPriority.Normal, token).Task.ConfigureAwait(false);
        }
        finally { prompts.Release(); }
    }
    public Task<TrustDecision> TrustAsync(HostKeyPrompt key, CancellationToken token) => InvokeAsync(ct => Dialogs.Trust(key, owner, ct), token);
    public Task<string?> PromptAsync(InteractivePrompt prompt, CancellationToken token) => InvokeAsync(ct => Dialogs.Interactive(prompt, owner, ct), token);
    public void Dispose()
    { if (disposed) return; disposed = true; stop.Cancel(); stop.Dispose(); /* Pending waiters own semaphore releases; never dispose it beneath a callback. */ }
}
