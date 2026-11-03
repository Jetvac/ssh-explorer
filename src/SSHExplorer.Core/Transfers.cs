using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace SSHExplorer.Core;

public enum ConflictDecision { Replace, Skip, KeepBoth, Cancel }
public sealed record TransferConflict(string Source, string Destination, bool SourceIsDirectory, bool DestinationIsDirectory, long? SourceLength, long? DestinationLength, string? Reason = null, Guid? JobId = null, int Attempt = 1);
public enum TransferState { Queued, Running, Completed, Cancelled, Failed }

public sealed class TransferJob : INotifyPropertyChanged
{
    private readonly object _sync = new();
    private CancellationTokenSource _cancel = new();
    private readonly Stopwatch _watch = new();
    private long _bytes;
    private long _notifiedAt;
    internal Func<Task>? RetryAction;
    internal Task Completion = Task.CompletedTask;
    internal Func<CancellationToken, Task>? Operation;
    internal ServerSession[] Sessions = [];
    public Guid Id { get; } = Guid.NewGuid();
    public Guid SessionId { get; internal set; }
    public string Name { get; internal set; } = "";
    public string Source { get; internal set; } = "";
    public string Destination { get; internal set; } = "";
    public long BytesTransferred => Interlocked.Read(ref _bytes);
    public long? TotalBytes { get; internal set; }
    public double BytesPerSecond => _watch.Elapsed.TotalSeconds > 0 ? BytesTransferred / _watch.Elapsed.TotalSeconds : 0;
    public TimeSpan? Remaining => State == TransferState.Completed ? TimeSpan.Zero : TotalBytes is long total && BytesPerSecond > 0 ? TimeSpan.FromSeconds(Math.Max(0, total - BytesTransferred) / BytesPerSecond) : null;
    public double? Progress => State == TransferState.Completed ? 100 : TotalBytes is long total && total > 0 ? Math.Min(100, BytesTransferred * 100d / total) : null;
    public TransferState State { get; internal set; } = TransferState.Queued;
    public string? Error { get; internal set; }
    public int CompletedItems { get; internal set; }
    public int SkippedItems { get; internal set; }
    public int Attempt { get; private set; } = 1;
    public string Summary => $"Выполнено объектов: {CompletedItems}; пропущено: {SkippedItems}";
    public event PropertyChangedEventHandler? PropertyChanged;
    internal CancellationToken Cancellation => _cancel.Token;
    public void Cancel() { lock (_sync) _cancel.Cancel(); }
    public Task RetryAsync() => RetryAction?.Invoke() ?? Task.CompletedTask;
    internal void Reset()
    {
        lock (_sync) { _cancel.Dispose(); _cancel = new(); }
        Interlocked.Exchange(ref _bytes, 0); TotalBytes = null; Error = null; CompletedItems = 0; SkippedItems = 0; Attempt++; _notifiedAt = 0; _watch.Restart(); State = TransferState.Queued; Notify();
    }
    internal void Start() { _watch.Restart(); State = TransferState.Running; Notify(); }
    internal void AddBytes(int bytes)
    {
        Interlocked.Add(ref _bytes, bytes);
        if (_watch.ElapsedMilliseconds - Interlocked.Read(ref _notifiedAt) >= 150)
        { Interlocked.Exchange(ref _notifiedAt, _watch.ElapsedMilliseconds); Notify(); }
    }
    internal void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    internal void Finish() { _watch.Stop(); Notify(); }
}

