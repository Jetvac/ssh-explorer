using System.Security.Cryptography;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace SSHExplorer.Core;

public sealed class RemoteFileService
{
    private readonly ServerSession _session;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SftpClient? _client;
    private readonly Stack<UndoRecord> _undo = [];
    public string HomeDirectory { get; private set; } = "/";
    public bool Available { get { try { return _client?.IsConnected == true; } catch (ObjectDisposedException) { return false; } } }
    public bool CanUndo { get { lock (_undo) return _undo.Count > 0; } }
    public string? UndoDescription { get { lock (_undo) return _undo.TryPeek(out var item) ? $"Отменить: «{PosixPath.Name(item.Destination)}» → «{PosixPath.Name(item.Source)}»" : null; } }
    public string? LastUndoUnavailableReason { get; private set; }
    internal RemoteFileService(ServerSession session) => _session = session;
    internal async Task ConnectAsync(CancellationToken ct)
    {
        _client = await _session.CreateSftpClientAsync(ct).ConfigureAwait(false);
        HomeDirectory = _client.WorkingDirectory;
    }
    private async Task<T> RunAsync<T>(Func<SftpClient, CancellationToken, T> action, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _session.Lifetime);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (!Available) throw new IOException("SFTP недоступен: " + (_session.SftpError ?? "соединение закрыто"));
            return await Task.Run(() => { linked.Token.ThrowIfCancellationRequested(); return action(_client!, linked.Token); }, linked.Token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
    private Task RunAsync(Action<SftpClient, CancellationToken> action, CancellationToken ct) => RunAsync((c, t) => { action(c, t); return true; }, ct);
    public Task<string> ResolvePathAsync(string path, CancellationToken ct = default) => RunAsync((c, t) =>
    {
        var normalized = PosixPath.Normalize(path, HomeDirectory);
        c.ChangeDirectory(normalized);
        return c.WorkingDirectory;
    }, ct);
    /// <summary>Explicitly follow a link chosen by the user. Ordinary properties and transfer never follow it implicitly.</summary>
    public Task<RemoteEntry> FollowLinkAsync(string path, CancellationToken ct = default) => RunAsync((c,t)=>
    {
        path=PosixPath.Normalize(path,HomeDirectory);
        var original=Find(c,path);
        if(!original.IsSymbolicLink)return Map(original);
        try
        {
            var target=c.Get(path); // This high-level API intentionally performs REALPATH here.
            if(target.IsSymbolicLink)throw new IOException("Цель ссылки недоступна, ссылка повреждена или образует цикл.");
            return Map(target);
        }
        catch(SftpPathNotFoundException ex){throw new IOException("Цель ссылки не найдена: "+path,ex);}
    },ct);
    public Task<IReadOnlyList<RemoteEntry>> ListAsync(string path, CancellationToken ct = default) => RunAsync<IReadOnlyList<RemoteEntry>>((c, t) =>
    {
        path = PosixPath.Normalize(path, HomeDirectory);
        List<RemoteEntry> result = [];
        foreach (var entry in c.ListDirectory(path)) { t.ThrowIfCancellationRequested(); if (entry.Name is not "." and not "..") result.Add(Map(entry)); }
        return result;
    }, ct);
    public async Task<RemoteEntry> StatAsync(string path, CancellationToken ct = default)
    {
        var entry = await RunAsync((c, t) => Map(Find(c, PosixPath.Normalize(path, HomeDirectory))), ct).ConfigureAwait(false);
        if (entry.IsSymbolicLink)
        {
            string? target = null;
            try { target = await ReadLinkAsync(entry.FullPath, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { target = "Недоступно: " + ErrorMessages.Describe(ex); }
            return CloneWithTarget(entry, target);
        }
        return entry;
    }
    internal Task<string> ReadLinkAsync(string path, CancellationToken ct) => RunAsync((c,t)=>SftpProtocolAdapter.ReadLink(c,PosixPath.Normalize(path,HomeDirectory)),ct);
    public Task CreateDirectoryAsync(string path, CancellationToken ct = default) => RunAsync((c, t) =>
    { path = PosixPath.Normalize(path, HomeDirectory); PosixPath.ValidateName(PosixPath.Name(path)); if (Exists(c, path)) throw new IOException("Объект с таким именем уже существует."); c.CreateDirectory(path); }, ct);
    public Task CreateFileAsync(string path, CancellationToken ct = default) => RunAsync((c, t) =>
    { path = PosixPath.Normalize(path, HomeDirectory); PosixPath.ValidateName(PosixPath.Name(path)); using var stream = c.Open(path, FileMode.CreateNew, FileAccess.Write); }, ct);
    /// <summary>MKDIR and exclusive CREATE are authoritative; an existence preflight alone cannot avoid a race.</summary>
    public Task<string> CreateUniqueAsync(string parent, bool directory, CancellationToken ct = default) => RunAsync((c, t) =>
    {
        parent = PosixPath.Normalize(parent, HomeDirectory);
        for (var index = 1; index <= 1000; index++)
        {
            t.ThrowIfCancellationRequested();
            var suffix = index == 1 ? "" : $" ({index})";
            var name = directory ? "Новая папка" + suffix : "Новый файл" + suffix + ".txt";
            var path = PosixPath.Combine(parent, name);
            try
            {
                if (directory) c.CreateDirectory(path);
                else { using var stream = c.Open(path, FileMode.CreateNew, FileAccess.Write); }
                return path;
            }
            catch (Exception ex) when (ex is SshException or IOException)
            {
                if (!Exists(c, path)) throw;
            }
        }
        throw new IOException("Не удалось подобрать свободное имя. Обновите папку и повторите создание.");
    }, ct);
    public Task RenameAsync(string source, string destination, CancellationToken ct = default) => RunAsync((c, t) =>
    {
        source = PosixPath.Normalize(source, HomeDirectory); destination = PosixPath.Normalize(destination, HomeDirectory);
        GuardRoot(source); PosixPath.ValidateName(PosixPath.Name(destination));
        if (source == destination) return;
        var file = Find(c, source);
        if (file.IsDirectory && PosixPath.IsSameOrDescendant(destination, source)) throw new IOException("Нельзя переместить папку внутрь самой себя.");
        if (Exists(c, destination)) throw new IOException("Объект назначения уже существует; переименование не перезаписывает данные.");
        string? signature = null;
        LastUndoUnavailableReason = null;
        try { signature = Signature(c, file, t); }
        catch (Exception ex) when (ex is IOException or SshException or UnauthorizedAccessException or NotSupportedException)
        { LastUndoUnavailableReason = "Переименование выполнится без отмены: " + ErrorMessages.Describe(ex); }
        t.ThrowIfCancellationRequested();
        file.MoveTo(destination); // ISftpFile from directory listing retains the symlink itself.
        lock (_undo)
        {
            if(signature is null) { _undo.Clear(); return; }
            if (_undo.Count >= 20) { var recent = _undo.Take(19).Reverse().ToArray(); _undo.Clear(); foreach (var item in recent) _undo.Push(item); }
            _undo.Push(new(source, destination, signature));
        }
    }, ct);
    public Task UndoAsync(CancellationToken ct = default) => RunAsync((c, t) =>
    {
        UndoRecord undo; lock (_undo) undo = _undo.Count > 0 ? _undo.Peek() : throw new InvalidOperationException("Нет доступной отмены.");
        if (Exists(c, undo.Source)) throw new IOException("Отмена невозможна: исходное имя уже занято.");
        var current = Find(c, undo.Destination);
        if (Signature(c, current, t) != undo.Signature) throw new IOException("Отмена невозможна: объект или его дерево изменены после операции.");
        current.MoveTo(undo.Source); lock (_undo) _undo.Pop();
    }, ct);
    public Task DeleteAsync(string path, CancellationToken ct = default) => RunAsync((c, t) => DeleteTree(c, PosixPath.Normalize(path, HomeDirectory), t), ct);
    public Task SetPermissionsAsync(string path, short mode, bool recursive = false, CancellationToken ct = default) => RunAsync((c, t) =>
    {
        path = PosixPath.Normalize(path, HomeDirectory);
        ApplyTree(c, Find(c, path), recursive, (f) =>
        {
            if (f.IsSymbolicLink) throw new IOException("Права символической ссылки не изменяются: SFTP SETSTAT может затронуть её цель.");
            f.SetPermissions(mode);
        }, t);
    }, ct);
    public Task SetOwnerAsync(string path, int uid, int gid, bool recursive = false, CancellationToken ct = default) => RunAsync((c, t) =>
    {
        if (uid < 0 || gid < 0) throw new ArgumentOutOfRangeException(nameof(uid), "UID/GID должны быть неотрицательными.");
        ApplyTree(c, Find(c, PosixPath.Normalize(path, HomeDirectory)), recursive, f =>
        {
            if (f.IsSymbolicLink) throw new IOException("Владелец символической ссылки не изменяется: SFTP SETSTAT может затронуть её цель.");
            f.UserId = uid; f.GroupId = gid; f.UpdateStatus();
        }, t);
    }, ct);
    public Task<byte[]> ReadBytesAsync(string path, long maxBytes, CancellationToken ct = default) => RunAsync((c, t) =>
    {
        if (maxBytes is < 1 or > 128 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        var file = Find(c, PosixPath.Normalize(path, HomeDirectory));
        if (!file.IsRegularFile || file.IsSymbolicLink) throw new IOException("Предпросмотр доступен только для обычного файла, без перехода по ссылке.");
        if (file.Length > maxBytes) throw new IOException("Файл превышает лимит предпросмотра.");
        using var input = c.OpenRead(file.FullName); using var output = new MemoryStream(); byte[] buffer = new byte[65536];
        while (true) { t.ThrowIfCancellationRequested(); int read = input.Read(buffer, 0, buffer.Length); if (read == 0) break; if (output.Length + read > maxBytes) throw new IOException("Файл вырос и превысил лимит предпросмотра."); output.Write(buffer, 0, read); }
        var after = Find(c, file.FullName);
        if (after.Length != file.Length || after.LastWriteTimeUtc != file.LastWriteTimeUtc) throw new IOException("Файл изменён во время загрузки предпросмотра.");
        return output.ToArray();
    }, ct);
    public Task<long> CalculateSizeAsync(string path, CancellationToken ct = default) => RunAsync((c, t) =>
    {
        long total = 0;
        ApplyTree(c, Find(c, PosixPath.Normalize(path, HomeDirectory)), true, f => { if (f.IsRegularFile && !f.IsSymbolicLink) total = checked(total + f.Length); }, t);
        return total;
    }, ct);
    internal static ISftpFile Find(SftpClient c, string path)
    {
        return SftpProtocolAdapter.Get(c,PosixPath.Normalize(path));
    }
    internal static bool Exists(SftpClient c, string path)
    { try { _ = Find(c, path); return true; } catch (SftpPathNotFoundException) { return false; } }
    internal static void GuardRoot(string path) { if (PosixPath.Normalize(path) == "/") throw new IOException("Операции удаления и перемещения корня запрещены."); }
    internal static void DeleteTree(SftpClient c, string path, CancellationToken ct, int depth = 0)
    {
        GuardRoot(path); ct.ThrowIfCancellationRequested(); if (depth > 128) throw new IOException("Превышен предел глубины каталогов (128).");
        var file = Find(c, path);
        if (file.IsDirectory && !file.IsSymbolicLink)
            foreach (var child in c.ListDirectory(path).Where(x => x.Name is not "." and not "..").ToArray()) DeleteTree(c, child.FullName, ct, depth + 1);
        ct.ThrowIfCancellationRequested(); file.Delete();
    }
    private static void ApplyTree(SftpClient c, ISftpFile file, bool recursive, Action<ISftpFile> action, CancellationToken ct, int depth = 0)
    {
        ct.ThrowIfCancellationRequested(); if (depth > 128) throw new IOException("Превышен предел глубины каталогов (128).");
        if (recursive && file.IsDirectory && !file.IsSymbolicLink)
            foreach (var child in c.ListDirectory(file.FullName).Where(x => x.Name is not "." and not "..")) ApplyTree(c, child, true, action, ct, depth + 1);
        action(file);
    }
    internal static RemoteEntry Map(ISftpFile f) => new()
    {
        Name = f.Name, FullPath = f.FullName, IsDirectory = f.IsDirectory, IsRegularFile = f.IsRegularFile, IsSymbolicLink = f.IsSymbolicLink,
        Length = f.Length, LastAccessTimeUtc = f.LastAccessTimeUtc, LastWriteTimeUtc = f.LastWriteTimeUtc, UserId = f.UserId, GroupId = f.GroupId,
        Permissions = ReadMode(f.Attributes)
    };
    private static short ReadMode(SftpFileAttributes attributes)
    {
        // GetBytes serializes only attributes changed since receipt, so it cannot be used to
        // read a fetched mode. Public flags include all twelve POSIX permission bits.
        return (short)((attributes.IsUIDBitSet?2048:0)|(attributes.IsGroupIDBitSet?1024:0)|(attributes.IsStickyBitSet?512:0)|
            (attributes.OwnerCanRead?256:0)|(attributes.OwnerCanWrite?128:0)|(attributes.OwnerCanExecute?64:0)|
            (attributes.GroupCanRead?32:0)|(attributes.GroupCanWrite?16:0)|(attributes.GroupCanExecute?8:0)|
            (attributes.OthersCanRead?4:0)|(attributes.OthersCanWrite?2:0)|(attributes.OthersCanExecute?1:0));
    }
    private static RemoteEntry CloneWithTarget(RemoteEntry f, string? target) => new() { Name=f.Name,FullPath=f.FullPath,IsDirectory=f.IsDirectory,IsRegularFile=f.IsRegularFile,IsSymbolicLink=f.IsSymbolicLink,Length=f.Length,LastAccessTimeUtc=f.LastAccessTimeUtc,LastWriteTimeUtc=f.LastWriteTimeUtc,UserId=f.UserId,GroupId=f.GroupId,Permissions=f.Permissions,LinkTarget=target };
    private static string Signature(SftpClient c, ISftpFile file, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); int count = 0; long contentBytes=0;
        void Visit(ISftpFile item, string relative, int depth)
        {
            ct.ThrowIfCancellationRequested();
            if (++count > 100000 || depth > 128) throw new IOException("Объект слишком велик для безопасного журнала отмены.");
            var entry = Map(item);
            hash.AppendData(Encoding.UTF8.GetBytes($"{relative.Length}:{relative}:{entry.IsDirectory}:{entry.IsSymbolicLink}:{entry.Length}:{entry.LastWriteTimeUtc.Ticks}:{entry.Permissions}:{entry.UserId}:{entry.GroupId}\0"));
            if(item.IsSymbolicLink)
            {
                string target=SftpProtocolAdapter.ReadLink(c,item.FullName);
                hash.AppendData(Encoding.UTF8.GetBytes($"{target.Length}:{target}\0"));
            }
            else if(item.IsRegularFile)
            {
                if(contentBytes+item.Length>256L*1024*1024)throw new IOException("Размер содержимого превышает лимит безопасной отмены 256 МБ.");
                using var input=c.OpenRead(item.FullName);byte[] buffer=new byte[65536];long readTotal=0;
                while(true){ct.ThrowIfCancellationRequested();int read=input.Read(buffer,0,buffer.Length);if(read==0)break;readTotal+=read;contentBytes+=read;if(contentBytes>256L*1024*1024)throw new IOException("Источник вырос и превысил лимит безопасной отмены.");hash.AppendData(buffer.AsSpan(0,read));}
                if(readTotal!=item.Length)throw new IOException("Объект изменён при подготовке безопасной отмены.");
                var after=Find(c,item.FullName);
                if(after.Length!=item.Length||after.LastWriteTimeUtc!=item.LastWriteTimeUtc||!after.IsRegularFile)throw new IOException("Объект изменён при подготовке безопасной отмены.");
            }
            else if(!item.IsDirectory)throw new IOException("Для специального объекта безопасная отмена недоступна.");
            if (item.IsDirectory && !item.IsSymbolicLink)
                foreach (var child in c.ListDirectory(item.FullName).Where(x=>x.Name is not "." and not "..").OrderBy(x=>x.Name,StringComparer.Ordinal)) Visit(child, relative+"/"+child.Name,depth+1);
        }
        Visit(file, "", 0); return Convert.ToHexString(hash.GetHashAndReset());
    }
    private sealed record UndoRecord(string Source, string Destination, string Signature);
}
