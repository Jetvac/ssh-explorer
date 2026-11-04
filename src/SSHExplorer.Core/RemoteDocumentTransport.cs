using System.Security.Cryptography;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace SSHExplorer.Core;

public sealed record RemoteDocumentIdentity(Guid ProfileId, string Host, int Port, string User, string HostKey)
{
    public static RemoteDocumentIdentity From(ServerSession session) => new(session.Profile.Id, session.Profile.Host.Trim('[', ']').ToLowerInvariant(), session.Profile.Port, session.Profile.Username,
        session.VerifiedHostKey ?? throw new IOException("Нет подтверждённого ключа SSH для редактирования."));
    public string Display => $"{User}@{Host}:{Port}";
}
public sealed record DocumentLocation(string Path, string TargetPath, string? LinkText = null);
public sealed record DocumentMetadata(long Length, DateTime ModifiedUtc, short Mode, int UserId, int GroupId);
public sealed record RemoteDocumentVersion(byte[] Bytes, DocumentMetadata Metadata)
{
    public string Hash => Convert.ToHexStringLower(SHA256.HashData(Bytes));
}
public sealed class DocumentConflictException(string message) : IOException(message);
public interface IRemoteDocumentTransport
{
    RemoteDocumentIdentity Identity { get; }
    Guid SessionId { get; }
    CancellationToken Lifetime { get; }
    bool Available => true;
    Task<DocumentLocation> LocateAsync(string path, bool followLink, CancellationToken ct);
    Task<RemoteDocumentVersion> ReadAsync(DocumentLocation location, CancellationToken ct);
    Task<RemoteDocumentVersion> ReplaceAsync(DocumentLocation location, string expectedHash, DocumentMetadata metadata, byte[] snapshot, Guid operation, Func<Task> beforeCommit, IProgress<long>? progress, CancellationToken ct);
    Task SaveCopyAsync(string path, byte[] bytes, short mode, CancellationToken ct);
}