/// <summary>A shared, bounded queue. All streaming work uses dedicated session-owned SFTP clients.</summary>
public sealed partial class TransferQueue
{
    private readonly List<TransferJob> _jobs = [];
    private readonly SemaphoreSlim _parallelism = new(2, 2);
    private readonly AsyncLocal<TransferJob?> _currentJob = new();
    public IReadOnlyList<TransferJob> Jobs { get { lock (_jobs) return _jobs.ToArray(); } }
    public event EventHandler? Changed;
    public Func<TransferConflict, Task<ConflictDecision>>? ResolveConflict { get; set; }
    public bool HasActive(Guid sessionId) => Jobs.Any(x => x.Sessions.Any(s => s.Id == sessionId) && x.State is TransferState.Queued or TransferState.Running);
    public void CancelSession(Guid sessionId) { foreach (var job in Jobs.Where(x => x.Sessions.Any(s => s.Id == sessionId))) if (job.State is TransferState.Queued or TransferState.Running) job.Cancel(); }
    public Task WaitAsync(TransferJob job, CancellationToken ct = default) => job.Completion.WaitAsync(ct);
    private TransferJob Enqueue(TransferJob job)
    {
        job.RetryAction = () =>
        {
            lock (job)
            {
                if (job.State is TransferState.Running or TransferState.Queued) return job.Completion;
                if (job.Sessions.Any(s => s.Lifetime.IsCancellationRequested)) throw new IOException("Сессия закрыта. Создайте новую передачу после подключения.");
                job.Reset(); job.Completion = Task.Run(() => RunJobAsync(job)); return job.Completion;
            }
        };
        lock (_jobs)
        {
            if (_jobs.Count >= 200) _jobs.RemoveAll(x => x.State is not TransferState.Queued and not TransferState.Running);
            _jobs.Add(job);
        }
        job.PropertyChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        job.Completion = Task.Run(() => RunJobAsync(job)); Changed?.Invoke(this, EventArgs.Empty); return job;
    }
    private async Task RunJobAsync(TransferJob job)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(job.Sessions.Select(x => x.Lifetime).Append(job.Cancellation).ToArray());
        bool acquired = false;
        try
        {
            await _parallelism.WaitAsync(linked.Token).ConfigureAwait(false); acquired = true; _currentJob.Value = job; job.Start();
            await job.Operation!(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested(); job.State = TransferState.Completed;
        }
        catch (OperationCanceledException) { job.State = TransferState.Cancelled; job.Error = "Передача отменена. " + job.Summary; }
        catch (Exception ex) { job.State = TransferState.Failed; job.Error = ErrorMessages.Describe(ex) + " " + job.Summary; }
        finally { _currentJob.Value = null; if (acquired) _parallelism.Release(); job.Finish(); }
    }
    public TransferJob Upload(ServerSession target, IEnumerable<string> localPaths, string remoteDirectory)
    {
        var paths = localPaths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0) throw new ArgumentException("Не выбраны файлы для загрузки.");
        var job = new TransferJob { SessionId=target.Id, Sessions=[target], Name=paths.Length == 1 ? Path.GetFileName(paths[0]) : $"Загрузка {paths.Length} объектов",Source=string.Join("; ",paths),Destination=remoteDirectory };
        job.Operation = async ct =>
        {
            var destination = await target.Files.ResolvePathAsync(remoteDirectory, ct).ConfigureAwait(false);
            using var lease = await target.LeaseSftpAsync(ct).ConfigureAwait(false);
            var client = lease.Client;
            await Task.Run(async () =>
            {
                job.TotalBytes = paths.Sum(p => LocalSize(p, ct)); job.Notify();
                foreach (var path in paths) await UploadItemAsync(job, client, path, PosixPath.Combine(destination, Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar))), ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        };
        return Enqueue(job);
    }
    public TransferJob Download(ServerSession source, IEnumerable<string> remotePaths, string localDirectory)
    {
        var paths = remotePaths.Select(x => PosixPath.Normalize(x, source.Files.HomeDirectory)).Distinct(StringComparer.Ordinal).ToArray();
        if (paths.Length == 0) throw new ArgumentException("Не выбраны файлы для скачивания.");
        string destination = Path.GetFullPath(localDirectory);
        var job = new TransferJob { SessionId=source.Id,Sessions=[source],Name=paths.Length == 1 ? PosixPath.Name(paths[0]) : $"Скачивание {paths.Length} объектов",Source=string.Join("; ",paths),Destination=destination };
        job.Operation = async ct =>
        {
            WindowsNames.EnsureNoReparseAncestors(destination);
            Directory.CreateDirectory(destination);
            using var lease = await source.LeaseSftpAsync(ct).ConfigureAwait(false);
            var client = lease.Client;
            await Task.Run(async () =>
            {
                job.TotalBytes = paths.Sum(p => RemoteSize(client, p, ct)); job.Notify();
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in paths)
                {
                    string local = await LocalNameAsync(path, destination, names, ct).ConfigureAwait(false);
                    if (local.Length == 0) { job.SkippedItems++; continue; }
                    await DownloadItemAsync(job, client, path, local, ct).ConfigureAwait(false);
                }
            }, ct).ConfigureAwait(false);
        };
        return Enqueue(job);
    }
    public TransferJob Copy(ServerSession source, IEnumerable<string> remotePaths, ServerSession target, string remoteDirectory, bool move = false)
    {
        var paths = remotePaths.Select(x => PosixPath.Normalize(x, source.Files.HomeDirectory)).Distinct(StringComparer.Ordinal).ToArray();
        if (paths.Length == 0) throw new ArgumentException("Не выбраны файлы.");
        bool sameEndpoint = HostTrustStore.NormalizeHost(source.Profile.Host) == HostTrustStore.NormalizeHost(target.Profile.Host) && source.Profile.Port == target.Profile.Port && source.Profile.Username == target.Profile.Username;
        var job = new TransferJob { SessionId=target.Id,Sessions=ReferenceEquals(source,target) ? [source] : [source,target],Name=(move ? "Перемещение: " : "Копирование: ")+(paths.Length==1?PosixPath.Name(paths[0]):$"{paths.Length} объектов"),Source=string.Join("; ",paths),Destination=remoteDirectory };
        job.Operation = async ct =>
        {
            string destination = await target.Files.ResolvePathAsync(remoteDirectory, ct).ConfigureAwait(false);
            if (sameEndpoint)
                foreach (var path in paths) if (PosixPath.IsSameOrDescendant(destination, path)) throw new IOException("Нельзя копировать или перемещать объект в самого себя либо своего потомка.");
            using var inputLease = await source.LeaseSftpAsync(ct).ConfigureAwait(false);
            using var outputLease = await target.LeaseSftpAsync(ct).ConfigureAwait(false);
            var input = inputLease.Client;
            var output = outputLease.Client;
            await Task.Run(async () =>
            {
                job.TotalBytes = paths.Sum(p => RemoteSize(input, p, ct)); job.Notify();
                foreach (var path in paths)
                {
                    string dest = PosixPath.Combine(destination, PosixPath.Name(path));
                    if (sameEndpoint && dest == path) throw new IOException("Источник и назначение совпадают.");
                    if (move && sameEndpoint && !RemoteFileService.Exists(output,dest))
                    {
                        try { await source.Files.RenameAsync(path,dest,ct).ConfigureAwait(false); job.CompletedItems++; continue; }
                        catch (SshException ex) when (ex.Message.Contains("cross",StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("failure",StringComparison.OrdinalIgnoreCase)) { /* A cross-filesystem rename can fail generically in SFTP v3. Fall back to verified streaming copy. */ }
                    }
                    await CopyItemAsync(job,source,input,output,path,dest,move,ct).ConfigureAwait(false);
                }
            },ct).ConfigureAwait(false);
        };
        return Enqueue(job);
    }
    private async Task<ConflictDecision> DecideAsync(TransferConflict conflict, CancellationToken ct)
    {
        conflict = conflict with { JobId = _currentJob.Value?.Id, Attempt = _currentJob.Value?.Attempt ?? 1 };
        var result = ResolveConflict is null ? ConflictDecision.Cancel : await ResolveConflict(conflict).WaitAsync(ct).ConfigureAwait(false);
        if (result == ConflictDecision.Cancel) throw new OperationCanceledException("Конфликт передачи не подтверждён.",ct);
        return result;
    }
    private async Task<string?> RemoteDestinationAsync(SftpClient client, string source, string destination, bool isDirectory, long? length, CancellationToken ct)
    {
        if (!RemoteFileService.Exists(client,destination)) return destination;
        var existing = RemoteFileService.Find(client,destination);
        if (isDirectory && existing.IsDirectory && !existing.IsSymbolicLink) return destination; // Merge directories; preserve unrelated contents.
        var decision = await DecideAsync(new(source,destination,isDirectory,existing.IsDirectory,length,existing.IsRegularFile?existing.Length:null, isDirectory != existing.IsDirectory ? "Типы объектов различаются" : null),ct).ConfigureAwait(false);
        if (decision == ConflictDecision.Skip) return null;
        if (decision == ConflictDecision.KeepBoth) return UniqueRemote(client,destination);
        if (isDirectory || existing.IsDirectory) throw new IOException("Замена файла папкой или папки файлом требует отдельного удаления. Выберите «Сохранить оба».");
        return destination;
    }
    private async Task UploadItemAsync(TransferJob job,SftpClient client,string source,string destination,CancellationToken ct,int depth=0)
    {
        ct.ThrowIfCancellationRequested(); CheckDepth(depth);
        var info = new FileInfo(source);
        if ((File.GetAttributes(source)&FileAttributes.ReparsePoint)!=0) throw new IOException("Загрузка локальных символических ссылок и reparse points запрещена без явного выбора цели: "+source);
        bool directory = Directory.Exists(source);
        var dest = await RemoteDestinationAsync(client,source,destination,directory,directory?null:info.Length,ct).ConfigureAwait(false);
        if (dest is null) { job.SkippedItems++; return; }
        if (directory)
        {
            if (!RemoteFileService.Exists(client,dest)) client.CreateDirectory(dest);
            foreach(var child in Directory.EnumerateFileSystemEntries(source)) await UploadItemAsync(job,client,child,PosixPath.Combine(dest,Path.GetFileName(child)),ct,depth+1).ConfigureAwait(false);
            job.CompletedItems++; return;
        }
        long expected=info.Length; DateTime modified=info.LastWriteTimeUtc;
        var prior=SnapshotOrNull(client,dest);
        string temporary=PosixPath.Combine(PosixPath.Parent(dest),".sshexplorer-"+Guid.NewGuid().ToString("N")+".part");
        try
        {
            using(var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read,128*1024,FileOptions.SequentialScan))
            using(var output=client.Open(temporary,FileMode.CreateNew,FileAccess.Write)) await StreamAsync(job,input,output,expected,ct).ConfigureAwait(false);
            info.Refresh(); if(info.Length!=expected||info.LastWriteTimeUtc!=modified)throw new IOException("Источник изменён во время загрузки: "+source);
            if(RemoteFileService.Find(client,temporary).Length!=expected)throw new IOException("Размер загруженного файла не совпадает: "+temporary);
            client.SetLastWriteTimeUtc(temporary,modified);
            CommitRemote(client,temporary,dest,prior,ct); job.CompletedItems++;
        }
        catch(Exception ex)
        {
            var diagnosed=DiagnoseWriteFailure(client,dest,ex,ct);
            TryDeleteOwned(client,temporary);
            if(!ReferenceEquals(diagnosed,ex))throw diagnosed;
            throw;
        }
    }
    private async Task DownloadItemAsync(TransferJob job,SftpClient client,string source,string destination,CancellationToken ct,int depth=0)
    {
        ct.ThrowIfCancellationRequested();CheckDepth(depth);
        var file=RemoteFileService.Find(client,source);
        if(file.IsSymbolicLink)throw new IOException("Скачивание символической ссылки в Windows требует явного выбора цели. Ссылка не разыменована: "+source);
        if(!file.IsDirectory&&!file.IsRegularFile)throw new IOException("Нельзя скачать специальный объект: "+source);
        WindowsNames.EnsureNoReparseAncestors(destination);
        if(File.Exists(destination)||Directory.Exists(destination))
        {
            bool destDirectory=Directory.Exists(destination);
            if(!(file.IsDirectory&&destDirectory))
            {
                var decision=await DecideAsync(new(source,destination,file.IsDirectory,destDirectory,file.IsRegularFile?file.Length:null,File.Exists(destination)?new FileInfo(destination).Length:null),ct).ConfigureAwait(false);
                if(decision==ConflictDecision.Skip){job.SkippedItems++;return;}
                if(decision==ConflictDecision.KeepBoth)destination=UniqueLocal(destination);
                else if(file.IsDirectory||destDirectory)throw new IOException("Замена файла папкой или папки файлом требует отдельного удаления. Выберите «Сохранить оба».");
            }
        }
        if(file.IsDirectory)
        {
            Directory.CreateDirectory(destination);var used=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var child in client.ListDirectory(source).Where(x=>x.Name is not "." and not ".."))
            {
                string local=await LocalNameAsync(child.FullName,destination,used,ct).ConfigureAwait(false);
                if(local.Length==0){job.SkippedItems++;continue;}
                await DownloadItemAsync(job,client,child.FullName,local,ct,depth+1).ConfigureAwait(false);
            }
            job.CompletedItems++;return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var prior=LocalSnapshot(destination);
        string temporary=Path.Combine(Path.GetDirectoryName(destination)!,".sshexplorer-"+Guid.NewGuid().ToString("N")+".part");
        try
        {
            using(var input=client.OpenRead(source))
            using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,128*1024,FileOptions.SequentialScan))
            { await StreamAsync(job,input,output,file.Length,ct).ConfigureAwait(false);output.Flush(true); }
            var after=RemoteFileService.Find(client,source);if(!Same(file,after))throw new IOException("Источник изменён во время скачивания: "+source);
            if(LocalSnapshot(destination)!=prior)throw new IOException("Локальное назначение изменено во время передачи: "+destination);
            ct.ThrowIfCancellationRequested();
            if(File.Exists(destination))File.Replace(temporary,destination,null);else File.Move(temporary,destination);
            File.SetLastWriteTimeUtc(destination,file.LastWriteTimeUtc);job.CompletedItems++;
        }
        catch{try{if(File.Exists(temporary))File.Delete(temporary);}catch{}throw;}
    }
    private async Task CopyItemAsync(TransferJob job,ServerSession sourceSession,SftpClient input,SftpClient output,string source,string destination,bool move,CancellationToken ct,int depth=0)
    {
        ct.ThrowIfCancellationRequested();CheckDepth(depth);RemoteFileService.GuardRoot(source);
        var file=RemoteFileService.Find(input,source);
        var dest=await RemoteDestinationAsync(output,source,destination,file.IsDirectory,file.IsRegularFile?file.Length:null,ct).ConfigureAwait(false);
        if(dest is null){job.SkippedItems++;return;}
        if(file.IsDirectory&&!file.IsSymbolicLink)
        {
            if(!RemoteFileService.Exists(output,dest))output.CreateDirectory(dest);
            int skipped=job.SkippedItems;
            foreach(var child in input.ListDirectory(source).Where(x=>x.Name is not "." and not "..").ToArray())
                await CopyItemAsync(job,sourceSession,input,output,child.FullName,PosixPath.Combine(dest,child.Name),move,ct,depth+1).ConfigureAwait(false);
            if(move&&job.SkippedItems==skipped){ct.ThrowIfCancellationRequested();RemoteFileService.Find(input,source).Delete();} // RMDIR fails if new or unsent entries remain.
            job.CompletedItems++;return;
        }
        if(file.IsSymbolicLink)
        {
            string target=await sourceSession.Files.ReadLinkAsync(source,ct).ConfigureAwait(false);
            if(RemoteFileService.Exists(output,dest))throw new IOException("Перезапись символической ссылки не выполняется. Выберите «Сохранить оба».");
            SftpProtocolAdapter.SymbolicLink(output,target,dest);
            if(move){ct.ThrowIfCancellationRequested();var current=RemoteFileService.Find(input,source);if(!Same(file,current))throw new IOException("Ссылка изменилась; источник сохранён.");current.Delete();}
            job.CompletedItems++;return;
        }
        if(!file.IsRegularFile)throw new IOException("Специальные объекты не копируются: "+source);
        var prior=SnapshotOrNull(output,dest);string temporary=PosixPath.Combine(PosixPath.Parent(dest),".sshexplorer-"+Guid.NewGuid().ToString("N")+".part");
        try
        {
            using(var read=input.OpenRead(source))using(var write=output.Open(temporary,FileMode.CreateNew,FileAccess.Write))await StreamAsync(job,read,write,file.Length,ct).ConfigureAwait(false);
            if(!Same(file,RemoteFileService.Find(input,source)))throw new IOException("Источник изменён во время копирования; он сохранён: "+source);
            if(RemoteFileService.Find(output,temporary).Length!=file.Length)throw new IOException("Размер копии не совпадает.");
            output.SetLastWriteTimeUtc(temporary,file.LastWriteTimeUtc);
            var mapped=RemoteFileService.Map(file);RemoteFileService.Find(output,temporary).SetPermissions(short.Parse(mapped.PermissionsText,System.Globalization.CultureInfo.InvariantCulture));
            CommitRemote(output,temporary,dest,prior,ct);
            if(move){ct.ThrowIfCancellationRequested();var current=RemoteFileService.Find(input,source);if(!Same(file,current))throw new IOException("Источник изменён после копирования и не удалён: "+source);current.Delete();}
            job.CompletedItems++;
        }
        catch(Exception ex)
        {
            var diagnosed=DiagnoseWriteFailure(output,dest,ex,ct);
            TryDeleteOwned(output,temporary);
            if(!ReferenceEquals(diagnosed,ex))throw diagnosed;
            throw;
        }
    }
    private static async Task StreamAsync(TransferJob job,Stream source,Stream destination,long expected,CancellationToken ct)
    {
        byte[] buffer=new byte[128*1024];long copied=0;
        while(true){ct.ThrowIfCancellationRequested();int read=await source.ReadAsync(buffer,ct).ConfigureAwait(false);if(read==0)break;if(copied+read>expected)throw new IOException("Источник вырос во время передачи.");await destination.WriteAsync(buffer.AsMemory(0,read),ct).ConfigureAwait(false);copied+=read;job.AddBytes(read);}
        if(copied!=expected)throw new IOException("Источник изменён: получен неполный файл.");await destination.FlushAsync(ct).ConfigureAwait(false);
    }
    private static string? SnapshotOrNull(SftpClient c,string path)=>RemoteFileService.Exists(c,path)?Stamp(RemoteFileService.Find(c,path)):null;
    private static string Stamp(ISftpFile f)=>$"{f.IsDirectory}:{f.IsSymbolicLink}:{f.Length}:{f.LastWriteTimeUtc.Ticks}:{f.UserId}:{f.GroupId}";
    private static bool Same(ISftpFile a,ISftpFile b)=>Stamp(a)==Stamp(b);
    private static string? LocalSnapshot(string path)=>File.Exists(path)?$"{new FileInfo(path).Length}:{File.GetLastWriteTimeUtc(path).Ticks}":Directory.Exists(path)?"directory":null;
    private static void CommitRemote(SftpClient c,string temporary,string destination,string? prior,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();if(SnapshotOrNull(c,destination)!=prior)throw new IOException("Назначение изменено во время передачи. Оно не перезаписано: "+destination);
        if(prior is null){RemoteFileService.Find(c,temporary).MoveTo(destination);return;}
        var existing=RemoteFileService.Find(c,destination);
        if(existing.IsDirectory)throw new IOException("Папка не может быть перезаписана файлом.");
        if(!existing.IsSymbolicLink)
        {
            var tempFile=RemoteFileService.Find(c,temporary);
            tempFile.SetPermissions(short.Parse(RemoteFileService.Map(existing).PermissionsText,System.Globalization.CultureInfo.InvariantCulture));
        }
        // Exact protocol paths preserve symlink semantics, unlike high-level REALPATH wrappers.
        try{SftpProtocolAdapter.PosixRename(c,temporary,destination);return;}
        catch(SshException ex)when(ex.Message.Contains("support",StringComparison.OrdinalIgnoreCase)||ex.Message.Contains("extension",StringComparison.OrdinalIgnoreCase)){}
        catch(NotSupportedException){}
        string backup=PosixPath.Combine(PosixPath.Parent(destination),".sshexplorer-"+Guid.NewGuid().ToString("N")+".backup");
        existing.MoveTo(backup);
        try{RemoteFileService.Find(c,temporary).MoveTo(destination);RemoteFileService.Find(c,backup).Delete();}
        catch{if(!RemoteFileService.Exists(c,destination)&&RemoteFileService.Exists(c,backup))RemoteFileService.Find(c,backup).MoveTo(destination);throw;}
    }
    private static void TryDeleteOwned(SftpClient c,string temporary){try{if(c.IsConnected&&RemoteFileService.Exists(c,temporary))RemoteFileService.Find(c,temporary).Delete();}catch{}}
    private static Exception DiagnoseWriteFailure(SftpClient client,string destination,Exception error,CancellationToken ct)
    {
        if(ct.IsCancellationRequested||error is not SftpException||error is SftpPermissionDeniedException or SftpPathNotFoundException)return error;
        try
        {
            if(!client.IsConnected)return error;
            var status=client.GetStatus(PosixPath.Parent(destination));
            if(status.AvailableBlocks==0&&status.TotalBlocks>0)
                return new IOException("Недостаточно свободного места на сервере для записи «"+destination+"». Существующий файл сохранён.",error);
            if(status.AvailableNodes==0&&status.TotalNodes>0)
                return new IOException("На сервере закончились свободные inode для записи «"+destination+"». Существующий файл сохранён.",error);
        }
        catch{ /* Unsupported statvfs or lost connection must preserve the original error. */ }
        return error;
    }
    private static void CheckDepth(int depth){if(depth>128)throw new IOException("Превышена глубина передачи (128).");}
    private static long LocalSize(string path,CancellationToken ct,int depth=0)
    {
        ct.ThrowIfCancellationRequested();CheckDepth(depth);if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("Локальные ссылки и reparse points не обходятся: "+path);
        return Directory.Exists(path)?Directory.EnumerateFileSystemEntries(path).Sum(x=>LocalSize(x,ct,depth+1)):new FileInfo(path).Length;
    }
    private static long RemoteSize(SftpClient c,string path,CancellationToken ct,int depth=0)
    {
        ct.ThrowIfCancellationRequested();CheckDepth(depth);var f=RemoteFileService.Find(c,path);if(f.IsSymbolicLink)return 0;
        return RemoteSizeEntry(c,f,ct,depth);
    }
    private static long RemoteSizeEntry(SftpClient c,ISftpFile f,CancellationToken ct,int depth)
    {
        ct.ThrowIfCancellationRequested();CheckDepth(depth);if(f.IsSymbolicLink)return 0;
        return f.IsDirectory?c.ListDirectory(f.FullName).Where(x=>x.Name is not "." and not "..").Sum(x=>RemoteSizeEntry(c,x,ct,depth+1)):f.IsRegularFile?f.Length:throw new IOException("Специальный объект не передаётся: "+f.FullName);
    }
    private static string UniqueRemote(SftpClient c,string path)
    {
        for(int i=2;i<100000;i++){string next=path+$" ({i})";if(!RemoteFileService.Exists(c,next))return next;}throw new IOException("Не удалось выбрать свободное имя.");
    }
    private static string UniqueLocal(string path)
    {
        string parent=Path.GetDirectoryName(path)!,name=Path.GetFileNameWithoutExtension(path),ext=Path.GetExtension(path);
        for(int i=2;i<100000;i++){string next=Path.Combine(parent,$"{name} ({i}){ext}");if(!File.Exists(next)&&!Directory.Exists(next))return next;}throw new IOException("Не удалось выбрать свободное имя.");
    }
    private async Task<string> LocalNameAsync(string remote,string directory,HashSet<string> used,CancellationToken ct)
    {
        string name=PosixPath.Name(remote),safe=WindowsNames.SafeName(name);
        bool collision=!used.Add(safe);
        if(safe!=name||collision)
        {
            string proposed=Path.Combine(directory,safe);if(collision)proposed=UniqueLocal(proposed);
            var decision=await DecideAsync(new(remote,proposed,false,false,null,null,"Имя Linux несовместимо с Windows или конфликтует без учёта регистра. «Сохранить оба» сохраняет безопасное имя и запись соответствия."),ct).ConfigureAwait(false);
            if(decision==ConflictDecision.Skip)return "";
            if(decision!=ConflictDecision.KeepBoth)throw new IOException("Для несовместимого имени выберите «Сохранить оба» или «Пропустить».");
            safe=Path.GetFileName(proposed);used.Add(safe);
            string mapping=Path.Combine(directory,".sshexplorer-name-map.jsonl");
            WindowsNames.EnsureNoReparseAncestors(mapping);
            await File.AppendAllTextAsync(mapping,System.Text.Json.JsonSerializer.Serialize(new{Remote=remote,Local=safe})+Environment.NewLine,ct).ConfigureAwait(false);
        }
        return WindowsNames.ContainedPath(directory,safe);
    }
}

