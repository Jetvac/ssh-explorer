namespace SSHExplorer.Core;

public sealed class RemoteEditWorkspace : IAsyncDisposable
{
    readonly List<RemoteEditSession> documents = [];
    readonly SemaphoreSlim openGate = new(1, 1);
    readonly CancellationTokenSource lifetime = new();
    readonly Task polling;
    public string Root { get; }
    public int UnreadableJournals { get; private set; }
    public IReadOnlyList<RemoteEditSession> Documents { get { lock (documents) return documents.ToArray(); } }
    public event EventHandler? Changed;
    public RemoteEditWorkspace(string dataDirectory)
    {
        Root = Path.Combine(dataDirectory, "RemoteEdits"); Directory.CreateDirectory(Root);
        if ((File.GetAttributes(Root) & FileAttributes.ReparsePoint) != 0) throw new IOException("Каталог рабочих копий не должен быть reparse point.");
        foreach (var directory in Directory.EnumerateDirectories(Root).Take(1000))
            try { var document = RemoteEditSession.Restore(directory); documents.Add(document); document.Changed += OnChanged; }
            catch { UnreadableJournals++; /* Never delete unreadable journals or log document contents. */ }
        polling = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            try { while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false)) await ReconcileAsync().ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        });
    }
    void OnChanged(object? sender, EventArgs e) { try { Changed?.Invoke(this, EventArgs.Empty); } catch { } }
    public async Task<RemoteEditSession> OpenAsync(IRemoteDocumentTransport transport, string path, string name, bool followLink, string? encoding = null, CancellationToken ct = default)
    {
        await openGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var location = await transport.LocateAsync(path, followLink, ct).ConfigureAwait(false);
            var existing = Documents.FirstOrDefault(d => d.Record.State != RemoteEditState.Completed && d.Record.Identity == transport.Identity && d.Record.Location.TargetPath == location.TargetPath);
            if (existing is not null) return existing;
            var document = await RemoteEditSession.OpenAsync(Root, transport, path, name, followLink, encoding, ct).ConfigureAwait(false);
            lock (documents) documents.Add(document); document.Changed += OnChanged; OnChanged(this, EventArgs.Empty); return document;
        }
        finally { openGate.Release(); }
    }
    public async Task ReconcileAsync()
    {
        foreach (var document in Documents.Where(d => d.Record.State != RemoteEditState.Completed))
            try { await document.ReconcileAsync(lifetime.Token).ConfigureAwait(false); } catch (OperationCanceledException) { } catch { }
    }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel(); try { await polling.ConfigureAwait(false); } catch (OperationCanceledException) { }
        foreach (var document in Documents) await document.DisposeAsync().ConfigureAwait(false); lifetime.Dispose();
    }
}