/// <summary>SFTP-only document protocol. A content check is not an atomic CAS; see REMOTE_EDIT.md.</summary>
public sealed class SftpDocumentTransport : IRemoteDocumentTransport
{
    readonly ServerSession session;
    public RemoteDocumentIdentity Identity { get; }
    public Guid SessionId => session.Id;
    public CancellationToken Lifetime => session.Lifetime;
    public bool Available => session.State == SessionState.Connected && session.SftpAvailable;
    public SftpDocumentTransport(ServerSession session) { this.session = session; Identity = RemoteDocumentIdentity.From(session); }
    void VerifyIdentity() { if (Identity != RemoteDocumentIdentity.From(session)) throw new IOException("Параметры сервера изменены. Документ не перенаправлен; подключитесь к исходному серверу."); }
    async Task<T> Run<T>(Func<SftpClient, CancellationToken, T> action, CancellationToken ct)
    {
        VerifyIdentity(); if (!Available) throw new IOException("Основная серверная сессия отключена. Возобновите документ после обычного подключения."); using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, Lifetime);
        using var lease = await session.LeaseSftpAsync(linked.Token).ConfigureAwait(false);
        using var cancel = linked.Token.Register(lease.Dispose);
        return await Task.Run(() => { linked.Token.ThrowIfCancellationRequested(); VerifyIdentity(); return action(lease.Client, linked.Token); }, linked.Token).ConfigureAwait(false);
    }
    public Task<DocumentLocation> LocateAsync(string path, bool followLink, CancellationToken ct) => Run((c, t) =>
    {
        path = PosixPath.Normalize(path, session.Files.HomeDirectory); var original = SftpProtocolAdapter.Get(c, path);
        if (original.IsSymbolicLink && !followLink) throw new IOException("Открытие цели ссылки требует явного согласия.");
        var target = c.Get(path); // Deliberate REALPATH, recorded and checked on every operation.
        if (!target.IsRegularFile || target.IsSymbolicLink) throw new IOException("FIFO, сокеты, устройства и каталоги не открываются как текст.");
        return new DocumentLocation(path, target.FullName, original.IsSymbolicLink ? SftpProtocolAdapter.ReadLink(c, path) : null);
    }, ct);
    static ISftpFile Validate(SftpClient c, DocumentLocation location)
    {
        var original = SftpProtocolAdapter.Get(c, location.Path);
        if (location.LinkText is not null)
        {
            if (!original.IsSymbolicLink || SftpProtocolAdapter.ReadLink(c, location.Path) != location.LinkText) throw new DocumentConflictException("Символическая ссылка изменена.");
        }
        else if (original.IsSymbolicLink) throw new DocumentConflictException("Вместо исходного файла появилась ссылка.");
        if (c.Get(location.Path).FullName != location.TargetPath) throw new DocumentConflictException("Изменилась согласованная цель пути.");
        var target = SftpProtocolAdapter.Get(c, location.TargetPath);
        if (!target.IsRegularFile || target.IsSymbolicLink) throw new DocumentConflictException("Тип назначения изменён.");
        return target;
    }
    static DocumentMetadata Metadata(ISftpFile f) => new(f.Length, f.LastWriteTimeUtc, RemoteFileService.ReadMode(f.Attributes), f.UserId, f.GroupId);
    static RemoteDocumentVersion Read(SftpClient c, string path, CancellationToken ct)
    {
        var f = SftpProtocolAdapter.Get(c, path);
        if (!f.IsRegularFile || f.IsSymbolicLink || f.Length > DocumentText.MaximumBytes) throw new IOException("Для редактирования нужен обычный файл не больше 32 МиБ.");
        using var input = c.OpenRead(path); using var output = new MemoryStream(); byte[] buffer = new byte[65536];
        while (true) { ct.ThrowIfCancellationRequested(); int count = input.Read(buffer); if (count == 0) break; if (output.Length + count > DocumentText.MaximumBytes) throw new IOException("Файл вырос больше 32 МиБ."); output.Write(buffer, 0, count); }
        var after = SftpProtocolAdapter.Get(c, path);
        if (!after.IsRegularFile || after.IsSymbolicLink || f.Length != after.Length || f.LastWriteTimeUtc != after.LastWriteTimeUtc) throw new DocumentConflictException("Файл менялся во время чтения. Повторите проверку.");
        return new(output.ToArray(), Metadata(after));
    }
    public Task<RemoteDocumentVersion> ReadAsync(DocumentLocation location, CancellationToken ct) => Run((c, t) => { Validate(c, location); return Read(c, location.TargetPath, t); }, ct);
    static void AssertExpected(RemoteDocumentVersion version, string hash, DocumentMetadata metadata)
    {
        if (version.Hash != hash || version.Metadata.Mode != metadata.Mode || version.Metadata.UserId != metadata.UserId || version.Metadata.GroupId != metadata.GroupId)
            throw new DocumentConflictException("Серверная версия или права изменены. Локальная правка сохранена.");
    }
    static short Octal(short mode) => Convert.ToInt16(Convert.ToString(mode & 0xfff, 8));
    public Task<RemoteDocumentVersion> ReplaceAsync(DocumentLocation location, string expectedHash, DocumentMetadata metadata, byte[] snapshot, Guid operation, Func<Task> beforeCommit, IProgress<long>? progress, CancellationToken ct) => Run((c, t) =>
    {
        Validate(c, location); AssertExpected(Read(c, location.TargetPath, t), expectedHash, metadata);
        var temporary = PosixPath.Combine(PosixPath.Parent(location.TargetPath), $".sshexplorer-edit-{operation:N}.tmp"); bool created = false;
        try
        {
            using (var output = c.Open(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                created = true; c.ChangePermissions(temporary, 600);
                for (int offset = 0; offset < snapshot.Length; offset += 65536) { t.ThrowIfCancellationRequested(); int count = Math.Min(65536, snapshot.Length - offset); output.Write(snapshot, offset, count); progress?.Report(offset + count); }
                output.Flush();
            }
            var f = SftpProtocolAdapter.Get(c, temporary);
            if (f.UserId != metadata.UserId || f.GroupId != metadata.GroupId) { f.UserId = metadata.UserId; f.GroupId = metadata.GroupId; f.UpdateStatus(); }
            c.ChangePermissions(temporary, Octal(metadata.Mode));
            var uploaded = Read(c, temporary, t); AssertExpected(uploaded, Convert.ToHexStringLower(SHA256.HashData(snapshot)), metadata);
            Validate(c, location); AssertExpected(Read(c, location.TargetPath, t), expectedHash, metadata);
            beforeCommit().GetAwaiter().GetResult(); t.ThrowIfCancellationRequested();
            // Unsupported POSIX rename fails safely. There is no delete+upload or silent backup fallback.
            SftpProtocolAdapter.PosixRename(c, temporary, location.TargetPath);
            Validate(c, location); var confirmed = Read(c, location.TargetPath, t);
            AssertExpected(confirmed, uploaded.Hash, metadata); return confirmed;
        }
        finally { if (created) try { var own = SftpProtocolAdapter.Get(c, temporary); if (own.IsRegularFile && !own.IsSymbolicLink) c.DeleteFile(temporary); } catch { /* Never roll back or remove the destination. */ } }
    }, ct);
    public async Task SaveCopyAsync(string path, byte[] bytes, short mode, CancellationToken ct)
    {
        if (bytes.Length > DocumentText.MaximumBytes) throw new IOException("Размер копии больше 32 МиБ.");
        await Run((c, t) =>
        {
            path = PosixPath.Normalize(path); PosixPath.ValidateName(PosixPath.Name(path));
            using (var output = c.Open(path, FileMode.CreateNew, FileAccess.Write)) { c.ChangePermissions(path, 600); for (int i = 0; i < bytes.Length; i += 65536) { t.ThrowIfCancellationRequested(); output.Write(bytes, i, Math.Min(65536, bytes.Length - i)); } output.Flush(); }
            c.ChangePermissions(path, Octal(mode));
            if (Read(c, path, t).Hash != Convert.ToHexStringLower(SHA256.HashData(bytes))) throw new IOException("Новая копия не прошла проверку. Проверьте назначение; исходный документ сохранён.");
            return true;
        }, ct).ConfigureAwait(false);
    }
}
