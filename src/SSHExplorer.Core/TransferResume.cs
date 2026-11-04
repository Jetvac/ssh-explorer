using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace SSHExplorer.Core;

/// <summary>What an interrupted attempt left: the source identity at that time and how to delete the part.</summary>
internal sealed record PartRecord(long SourceLength, DateTime SourceModified, Func<Task> Delete);

/// <summary>
/// Resumable partial files. A part is named after the transfer pair (direction, source, destination), so a retry finds
/// exactly its own part. The job remembers the source's size and modification time when the part was kept; a retry
/// continues only when both are unchanged, the part is not longer than the source and its last 64 KiB equal the source
/// at the same offset. Anything else discards the part and sends the file from the start. Works after a dropped
/// connection too: nothing has to be written to the server at the moment of failure.
/// </summary>
public static class TransferResume
{
    const int Overlap = 64 * 1024;

    public static string PartName(string direction, string source, string destination)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(direction + "\n" + source + "\n" + destination));
        return ".sshexplorer-r-" + Convert.ToHexString(hash, 0, 12).ToLowerInvariant() + ".part";
    }

    /// <summary>A changed source, a refused write or a full disk cannot be continued; interruptions and cancellations can.</summary>
    public static bool ShouldRetain(Exception error) =>
        !(error is IOException io && (io.Message.StartsWith("Источник", StringComparison.Ordinal) || io.Message.Contains("space", StringComparison.OrdinalIgnoreCase) || io.Message.Contains("места", StringComparison.Ordinal)))
        && error is not (SftpPermissionDeniedException or UnauthorizedAccessException);

    static bool Unchanged(PartRecord? mark, long length, DateTime modified) => mark is not null && mark.SourceLength == length && mark.SourceModified == modified;

    /// <summary>Offset to continue an upload from, or 0 (an existing part of ours that cannot be continued is deleted).</summary>
    internal static long ResumableRemote(SftpClient client, string part, PartRecord? mark, long sourceLength, DateTime sourceModified, Stream source)
    {
        if (!RemoteFileService.Exists(client, part)) return 0;
        var file = RemoteFileService.Find(client, part);
        if (Unchanged(mark, sourceLength, sourceModified) && file.IsRegularFile && file.Length > 0 && file.Length <= sourceLength)
        {
            using var reader = client.OpenRead(part);
            if (TailMatches(reader, source, file.Length)) return file.Length;
        }
        try { file.Delete(); } catch (SshException) { }
        return 0;
    }

    /// <summary>Offset to continue a download from, or 0 (an existing part of ours that cannot be continued is deleted).</summary>
    internal static long ResumableLocal(string part, PartRecord? mark, long sourceLength, DateTime sourceModified, Stream source)
    {
        if (!File.Exists(part)) return 0;
        var length = new FileInfo(part).Length;
        if (Unchanged(mark, sourceLength, sourceModified) && length > 0 && length <= sourceLength)
        {
            using var reader = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (TailMatches(reader, source, length)) return length;
        }
        File.Delete(part);
        return 0;
    }

    static bool TailMatches(Stream part, Stream source, long length)
    {
        int size = (int)Math.Min(Overlap, length); long start = length - size;
        var a = new byte[size]; var b = new byte[size];
        part.Seek(start, SeekOrigin.Begin); part.ReadExactly(a);
        source.Seek(start, SeekOrigin.Begin); source.ReadExactly(b);
        source.Seek(0, SeekOrigin.Begin);
        return a.AsSpan().SequenceEqual(b);
    }

    public static string Size(long bytes)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"]; double value = bytes; int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return value.ToString(unit == 0 ? "0" : "0.#", CultureInfo.CurrentCulture) + " " + units[unit];
    }
}
