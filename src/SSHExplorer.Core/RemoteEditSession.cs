using System.Security.Cryptography;
using System.Text.Json;

namespace SSHExplorer.Core;

public enum RemoteEditState { Preparing, Open, Pending, Uploading, Confirmed, Conflict, Error, Paused, Unknown, FormatConfirmation, Completed }
public sealed record DocumentOperation(Guid Id, string PreviousHash, string SnapshotHash, long Generation, bool FinalizationStarted, DocumentMetadata Metadata);
public sealed class RemoteEditRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public RemoteDocumentIdentity Identity { get; set; } = null!;
    public DocumentLocation Location { get; set; } = null!;
    public string ServerName { get; set; } = "";
    public string WorkingName { get; set; } = "document.txt";
    public string BaselineHash { get; set; } = "";
    public DocumentMetadata Metadata { get; set; } = null!;
    public DocumentTextFormat Format { get; set; } = null!;
    public string? ExplicitEncoding { get; set; }
    public string? PendingHash { get; set; }
    public long SnapshotLength { get; set; }
    public long Generation { get; set; }
    public DocumentOperation? Operation { get; set; }
    public RemoteEditState State { get; set; } = RemoteEditState.Preparing;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public string? Message { get; set; }
}

/// <summary>Durable byte-preserving document bridge. It never owns or terminates an editor process.</summary>
public sealed class RemoteEditSession : IAsyncDisposable
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    readonly SemaphoreSlim gate = new(1, 1);
    readonly CancellationTokenSource lifetime = new();
    CancellationTokenSource? connection;
    IRemoteDocumentTransport? transport;
    FileSystemWatcher? watcher;
    long changeSignal;
    bool autoSync, disposed;
    string? approvedFormatHash;
    public RemoteEditRecord Record { get; }
    public string DirectoryPath { get; }
    public string WorkingPath => Path.Combine(DirectoryPath, Record.WorkingName);
    public string SnapshotPath => Path.Combine(DirectoryPath, "snapshot.bin");
    public string OperationPath => Path.Combine(DirectoryPath, "operation.bin");
    public string ManifestPath => Path.Combine(DirectoryPath, "document.json");
    public Guid? AttachedSessionId => transport?.SessionId;
    public bool Connected => transport is not null && transport.Available && !transport.Lifetime.IsCancellationRequested && connection?.IsCancellationRequested == false;
    public bool IsBusy => Record.State is RemoteEditState.Preparing or RemoteEditState.Uploading;
    public string StatusText => Record.State switch { RemoteEditState.Preparing => "Подготовка", RemoteEditState.Open => "Открыта рабочая копия", RemoteEditState.Pending => "Ожидает отправки", RemoteEditState.Uploading => "Отправка и проверка", RemoteEditState.Confirmed => "Сохранено на сервере", RemoteEditState.Conflict => "Конфликт версий", RemoteEditState.Error => "Ошибка", RemoteEditState.Paused => "Приостановлено", RemoteEditState.Unknown => "Результат неизвестен", RemoteEditState.FormatConfirmation => "Изменён формат", _ => "Завершено" };
    public bool HasUnconfirmed => Record.PendingHash is not null || Record.Operation is not null || Record.State is RemoteEditState.Error or RemoteEditState.Conflict or RemoteEditState.Unknown or RemoteEditState.FormatConfirmation;
    public long BytesTransferred { get; private set; }
    public event EventHandler? Changed;
    RemoteEditSession(string directory, RemoteEditRecord record) { DirectoryPath = directory; Record = record; }
    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static async Task AtomicWriteAsync(string path, byte[] bytes, CancellationToken ct = default)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            { await output.WriteAsync(bytes, ct).ConfigureAwait(false); await output.FlushAsync(ct).ConfigureAwait(false); output.Flush(true); }
            ct.ThrowIfCancellationRequested(); File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    void Notify() { try { Changed?.Invoke(this, EventArgs.Empty); } catch { /* A UI observer cannot affect document persistence. */ } }
    async Task Persist(CancellationToken ct = default)
    {
        Record.UpdatedUtc = DateTime.UtcNow;
        await AtomicWriteAsync(ManifestPath, JsonSerializer.SerializeToUtf8Bytes(Record, Json), ct).ConfigureAwait(false); Notify();
    }
    public static async Task<RemoteEditSession> OpenAsync(string root, IRemoteDocumentTransport transport, string path, string serverName, bool followLink, string? explicitEncoding = null, CancellationToken ct = default)
    {
        var location = await transport.LocateAsync(path, followLink, ct).ConfigureAwait(false);
        var version = await transport.ReadAsync(location, ct).ConfigureAwait(false);
        var format = await Task.Run(() => DocumentText.Decode(version.Bytes, explicitEncoding).Format, ct).ConfigureAwait(false);
        var extension = Path.GetExtension(PosixPath.Name(location.Path));
        if (extension.Length is < 2 or > 16 || extension.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '.')) extension = ".txt";
        static string SafeLabel(string value) => new string(value.Select(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c) ? '_' : c).Take(42).ToArray()).Trim(' ', '.');
        var basename = SafeLabel(PosixPath.Name(location.Path)); if (basename.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) basename = basename[..^extension.Length];
        var localName = "document-" + basename + "-" + SafeLabel(serverName) + extension;
        var record = new RemoteEditRecord { Identity = transport.Identity, Location = location, ServerName = serverName, WorkingName = localName, BaselineHash = version.Hash, Metadata = version.Metadata, Format = format, ExplicitEncoding = explicitEncoding, State = RemoteEditState.Open };
        var directory = Path.Combine(root, record.Id.ToString("N")); Directory.CreateDirectory(directory);
        var result = new RemoteEditSession(directory, record);
        await AtomicWriteAsync(result.WorkingPath, version.Bytes, ct).ConfigureAwait(false);
        await AtomicWriteAsync(result.SnapshotPath, version.Bytes, ct).ConfigureAwait(false);
        record.SnapshotLength = version.Bytes.LongLength;
        await result.Persist(ct).ConfigureAwait(false); result.Bind(transport, true); result.Watch(); return result;
    }
    public static RemoteEditSession Restore(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 || !Guid.TryParseExact(Path.GetFileName(directory), "N", out var id)) throw new IOException("Неверная папка edit session.");
        var manifest = new FileInfo(Path.Combine(directory, "document.json"));
        if (manifest.Length > 128 * 1024 || (manifest.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Недопустимый файл журнала.");
        var record = JsonSerializer.Deserialize<RemoteEditRecord>(File.ReadAllBytes(manifest.FullName), Json) ?? throw new IOException("Пустой журнал.");
        if (record.Id != id || record.WorkingName != Path.GetFileName(record.WorkingName) || !(record.WorkingName.StartsWith("document.", StringComparison.Ordinal) || record.WorkingName.StartsWith("document-", StringComparison.Ordinal)) || record.Identity is null || record.Location is null || record.Metadata is null || record.Format is null) throw new IOException("Повреждённый журнал документа.");
        var result = new RemoteEditSession(directory, record);
        if (record.State != RemoteEditState.Completed) { record.State = record.Operation is null ? RemoteEditState.Paused : RemoteEditState.Unknown; record.Message = "Восстановленная рабочая копия. Подключитесь к исходному серверу и возобновите явно."; result.Watch(); }
        return result;
    }
    void Bind(IRemoteDocumentTransport value, bool automatic)
    {
        if (value.Identity != Record.Identity) throw new IOException("Сервер, пользователь или SSH host key не соответствует документу.");
        connection?.Dispose(); transport = value; connection = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, value.Lifetime); autoSync = automatic;
    }
    void Watch()
    {
        watcher = new(DirectoryPath) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size, EnableRaisingEvents = true };
        void Signal(object? sender, FileSystemEventArgs e) { if (Path.GetFileName(e.FullPath) == Record.WorkingName || e is RenamedEventArgs rename && Path.GetFileName(rename.OldFullPath) == Record.WorkingName) SignalChange(); }
        watcher.Changed += Signal; watcher.Created += Signal; watcher.Deleted += Signal; watcher.Renamed += Signal; watcher.Error += (_, _) => SignalChange();
    }
    void SignalChange()
    {
        var signal = Interlocked.Increment(ref changeSignal);
        _ = Task.Run(async () => { try { await Task.Delay(500, lifetime.Token).ConfigureAwait(false); if (signal == Volatile.Read(ref changeSignal)) await CheckAsync(autoSync, lifetime.Token).ConfigureAwait(false); } catch (OperationCanceledException) { } catch { /* CheckAsync persists recoverable errors; watcher is only a hint. */ } });
    }
    async Task<byte[]> StableRead(CancellationToken ct)
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                byte[] Read()
                {
                    var info = new FileInfo(WorkingPath);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length > DocumentText.MaximumBytes) throw new IOException("Рабочая копия должна быть обычным файлом не больше 32 МиБ.");
                    using var input = new FileStream(WorkingPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                    using var output = new MemoryStream(); byte[] buffer = new byte[65536]; int count;
                    while ((count = input.Read(buffer)) != 0) { ct.ThrowIfCancellationRequested(); if (output.Length + count > DocumentText.MaximumBytes) throw new IOException("Рабочая копия больше 32 МиБ."); output.Write(buffer, 0, count); } return output.ToArray();
                }
                var before = await Task.Run(Read, ct).ConfigureAwait(false); await Task.Delay(180, ct).ConfigureAwait(false); var after = await Task.Run(Read, ct).ConfigureAwait(false);
                if (Hash(before) == Hash(after)) return after;
            }
            catch (IOException) when (attempt < 5) { }
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        throw new IOException("Файл ещё записывается, заблокирован или отсутствует. Серверная версия сохранена; повторите проверку.");
    }
    public async Task CheckAsync(bool send = false, CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        await gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (Record.State == RemoteEditState.Completed) return;
            var bytes = await StableRead(linked.Token).ConfigureAwait(false); var hash = Hash(bytes);
            if (hash == Record.BaselineHash && Record.Operation is null) { Record.PendingHash = null; Record.State = Connected ? RemoteEditState.Confirmed : RemoteEditState.Paused; Record.Message = "Локальные байты совпадают с подтверждённой версией."; await Persist().ConfigureAwait(false); return; }
            var blocked = Record.State is RemoteEditState.Conflict or RemoteEditState.Unknown or RemoteEditState.FormatConfirmation or RemoteEditState.Error;
            if (Record.PendingHash != hash) { await AtomicWriteAsync(SnapshotPath, bytes, linked.Token).ConfigureAwait(false); Record.PendingHash = hash; Record.Generation++; }
            Record.SnapshotLength = bytes.LongLength;
            if (!blocked) { Record.State = Connected ? RemoteEditState.Pending : RemoteEditState.Paused; Record.Message = "Сохранённая локальная версия ожидает отправки."; }
            await Persist().ConfigureAwait(false);
            if (send && !blocked && Connected) await SendSnapshot(null, null, linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Record.State = RemoteEditState.Error; Record.Message = ErrorMessages.Describe(ex); await Persist().ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    public Task ReconcileAsync(CancellationToken ct = default) => CheckAsync(autoSync, ct);
    public async Task AttachForReviewAsync(IRemoteDocumentTransport value, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false); try { Bind(value, false); } finally { gate.Release(); }
    }
    public async Task<RemoteDocumentVersion> ReviewServerAsync(IRemoteDocumentTransport value, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { Bind(value, false); return await value.ReadAsync(Record.Location, ct).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    async Task SendSnapshot(string? expectedHash, DocumentMetadata? metadata, CancellationToken caller)
    {
        if (!Connected || Record.PendingHash is null) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, connection!.Token); var ct = linked.Token;
        var bytes = await File.ReadAllBytesAsync(SnapshotPath, ct).ConfigureAwait(false);
        if (Hash(bytes) != Record.PendingHash) throw new IOException("Не совпал хеш локального snapshot. Рабочая копия сохранена.");
        var format = await Task.Run(() => DocumentText.InspectSaved(bytes, Record.ExplicitEncoding).Format, ct).ConfigureAwait(false);
        if (Record.Format.NeedsConfirmation(format) && approvedFormatHash != Record.PendingHash)
        { Record.State = RemoteEditState.FormatConfirmation; Record.Message = $"Редактор изменил формат: {Record.Format.Description} → {format.Description}. Подтвердите отправку этой версии."; await Persist().ConfigureAwait(false); return; }
        var operation = new DocumentOperation(Guid.NewGuid(), expectedHash ?? Record.BaselineHash, Record.PendingHash, Record.Generation, false, metadata ?? Record.Metadata);
        await AtomicWriteAsync(OperationPath, bytes, ct).ConfigureAwait(false);
        Record.Operation = operation; Record.State = RemoteEditState.Uploading; Record.Message = "Отправка и проверка сохранённой версии…"; BytesTransferred = 0; await Persist(ct).ConfigureAwait(false);
        try
        {
            var progressClock = System.Diagnostics.Stopwatch.StartNew();
            var result = await transport!.ReplaceAsync(Record.Location, operation.PreviousHash, metadata ?? Record.Metadata, bytes, operation.Id,
                async () => { Record.Operation = operation with { FinalizationStarted = true }; await Persist().ConfigureAwait(false); },
                new ByteProgress(n => { BytesTransferred = n; if (progressClock.ElapsedMilliseconds >= 150 || n == bytes.LongLength) { progressClock.Restart(); Notify(); } }), ct).ConfigureAwait(false);
            if (result.Hash != operation.SnapshotHash) throw new IOException("Сервер не подтвердил отправленный хеш.");
            Record.BaselineHash = result.Hash; Record.Metadata = result.Metadata; Record.Format = format; Record.PendingHash = null; Record.Operation = null; approvedFormatHash = null;
            if (Record.ExplicitEncoding is not null) Record.ExplicitEncoding = format.Encoding;
            Record.State = RemoteEditState.Confirmed; Record.Message = "На сервере подтверждена версия " + result.Hash[..12] + "."; await Persist().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            autoSync = false;
            Record.State = Record.Operation?.FinalizationStarted == true ? RemoteEditState.Unknown : ex is DocumentConflictException ? RemoteEditState.Conflict : ex is OperationCanceledException ? RemoteEditState.Paused : RemoteEditState.Error;
            Record.Message = Record.State == RemoteEditState.Unknown ? "Результат замены неизвестен. При восстановлении сначала проверим сервер; слепого повтора нет." : ErrorMessages.Describe(ex);
            if (Record.Operation?.FinalizationStarted != true) Record.Operation = null;
            await Persist().ConfigureAwait(false);
        }
    }
    public async Task ResumeAsync(IRemoteDocumentTransport value, bool approveFormat = false, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Bind(value, false); var remote = await value.ReadAsync(Record.Location, ct).ConfigureAwait(false);
            if (Record.Operation is { } operation && remote.Hash == operation.SnapshotHash)
            {
                var snapshot = await File.ReadAllBytesAsync(OperationPath, ct).ConfigureAwait(false);
                if (Hash(snapshot) != operation.SnapshotHash) throw new IOException("Повреждён сохранённый snapshot операции.");
                if (remote.Metadata.Mode != operation.Metadata.Mode || remote.Metadata.UserId != operation.Metadata.UserId || remote.Metadata.GroupId != operation.Metadata.GroupId) throw new DocumentConflictException("Содержимое отправлено, но метаданные не соответствуют ожидаемым. Проверьте сервер.");
                Record.BaselineHash = remote.Hash; Record.Metadata = remote.Metadata; Record.Format = DocumentText.InspectSaved(snapshot, Record.ExplicitEncoding).Format; Record.Operation = null; Record.PendingHash = null;
                if (Record.ExplicitEncoding is not null) Record.ExplicitEncoding = Record.Format.Encoding;
            }
            else if (remote.Hash != (Record.Operation?.PreviousHash ?? Record.BaselineHash)) { Record.State = RemoteEditState.Conflict; Record.Message = "На сервере другая версия. Выберите решение конфликта."; await Persist().ConfigureAwait(false); return; }
            else Record.Operation = null;
            if (approveFormat) approvedFormatHash = Record.PendingHash;
            Record.State = RemoteEditState.Open; autoSync = true; await Persist().ConfigureAwait(false);
        }
        finally { gate.Release(); }
        await CheckAsync(true, ct).ConfigureAwait(false);
    }
    public async Task ReplaceApprovedVersionAsync(RemoteDocumentVersion approved, CancellationToken ct = default)
    {
        await CheckAsync(false, ct).ConfigureAwait(false); await gate.WaitAsync(ct).ConfigureAwait(false);
        try { if (!Connected) throw new IOException("Подключитесь к исходному серверу."); await SendSnapshot(approved.Hash, approved.Metadata, ct).ConfigureAwait(false); autoSync = Record.State == RemoteEditState.Confirmed; }
        finally { gate.Release(); }
    }
    public Task<RemoteDocumentVersion> ReadServerAsync(CancellationToken ct = default) => Connected ? transport!.ReadAsync(Record.Location, ct) : throw new IOException("Подключитесь к исходному серверу.");
    public async Task SaveRemoteCopyAsync(string path, CancellationToken ct = default)
    {
        await CheckAsync(false, ct).ConfigureAwait(false); await gate.WaitAsync(ct).ConfigureAwait(false);
        try { if (!Connected) throw new IOException("Подключитесь к исходному серверу."); var bytes = await StableRead(ct).ConfigureAwait(false); await transport!.SaveCopyAsync(path, bytes, Record.Metadata.Mode, ct).ConfigureAwait(false); Record.Message = "Отдельная копия проверена: " + path + ". Исходный документ не заменён."; await Persist().ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    public async Task UseLocalCopyAsync(string source, CancellationToken ct = default)
    {
        autoSync = false;
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { var info = new FileInfo(source); if (info.Length > DocumentText.MaximumBytes || (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Выберите обычный файл до 32 МиБ."); var bytes = await File.ReadAllBytesAsync(source, ct).ConfigureAwait(false); DocumentText.Decode(bytes, Record.ExplicitEncoding); await AtomicWriteAsync(WorkingPath, bytes, ct).ConfigureAwait(false); }
        finally { gate.Release(); }
        await CheckAsync(false, ct).ConfigureAwait(false);
    }
    public void CancelSending() { autoSync = false; connection?.Cancel(); }
    public async Task<byte[]> ReadWorkingSnapshotAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await StableRead(ct).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    public async Task DetachAsync()
    {
        CancelSending(); await gate.WaitAsync().ConfigureAwait(false);
        try { transport = null; connection?.Dispose(); connection = null; if (Record.State != RemoteEditState.Completed) { if (Record.State != RemoteEditState.Unknown) Record.State = RemoteEditState.Paused; Record.Message = "Отключено. Рабочая копия и snapshot сохранены; внешний редактор остаётся открыт."; await Persist().ConfigureAwait(false); } }
        finally { gate.Release(); }
    }
    public async Task CompleteAsync(bool keepUnconfirmed, CancellationToken ct = default)
    {
        await CheckAsync(false, ct).ConfigureAwait(false); await gate.WaitAsync(ct).ConfigureAwait(false);
        try { if (HasUnconfirmed && !keepUnconfirmed) throw new IOException("Есть неподтверждённые изменения. Сначала отправьте или сохраните отдельную локальную копию."); autoSync = false; Record.State = RemoteEditState.Completed; Record.Message = "Редактирование завершено. Локальная копия сохранена; удаляется только явной очисткой завершённых документов."; watcher?.Dispose(); watcher = null; await Persist().ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    public async ValueTask DisposeAsync() { if (disposed) return; disposed = true; watcher?.Dispose(); lifetime.Cancel(); await DetachAsync().ConfigureAwait(false); lifetime.Dispose(); }
    sealed class ByteProgress(Action<long> report) : IProgress<long> { public void Report(long value) => report(value); }
}
