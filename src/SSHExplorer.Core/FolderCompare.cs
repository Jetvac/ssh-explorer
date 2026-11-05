using System.Security.Cryptography;
using Renci.SshNet;

namespace SSHExplorer.Core;

/// <summary>A file or folder found on one side, by its path relative to the compared root ("/" separators).</summary>
public sealed record CompareEntry(string RelativePath, bool IsDirectory, long Length, DateTime ModifiedUtc);

public enum CompareKind { OnlyLocal, OnlyRemote, Different, Same, TypeConflict }

/// <summary>One line of the comparison. <see cref="Newer"/> says where the newer copy is when the times differ.</summary>
public sealed record CompareResult(string RelativePath, CompareKind Kind, CompareEntry? Local, CompareEntry? Remote, string Reason)
{
    public bool IsDirectory => (Local ?? Remote)!.IsDirectory;
    public string Newer => Local is { } l && Remote is { } r && !IsDirectory ? (l.ModifiedUtc - r.ModifiedUtc).TotalSeconds > FolderCompare.ToleranceSeconds ? "local" : (r.ModifiedUtc - l.ModifiedUtc).TotalSeconds > FolderCompare.ToleranceSeconds ? "remote" : "" : "";
}

public sealed record CompareReport(IReadOnlyList<CompareResult> Results, IReadOnlyList<string> Skipped, bool Truncated);

/// <summary>
/// Compares a Windows folder with a server folder. Read-only: nothing is copied or deleted here. Symbolic links and special
/// files are not followed and are listed as skipped. Times are compared with a 2-second tolerance (FAT and SFTP precision).
/// </summary>
public static class FolderCompare
{
    public const double ToleranceSeconds = 2;
    public const int MaxEntries = 20_000, MaxDepth = 32;

    public static IReadOnlyList<CompareResult> Compare(IEnumerable<CompareEntry> local, IEnumerable<CompareEntry> remote, ISet<string>? sameContent = null, ISet<string>? differentContent = null)
    {
        var l = local.ToDictionary(e => e.RelativePath, StringComparer.Ordinal); var r = remote.ToDictionary(e => e.RelativePath, StringComparer.Ordinal);
        var results = new List<CompareResult>();
        foreach (var path in l.Keys.Union(r.Keys).Order(StringComparer.Ordinal))
        {
            l.TryGetValue(path, out var a); r.TryGetValue(path, out var b);
            if (b is null) { results.Add(new(path, CompareKind.OnlyLocal, a, null, "Только на компьютере")); continue; }
            if (a is null) { results.Add(new(path, CompareKind.OnlyRemote, null, b, "Только на сервере")); continue; }
            if (a.IsDirectory != b.IsDirectory) { results.Add(new(path, CompareKind.TypeConflict, a, b, a.IsDirectory ? "На компьютере папка, на сервере файл" : "На компьютере файл, на сервере папка")); continue; }
            if (a.IsDirectory) { results.Add(new(path, CompareKind.Same, a, b, "Папка есть с обеих сторон")); continue; }
            if (differentContent?.Contains(path) == true) { results.Add(new(path, CompareKind.Different, a, b, "Содержимое различается (SHA-256)")); continue; }
            if (a.Length != b.Length) { results.Add(new(path, CompareKind.Different, a, b, "Размер различается")); continue; }
            if (sameContent?.Contains(path) == true) { results.Add(new(path, CompareKind.Same, a, b, "Содержимое совпадает (SHA-256)")); continue; }
            var delta = (a.ModifiedUtc - b.ModifiedUtc).TotalSeconds;
            if (Math.Abs(delta) > ToleranceSeconds) { results.Add(new(path, CompareKind.Different, a, b, delta > 0 ? "Изменён позже на компьютере" : "Изменён позже на сервере")); continue; }
            results.Add(new(path, CompareKind.Same, a, b, "Размер и время совпадают"));
        }
        return results;
    }

