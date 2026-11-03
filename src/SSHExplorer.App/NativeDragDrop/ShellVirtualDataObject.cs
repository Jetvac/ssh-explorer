using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using SSHExplorer.Core;

namespace SSHExplorer.App.NativeDragDrop;

/// <summary>Native virtual-file source. No file content is read before the Shell requests FILECONTENTS.</summary>
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class ShellVirtualDataObject : System.Runtime.InteropServices.ComTypes.IDataObject, IDataObjectAsyncCapability, ICustomQueryInterface
{
    public sealed record VirtualEntry(string Name, RemoteEntry? Remote, bool Directory, byte[]? Inline = null)
    { public long Length => Directory ? 0 : Inline?.LongLength ?? Remote?.Length ?? 0; }
    private readonly ServerSession session;
    private readonly RemoteEntry[] selected;
    private readonly string token;
    private readonly TransferQueue queue;
    private readonly CancellationTokenSource cancellation;
    private readonly ManualResetEventSlim hostStopped;
    private readonly object gate = new();
    private readonly List<ShellFileStream> streams = [];
    private readonly HashSet<int> fullyRead = [];
    private Task<VirtualEntry[]>? manifest;
    private Task<RemoteExportSource>? reader;
    private ExternalTransferOperation? operation;
    private bool asyncMode = true, inOperation, dragReturned, ended, faulted;
    private IntPtr marshaler;
    private int readersClosed;
    private int manifestSkipped;
    private static readonly ConcurrentDictionary<Guid, ShellVirtualDataObject> active = new();
    private static int hosts;
    internal static int ActiveCount => active.Count;
    internal static int MtaHostCount => Volatile.Read(ref hosts);
    private readonly Guid id = Guid.NewGuid();
    public Guid SourceSessionId => session.Id;
    public bool IsExtracting { get { lock (gate) return inOperation; } }
    public bool IsCancelled => cancellation.IsCancellationRequested;
    public int GetDataThreadId { get; private set; }
    public int ReadThreadId { get; private set; }
    public ApartmentState GetDataApartment { get; private set; }
    public ApartmentState ReadApartment { get; private set; }
    public string? Failure { get; private set; }
    public TransferJob? Job => operation?.Job;

    private ShellVirtualDataObject(ServerSession session, RemoteEntry[] selected, string token,
        TransferQueue queue, ManualResetEventSlim stop)
    {
        this.session = session; this.selected = selected; this.token = token; this.queue = queue; hostStopped = stop;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(session.Lifetime);
        cancellation.Token.Register(() =>
        {
            if (!faulted) operation?.Cancel();
            // Keep the COM apartment alive until the target receives E_ABORT and sends EndOperation.
            // Uninitializing the last MTA while an out-of-process Read is in flight can strand that RPC.
            lock (gate) if (!inOperation) { active.TryRemove(id, out _); hostStopped.Set(); }
            _ = Task.Run(CloseReaders);
            // A crashed/non-cooperating target may never call EndOperation. Retain its apartment only
            // for a short cancellation grace period; all SSH transports are already closed above.
            _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(completed =>
            { lock (gate) if (inOperation && !ended) { active.TryRemove(id, out _); hostStopped.Set(); } }, TaskScheduler.Default);
        });
        var outer = Marshal.GetIUnknownForObject(this);
        try { Marshal.ThrowExceptionForHR(ShellInterop.CoCreateFreeThreadedMarshaler(outer, out marshaler)); }
        finally { Marshal.Release(outer); }
        // A file-only selection already has complete descriptors; metadata queries never need a UI wait.
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (selected.All(e => e.IsRegularFile && !e.IsSymbolicLink && e.Name.Length < 260 && WindowsNames.SafeName(e.Name) == e.Name && names.Add(e.Name)))
            manifest = Task.FromResult(selected.Select(e => new VirtualEntry(e.Name, e, false)).ToArray());
    }

    public static Task<ShellVirtualDataObject> CreateAsync(ServerSession source, IReadOnlyList<RemoteEntry> selected,
        string opaqueToken, TransferQueue queue)
    {
        if (selected.Count == 0) throw new ArgumentException("Не выбраны объекты.");
        var ready = new TaskCompletionSource<ShellVirtualDataObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stop = new ManualResetEventSlim();
        var snapshot = selected.Select(Clone).ToArray();
        if (snapshot.Any(e => e.IsRegularFile && !e.IsSymbolicLink && e.Length < 0))
            throw new IOException("Сервер не предоставил достоверный размер обычного файла. Перетаскивание в Windows остановлено.");
        var host = new Thread(() =>
        {
            Interlocked.Increment(ref hosts);
            var hr = ShellInterop.CoInitializeEx(IntPtr.Zero, 0);
            try
            {
                Marshal.ThrowExceptionForHR(hr);
                var data = new ShellVirtualDataObject(source, snapshot, opaqueToken, queue, stop);
                ready.SetResult(data); stop.Wait(); GC.KeepAlive(data);
            }
            catch (Exception e) { ready.TrySetException(e); }
            finally { if (hr >= 0) ShellInterop.CoUninitialize(); Interlocked.Decrement(ref hosts); }
        }) { IsBackground = true, Name = "SSH Explorer OLE source" };
        host.SetApartmentState(ApartmentState.MTA); host.Start(); return ready.Task;
    }

    private static RemoteEntry Clone(RemoteEntry e) => new() { Name = e.Name, FullPath = e.FullPath, IsDirectory = e.IsDirectory, IsRegularFile = e.IsRegularFile, IsSymbolicLink = e.IsSymbolicLink, Length = e.Length, LastWriteTimeUtc = e.LastWriteTimeUtc, LastAccessTimeUtc = e.LastAccessTimeUtc, Permissions = e.Permissions, UserId = e.UserId, GroupId = e.GroupId };

    private ExternalTransferOperation EnsureOperation()
    {
        lock (gate)
        {
            if (operation is not null) return operation;
            cancellation.Token.ThrowIfCancellationRequested();
            operation = queue.BeginExternalDownload(session, selected.Select(x => x.FullPath));
            if (manifest?.IsCompletedSuccessfully == true) operation.SetTotal(manifest.Result.Where(e => !e.Directory).Sum(e => e.Length));
            operation.Cancellation.Register(() => { if (!cancellation.IsCancellationRequested) cancellation.Cancel(); });
            return operation;
        }
    }

    private Task<VirtualEntry[]> ManifestTask()
    {
        lock (gate) return manifest ??= Task.Run(BuildManifestAsync, cancellation.Token);
    }
    private async Task<VirtualEntry[]> BuildManifestAsync()
    {
        var output = new List<VirtualEntry>(); var mappings = new List<object>();
        var topNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var task = EnsureOperation();
        async Task Visit(RemoteEntry entry, string name, int depth, string? originalRelative = null)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            if (depth > 128 || output.Count >= 100000) throw new IOException("Превышен предел дерева перетаскивания (128 уровней / 100000 объектов).");
            if (name.Length >= 260) throw new IOException("Относительный путь превышает ограничение Windows Shell FILEDESCRIPTORW (259 символов): " + name);
            if (entry.IsSymbolicLink || (!entry.IsDirectory && !entry.IsRegularFile))
                throw new IOException("Символические ссылки и специальные объекты не передаются в Windows без явного выбора цели: " + entry.FullPath);
            if (entry.IsRegularFile && entry.Length < 0) throw new IOException("Сервер не предоставил достоверный размер файла: " + entry.FullPath);
            output.Add(new VirtualEntry(name, entry, entry.IsDirectory));
            var original = originalRelative ?? entry.Name;
            if (name != original) mappings.Add(new { Remote = entry.FullPath, Local = name });
            if (!entry.IsDirectory) return;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var child in await session.Files.ListAsync(entry.FullPath, cancellation.Token).ConfigureAwait(false))
            {
                var safe = await SafeName(child, names).ConfigureAwait(false);
                if (safe is null) { manifestSkipped++; continue; }
                await Visit(child, name + "\\" + safe, depth + 1, original + "\\" + child.Name).ConfigureAwait(false);
            }
        }
        async Task<string?> SafeName(RemoteEntry entry, HashSet<string> used)
        {
            var name = WindowsNames.SafeName(entry.Name); var proposed = name;
            var n = 2; while (!used.Add(proposed)) proposed = name + $" ({n++})";
            if (proposed != entry.Name)
            {
                var conflict = new TransferConflict(entry.FullPath, proposed, entry.IsDirectory, entry.IsDirectory, entry.IsRegularFile ? entry.Length : null, null,
                    "Имя Linux несовместимо с Windows или конфликтует без учёта регистра. «Сохранить оба» передаст безопасное имя и файл соответствия.", task.Job.Id, task.Job.Attempt);
                var decision = queue.ResolveConflict is null ? ConflictDecision.Cancel : await queue.ResolveConflict(conflict).WaitAsync(cancellation.Token).ConfigureAwait(false);
                if (decision == ConflictDecision.Skip) { used.Remove(proposed); return null; }
                if (decision != ConflictDecision.KeepBoth) throw new OperationCanceledException("Преобразование имени для Windows не подтверждено.");
            }
            return proposed;
        }
        foreach (var entry in selected)
        {
            var safe = await SafeName(entry, topNames).ConfigureAwait(false);
            if (safe is null) { manifestSkipped++; continue; }
            await Visit(entry, safe, 0).ConfigureAwait(false);
        }
        if (mappings.Count > 0)
        {
            var mappingName = ".sshexplorer-name-map.jsonl"; while (!topNames.Add(mappingName)) mappingName = "_" + mappingName;
            output.Add(new VirtualEntry(mappingName, null, false, Encoding.UTF8.GetBytes(string.Join('\n', mappings.Select(x => System.Text.Json.JsonSerializer.Serialize(x))) + "\n")));
        }
        task.SetTotal(output.Where(x => !x.Directory).Sum(x => x.Length)); task.SetSkipped(manifestSkipped); return output.ToArray();
    }

    private VirtualEntry[] GetManifest()
    {
        var task = ManifestTask();
        if (!task.IsCompleted && Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            throw new COMException("Метаданные дерева подготавливаются в фоне; повторите запрос после подготовки.", unchecked((int)0x8000000A));
        return task.GetAwaiter().GetResult();
    }
    internal Stream OpenStream(VirtualEntry entry)
    {
        cancellation.Token.ThrowIfCancellationRequested();
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            throw new COMException("Сетевое чтение OLE не выполняется в STA интерфейса.", ShellInterop.WrongThread);
        ReadThreadId = Environment.CurrentManagedThreadId;
        ReadApartment = Thread.CurrentThread.GetApartmentState();
        EnsureOperation().WaitUntilRunningAsync(cancellation.Token).GetAwaiter().GetResult();
        if (entry.Directory) return new MemoryStream([]);
        if (entry.Inline is not null) return new MemoryStream(entry.Inline, false);
        Task<RemoteExportSource> pending;
        lock (gate) pending = reader ??= session.OpenExportSourceAsync(cancellation.Token);
        return pending.GetAwaiter().GetResult().OpenRead(entry.Remote!);
    }
    internal void BytesRead(int index, long position, int bytes, long length)
    {
        EnsureOperation().AddBytes(bytes);
        if (position == length) lock (gate) fullyRead.Add(index);
    }
    internal void Failed(Exception error)
    {
        NativeTrace.Write("Failed " + error.GetType().Name);
        Failure = ErrorMessages.Describe(error);
        if (error is OperationCanceledException || session.Lifetime.IsCancellationRequested) { Cancel(); return; }
        faulted = true; operation?.Fail(error); cancellation.Cancel();
    }

    public void OleDragReturned(bool accepted)
    {
        lock (gate) dragReturned = true;
        if (!accepted) { Cancel(); return; }
        lock (gate)
        {
            if (!inOperation)
            {
                if (!ended && operation is not null)
                {
                    // IDataObjectAsyncCapability is optional. A synchronous target has finished
                    // extraction before Drop/DoDragDrop returns, so its accepted effect is final.
                    ended = true;
                    if (!faulted && !cancellation.IsCancellationRequested)
                    {
                        var entries = manifest?.IsCompletedSuccessfully == true ? manifest.Result : [];
                        operation.Complete(fullyRead.Count, manifestSkipped + entries.Count(e => !e.Directory) - fullyRead.Count);
                    }
                }
                hostStopped.Set(); _ = Task.Run(CloseReaders);
            }
        }
    }
    public void Cancel()
    {
        NativeTrace.Write("Cancel enter");
        if (!cancellation.IsCancellationRequested) cancellation.Cancel(); operation?.Cancel();
        lock (gate) if (!inOperation) { active.TryRemove(id, out _); hostStopped.Set(); }
        NativeTrace.Write("Cancel exit");
    }
    private void CloseReaders()
    {
        NativeTrace.Write("CloseReaders enter");
        if (Interlocked.Exchange(ref readersClosed, 1) != 0) return;
        ShellFileStream[] open; Task<RemoteExportSource>? source;
        lock (gate) { open = streams.ToArray(); streams.Clear(); source = reader; }
        if (source is not null) { try { NativeTrace.Write("Source dispose start"); source.GetAwaiter().GetResult().Dispose(); NativeTrace.Write("Source dispose done"); } catch { } }
        NativeTrace.Write("Streams close start");
        foreach (var stream in open) stream.Close();
        NativeTrace.Write("Streams close done");
        cancellation.Dispose();
        NativeTrace.Write("CloseReaders exit");
    }

    public void GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        medium = default; GetDataThreadId = Environment.CurrentManagedThreadId; GetDataApartment = Thread.CurrentThread.GetApartmentState();
        var hr = QueryGetData(ref format); if (hr != 0) throw new COMException("Формат OLE недоступен.", hr);
        try
        {
            if (format.cfFormat == ShellInterop.Token) medium = ShellInterop.Global(Encoding.UTF8.GetBytes(token));
            else if (format.cfFormat == ShellInterop.Preferred) medium = ShellInterop.Global(BitConverter.GetBytes(1u));
            else if (format.cfFormat == ShellInterop.InDragLoop) medium = ShellInterop.Global(BitConverter.GetBytes(dragReturned ? 0u : 1u));
            else if (format.cfFormat == ShellInterop.Descriptor) medium = ShellInterop.Global(DescriptorBytes(GetManifest()));
            else if (format.cfFormat == ShellInterop.Contents)
            {
                var entries = GetManifest(); if (format.lindex < 0 || format.lindex >= entries.Length) throw new COMException("Некорректный индекс файла.", ShellInterop.IndexError);
                var stream = new ShellFileStream(this, entries[format.lindex], format.lindex);
                lock (gate) streams.Add(stream);
                medium = new STGMEDIUM { tymed = TYMED.TYMED_ISTREAM, unionmember = Marshal.GetComInterfaceForObject(stream, typeof(INativeStream)), pUnkForRelease = null };
            }
        }
        catch (COMException e) when (e.HResult is unchecked((int)0x8000000A) or ShellInterop.IndexError or ShellInterop.FormatError or ShellInterop.MediumError) { throw; }
        catch (Exception e) { Failed(e); throw; }
    }
    public static byte[] DescriptorBytes(IReadOnlyList<VirtualEntry> entries)
    {
        var result = new byte[checked(4 + entries.Count * 592)]; BitConverter.GetBytes(entries.Count).CopyTo(result, 0);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i]; var offset = 4 + i * 592;
            if (!entry.Directory && entry.Length < 0) throw new IOException("Нельзя передать отрицательный или неизвестный размер файла в Windows Shell.");
            var hasTime = entry.Remote is not null && entry.Remote.LastWriteTimeUtc.Year >= 1601;
            var flags = 0x80004004u | (entry.Directory ? 0u : 0x40u) | (hasTime ? 0x20u : 0u);
            BitConverter.GetBytes(flags).CopyTo(result, offset);
            BitConverter.GetBytes(entry.Directory ? 0x10u : 0x80u).CopyTo(result, offset + 36);
            if (hasTime) BitConverter.GetBytes(entry.Remote!.LastWriteTimeUtc.ToFileTimeUtc()).CopyTo(result, offset + 56);
            BitConverter.GetBytes((uint)((ulong)entry.Length >> 32)).CopyTo(result, offset + 64);
            BitConverter.GetBytes((uint)entry.Length).CopyTo(result, offset + 68);
            var name = Encoding.Unicode.GetBytes(entry.Name); if (name.Length > 518) throw new IOException("Слишком длинный относительный путь Windows Shell."); name.CopyTo(result, offset + 72);
        }
        return result;
    }
    public int QueryGetData(ref FORMATETC f)
    {
        if (f.dwAspect != DVASPECT.DVASPECT_CONTENT) return ShellInterop.FormatError;
        if (f.cfFormat == ShellInterop.Contents) return (f.tymed & TYMED.TYMED_ISTREAM) != 0 ? 0 : ShellInterop.MediumError;
        if (f.cfFormat == ShellInterop.Descriptor || f.cfFormat == ShellInterop.Token || f.cfFormat == ShellInterop.Preferred || f.cfFormat == ShellInterop.InDragLoop)
            return (f.tymed & TYMED.TYMED_HGLOBAL) != 0 ? 0 : ShellInterop.MediumError;
        return ShellInterop.FormatError; // CF_HDROP is deliberately never advertised for remote data.
    }
    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new COMException("GetDataHere не поддерживается.", ShellInterop.NotImplemented);
    public int GetCanonicalFormatEtc(ref FORMATETC input, out FORMATETC output) { output = input; output.ptd = IntPtr.Zero; return 0x00040130; }
    public void SetData(ref FORMATETC format, ref STGMEDIUM medium, bool release)
    {
        try { /* Shell owns conflict decisions and reports final success through EndOperation. */ }
        finally { if (release) ShellInterop.ReleaseStgMedium(ref medium); }
    }
    public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
    {
        if (direction != DATADIR.DATADIR_GET) throw new COMException("Только получение форматов.", ShellInterop.NotImplemented);
        FORMATETC Format(short id, TYMED medium) => new() { cfFormat = id, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = medium };
        return new FormatEnumerator([Format(ShellInterop.Token, TYMED.TYMED_HGLOBAL), Format(ShellInterop.Descriptor, TYMED.TYMED_HGLOBAL), Format(ShellInterop.Contents, TYMED.TYMED_ISTREAM), Format(ShellInterop.Preferred, TYMED.TYMED_HGLOBAL), Format(ShellInterop.InDragLoop, TYMED.TYMED_HGLOBAL)]);
    }
    public int DAdvise(ref FORMATETC f, ADVF flags, IAdviseSink sink, out int connection) { connection = 0; return ShellInterop.UnsupportedAdvice; }
    public void DUnadvise(int connection) => throw new COMException("OLE advice недоступен.", ShellInterop.UnsupportedAdvice);
    public int EnumDAdvise(out IEnumSTATDATA? advise) { advise = null; return ShellInterop.UnsupportedAdvice; }
    public int SetAsyncMode(bool value) { asyncMode = value; return 0; }
    public int GetAsyncMode(out bool value) { value = asyncMode; return 0; }
    public int StartOperation(IntPtr reserved)
    {
        try { lock (gate) { if (ended) return ShellInterop.Abort; inOperation = true; active[id] = this; } EnsureOperation(); return 0; }
        catch (Exception e) { Failed(e); return Marshal.GetHRForException(e); }
    }
    public int InOperation(out bool value) { lock (gate) value = inOperation; return 0; }
    public int EndOperation(int result, IntPtr reserved, uint effects)
    {
        NativeTrace.Write("EndOperation enter " + result.ToString("X"));
        lock (gate) { ended = true; inOperation = false; }
        if (result == ShellInterop.Abort) { if (!faulted) Cancel(); }
        else if (result < 0) Failed(new COMException("Windows Explorer прервал получение файлов.", result));
        else if (!cancellation.IsCancellationRequested)
        {
            var entries = manifest?.IsCompletedSuccessfully == true ? manifest.Result : [];
            int complete; lock (gate) complete = fullyRead.Count;
            operation?.Complete(complete, manifestSkipped + entries.Count(e => !e.Directory) - complete);
        }
        _ = Task.Run(CloseReaders); active.TryRemove(id, out _); hostStopped.Set(); NativeTrace.Write("EndOperation exit"); return 0;
    }
    public CustomQueryInterfaceResult GetInterface(ref Guid iid, out IntPtr ppv)
    {
        ppv = IntPtr.Zero;
        if (iid != ShellInterop.MarshalId || marshaler == IntPtr.Zero) return CustomQueryInterfaceResult.NotHandled;
        return Marshal.QueryInterface(marshaler, in iid, out ppv) == 0 ? CustomQueryInterfaceResult.Handled : CustomQueryInterfaceResult.Failed;
    }
    internal ShellFileStream RegisterClone(ShellFileStream stream) { lock (gate) streams.Add(stream); return stream; }
    ~ShellVirtualDataObject() { if (marshaler != IntPtr.Zero) Marshal.Release(marshaler); }
}
