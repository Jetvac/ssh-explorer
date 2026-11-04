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
    public async Task<int> ClearCompletedAsync(CancellationToken ct = default)
    {
        int count = 0;
        foreach (var document in Documents.Where(d => d.Record.State == RemoteEditState.Completed))
        {
            ct.ThrowIfCancellationRequested(); var directory = Path.GetFullPath(document.DirectoryPath);
            if (!directory.StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(directory) != document.Record.Id.ToString("N") || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { document.Record.WorkingName, "document.json", "snapshot.bin", "operation.bin" };
            var files = Directory.GetFiles(directory);
            if (Directory.EnumerateDirectories(directory).Any() || files.Any(f => !allowed.Contains(Path.GetFileName(f)) || (File.GetAttributes(f) & FileAttributes.ReparsePoint) != 0)) continue;
            await document.DisposeAsync().ConfigureAwait(false);
            foreach (var file in files.OrderBy(f => Path.GetFileName(f) == document.Record.WorkingName ? 0 : Path.GetFileName(f) == "document.json" ? 2 : 1)) File.Delete(file);
            Directory.Delete(directory, false); lock (documents) documents.Remove(document); count++;
        }
        OnChanged(this, EventArgs.Empty); return count;
    }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel(); try { await polling.ConfigureAwait(false); } catch (OperationCanceledException) { }
        foreach (var document in Documents) await document.DisposeAsync().ConfigureAwait(false); lifetime.Dispose();
    }
}