    public static (IReadOnlyList<CompareEntry> Entries, IReadOnlyList<string> Skipped, bool Truncated) ListLocal(string root, CancellationToken ct = default)
    {
        var entries = new List<CompareEntry>(); var skipped = new List<string>(); bool truncated = false;
        void Walk(DirectoryInfo dir, string prefix, int depth)
        {
            if (depth > MaxDepth) { skipped.Add(prefix + " — слишком глубокая вложенность"); return; }
            IEnumerable<FileSystemInfo> children;
            try { children = dir.EnumerateFileSystemInfos().OrderBy(c => c.Name, StringComparer.Ordinal).ToArray(); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { skipped.Add((prefix.Length == 0 ? "." : prefix) + " — " + ex.Message); return; }
            foreach (var child in children)
            {
                ct.ThrowIfCancellationRequested();
                if (entries.Count >= MaxEntries) { truncated = true; return; }
                var relative = prefix.Length == 0 ? child.Name : prefix + "/" + child.Name;
                if ((child.Attributes & FileAttributes.ReparsePoint) != 0) { skipped.Add(relative + " — ссылка или точка повторной обработки не обходится"); continue; }
                if (child is DirectoryInfo d) { entries.Add(new(relative, true, 0, d.LastWriteTimeUtc)); Walk(d, relative, depth + 1); }
                else if (child is FileInfo f) entries.Add(new(relative, false, f.Length, f.LastWriteTimeUtc));
            }
        }
        Walk(new DirectoryInfo(root), "", 0);
        return (entries, skipped, truncated);
    }

    internal static (IReadOnlyList<CompareEntry> Entries, IReadOnlyList<string> Skipped, bool Truncated) ListRemote(SftpClient client, string root, CancellationToken ct = default)
    {
        var entries = new List<CompareEntry>(); var skipped = new List<string>(); bool truncated = false;
        void Walk(string dir, string prefix, int depth)
        {
            if (depth > MaxDepth) { skipped.Add(prefix + " — слишком глубокая вложенность"); return; }
            IEnumerable<Renci.SshNet.Sftp.ISftpFile> children;
            try { children = client.ListDirectory(dir).Where(c => c.Name is not "." and not "..").OrderBy(c => c.Name, StringComparer.Ordinal).ToArray(); }
            catch (Exception ex) when (ex is Renci.SshNet.Common.SftpPermissionDeniedException or Renci.SshNet.Common.SshException) { skipped.Add((prefix.Length == 0 ? "." : prefix) + " — " + ErrorMessages.Describe(ex)); return; }
            foreach (var child in children)
            {
                ct.ThrowIfCancellationRequested();
                if (entries.Count >= MaxEntries) { truncated = true; return; }
                var relative = prefix.Length == 0 ? child.Name : prefix + "/" + child.Name;
                if (child.IsSymbolicLink) { skipped.Add(relative + " — символическая ссылка не обходится"); continue; }
                if (child.IsDirectory) { entries.Add(new(relative, true, 0, child.LastWriteTimeUtc)); Walk(child.FullName, relative, depth + 1); }
                else if (child.IsRegularFile) entries.Add(new(relative, false, child.Length, child.LastWriteTimeUtc));
                else skipped.Add(relative + " — специальный файл (сокет, устройство, FIFO)");
            }
        }
        Walk(root, "", 0);
        return (entries, skipped, truncated);
    }

    /// <summary>Lowercase SHA-256 of a local file.</summary>
    public static string LocalHash(string path) { using var s = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant(); }

    /// <summary>
    /// Parses <c>sha256sum</c> output run inside the compared root ("hash  relative/path"). Lines that sha256sum escapes
    /// (names with a newline or backslash start with "\") are left out, so such files count as different — never as equal.
    /// </summary>
    public static Dictionary<string, string> ParseRemoteHashes(string output)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 67 || line[64] != ' ' || line[65] is not (' ' or '*') || !line[..64].All(Uri.IsHexDigit)) continue;
            result[line[66..].TrimEnd('\r')] = line[..64].ToLowerInvariant();
        }
        return result;
    }
}

/// <summary>Runs a comparison against a live session (listing over SFTP, optional SHA-256 via sha256sum).</summary>
public sealed class FolderComparer(ServerSession session)
{
    public async Task<CompareReport> CompareAsync(string localRoot, string remoteRoot, bool byContent, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        progress?.Report("Чтение папки на компьютере…");
        var local = await Task.Run(() => FolderCompare.ListLocal(localRoot, ct), ct).ConfigureAwait(false);
        progress?.Report("Чтение папки на сервере…");
        (IReadOnlyList<CompareEntry> Entries, IReadOnlyList<string> Skipped, bool Truncated) remote;
        using (var lease = await session.LeaseSftpAsync(ct).ConfigureAwait(false)) remote = await Task.Run(() => FolderCompare.ListRemote(lease.Client, remoteRoot, ct), ct).ConfigureAwait(false);
        HashSet<string>? same = null, different = null;
        if (byContent)
        {
            var candidates = FolderCompare.Compare(local.Entries, remote.Entries).Where(r => r.Kind != CompareKind.OnlyLocal && r.Kind != CompareKind.OnlyRemote && !r.IsDirectory && r.Local!.Length == r.Remote!.Length).Select(r => r.RelativePath).ToArray();
            same = new(StringComparer.Ordinal); different = new(StringComparer.Ordinal);
            foreach (var chunk in candidates.Chunk(200))
            {
                progress?.Report($"Сравнение содержимого: {same.Count + different.Count} из {candidates.Length}…");
                var command = "cd " + PosixPath.Quote(remoteRoot) + " && sha256sum -- " + string.Join(' ', chunk.Select(p => PosixPath.Quote(p))) + " 2>/dev/null";
                var output = (await session.ExecuteLongAsync(command, TimeSpan.FromMinutes(10), ct).ConfigureAwait(false)).Output;
                var hashes = FolderCompare.ParseRemoteHashes(output);
                foreach (var path in chunk)
                {
                    var localHash = await Task.Run(() => FolderCompare.LocalHash(Path.Combine(localRoot, path.Replace('/', Path.DirectorySeparatorChar))), ct).ConfigureAwait(false);
                    if (hashes.TryGetValue(path, out var remoteHash) && remoteHash == localHash) same.Add(path); else different.Add(path);
                }
            }
        }
        progress?.Report("Готово");
        return new CompareReport(FolderCompare.Compare(local.Entries, remote.Entries, same, different), [.. local.Skipped.Select(s => "Компьютер: " + s), .. remote.Skipped.Select(s => "Сервер: " + s)], local.Truncated || remote.Truncated);
    }
}