public static class WindowsNames
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase) { "CON","PRN","AUX","NUL","COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9","COM¹","COM²","COM³","LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9","LPT¹","LPT²","LPT³" };
    public static string SafeName(string name)
    {
        PosixPath.ValidateName(name);
        var result=new System.Text.StringBuilder();
        for(int i=0;i<name.Length;i++){char ch=name[i];bool invalid=ch<32||"<>:\"/\\|?*".Contains(ch)||((ch==' '||ch=='.')&&i==name.Length-1);result.Append(invalid?$"%{(int)ch:X4}":ch);}
        string safe=result.ToString();if(Reserved.Contains(safe.Split('.')[0]))safe="_"+safe;
        if(safe.Length>180)safe=safe[..150]+"~"+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name)))[..16];
        return safe;
    }
    public static string ContainedPath(string directory,string name)
    {
        if(name!=Path.GetFileName(name)||name is "." or "..")throw new IOException("Имя пытается выйти из выбранной локальной папки.");
        string root=Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        string result=Path.GetFullPath(Path.Combine(root,name));
        if(!result.StartsWith(root,StringComparison.OrdinalIgnoreCase))throw new IOException("Путь выходит из выбранной локальной папки.");
        return result;
    }
    public static void EnsureNoReparseAncestors(string path)
    {
        for(string? current=Path.GetFullPath(path);current is not null;current=Path.GetDirectoryName(current))
        {
            if((File.Exists(current)||Directory.Exists(current))&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)
                throw new IOException("Передача не следует локальной символической ссылке или reparse point: "+current);
        }
    }
}
