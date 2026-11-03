using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace SSHExplorer.Core;

public sealed partial class TransferQueue
{
    /// <summary>The Windows Shell owns the target and reads streams; this handle owns source progress/cancellation.</summary>
    public ExternalTransferOperation BeginExternalDownload(ServerSession source, IEnumerable<string> paths)
    {
        var names = paths.ToArray();
        var job = new TransferJob { SessionId = source.Id, Sessions = [source], Name = "В Windows: " + (names.Length == 1 ? PosixPath.Name(names[0]) : $"{names.Length} объектов"), Source = string.Join("; ", names), Destination = "Выбранная папка Windows Explorer" };
        var operation = new ExternalTransferOperation(job, source.Lifetime);
        job.Operation = async ct => { operation.MarkRunning(); await operation.Completion.WaitAsync(ct).ConfigureAwait(false); };
        Enqueue(job);
        // A native Shell extraction cannot be silently replayed: its target exists only in the target's operation.
        job.RetryAction = () => throw new InvalidOperationException("Повторите перетаскивание в выбранную папку Windows. Автоматическое повторение исходящего OLE-копирования недоступно.");
        return operation;
    }
}

public sealed class ExternalTransferOperation
{
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource running = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource cancellation;
    private readonly CancellationToken token;
    private readonly CancellationTokenRegistration cancellationRegistration;
    internal Task Completion => completion.Task;
    public TransferJob Job { get; }
    public CancellationToken Cancellation => token;
    internal void MarkRunning() => running.TrySetResult();
    public Task WaitUntilRunningAsync(CancellationToken ct) => running.Task.WaitAsync(ct);
    internal ExternalTransferOperation(TransferJob job, CancellationToken sessionLifetime)
    {
        Job = job; cancellation = CancellationTokenSource.CreateLinkedTokenSource(job.Cancellation, sessionLifetime);
        token = cancellation.Token;
        cancellationRegistration = token.Register(() => completion.TrySetCanceled(token));
        // Every terminal state unlinks source lifetime and callbacks, including failed/cancelled Shell targets.
        _ = completion.Task.ContinueWith(_ => { cancellationRegistration.Dispose(); cancellation.Dispose(); },
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }
    public void SetTotal(long bytes) { Job.TotalBytes = bytes; Job.Notify(); }
    public void SetSkipped(int count) { Job.SkippedItems = count; Job.Notify(); }
    public void AddBytes(int bytes) { Cancellation.ThrowIfCancellationRequested(); Job.AddBytes(bytes); }
    public void Complete(int completed, int skipped = 0)
    { Job.CompletedItems = completed; Job.SkippedItems = skipped; completion.TrySetResult(); }
    public void Fail(Exception error) => completion.TrySetException(error);
    public void Cancel() => Job.Cancel();
}

public sealed partial class ServerSession
{
    public async Task<RemoteExportSource> OpenExportSourceAsync(CancellationToken ct = default)
        => new(await LeaseSftpAsync(ct).ConfigureAwait(false), Lifetime, ct);
}

/// <summary>One session-owned connection per native extraction. All streams share its serialized transport.</summary>
public sealed class RemoteExportSource : IDisposable
{
    private readonly ServerSession.SftpLease lease;
    private readonly object gate = new();
    private readonly CancellationTokenSource cancellation;
    private readonly List<ExportStream> streams = [];
    private int disposed;
    internal RemoteExportSource(ServerSession.SftpLease lease, CancellationToken owner, CancellationToken caller)
    {
        this.lease = lease; cancellation = CancellationTokenSource.CreateLinkedTokenSource(owner, caller);
        cancellation.Token.Register(() => ThreadPool.QueueUserWorkItem(_ => Dispose()));
    }
    public Stream OpenRead(RemoteEntry expected)
    {
        lock (gate)
        {
            Check(); if (!expected.IsRegularFile || expected.IsSymbolicLink) throw new IOException("В Windows передаются только обычные файлы; для ссылки требуется явный выбор её цели.");
            var stream = new ExportStream(this, expected); streams.Add(stream); return stream;
        }
    }
    private void Check() { cancellation.Token.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this); }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        // Dispose the transport before taking gate: this interrupts a blocked Read without waiting on its lock.
        lease.Dispose();
        lock (gate) { foreach (var stream in streams) stream.CloseFile(); streams.Clear(); }
        cancellation.Dispose();
    }
    private sealed class ExportStream(RemoteExportSource owner, RemoteEntry expected) : Stream
    {
        private SftpFileStream? input;
        private long position;
        private bool closed;
        public override bool CanRead => !closed; public override bool CanSeek => !closed; public override bool CanWrite => false;
        public override long Length => expected.Length;
        public override long Position { get => position; set => Seek(value, SeekOrigin.Begin); }
        private void EnsureOpen()
        {
            owner.Check(); if (closed) throw new ObjectDisposedException(nameof(ExportStream));
            if (input is not null) return;
            var now = RemoteFileService.Map(RemoteFileService.Find(owner.lease.Client, expected.FullPath));
            if (!now.IsRegularFile || now.IsSymbolicLink || now.Length != expected.Length || now.LastWriteTimeUtc != expected.LastWriteTimeUtc)
                throw new IOException("Удалённый файл изменился после начала перетаскивания: " + expected.FullPath);
            input = owner.lease.Client.OpenRead(expected.FullPath); input.Position = position;
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count == 0) return 0;
            lock (owner.gate)
            {
                EnsureOpen();
                // SSH.NET's synchronous Read explicitly uses CancellationToken.None. Keep COM synchronous
                // on its MTA worker while the actual SFTP request remains cancellation-aware.
                var readLimit = Length == 0 ? Math.Min(count, 1) : (int)Math.Min(count, Math.Max(0, Length - position));
                var read = input!.ReadAsync(buffer.AsMemory(offset, readLimit), owner.cancellation.Token).AsTask().GetAwaiter().GetResult();
                if (Length == 0 && read != 0) throw new IOException("Источник сообщает нулевой размер, но содержит данные. Пустой файл не будет выдан за успешную копию.");
                position += read; owner.Check();
                if (position >= Length || read == 0)
                {
                    if (position != Length) throw new IOException("Удалённый файл сократился во время передачи.");
                    var now = RemoteFileService.Map(RemoteFileService.Find(owner.lease.Client, expected.FullPath));
                    if (now.Length != expected.Length || now.LastWriteTimeUtc != expected.LastWriteTimeUtc || now.IsSymbolicLink || !now.IsRegularFile)
                        throw new IOException("Удалённый файл изменился во время передачи: " + expected.FullPath);
                    CloseFile();
                }
                return read;
            }
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            lock (owner.gate)
            {
                owner.Check(); var target = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => checked(position + offset), SeekOrigin.End => checked(Length + offset), _ => throw new ArgumentOutOfRangeException(nameof(origin)) };
                if (target < 0 || target > Length) throw new IOException("Некорректная позиция удалённого потока.");
                position = target; if (input is not null) input.Position = target; return position;
            }
        }
        internal void CloseFile() { try { input?.Dispose(); } catch { } input = null; }
        protected override void Dispose(bool disposing) { if (disposing) lock (owner.gate) { closed = true; CloseFile(); } base.Dispose(disposing); }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
