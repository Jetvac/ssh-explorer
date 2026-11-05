using System.Globalization;

namespace SSHExplorer.Core;

public enum ArchiveFormat { TarGz, TarXz, TarBz2, Tar, Zip }

/// <summary>How an archive is extracted: into a new folder (never overwrites) or into the archive's folder, skipping existing files.</summary>
public enum ExtractMode { NewFolder, HereKeepExisting }

/// <summary>Archive entries as the server lists them, with entries that would land outside the destination.</summary>
public sealed record ArchiveListing(IReadOnlyList<string> Entries, IReadOnlyList<string> Unsafe, bool Truncated);

/// <summary>
/// tar/zip on the server through the account's own tools (nothing installed). Names go in as <c>./name</c> so a leading
/// dash is never an option; the archive is written to a temporary name and moved into place only when complete and only
/// if the target name is still free, so an existing file is never replaced and a failure leaves no half-written archive.
/// </summary>
public static class Archives
{
    public const int ListLimit = 5000;
    public const string ToolsCommand = "for t in tar gzip xz bzip2 zip unzip; do command -v $t >/dev/null 2>&1 && printf '%s ' $t; done; echo";

    public static IReadOnlySet<string> ParseTools(string output) => output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);

    public static string Extension(ArchiveFormat format) => format switch
    {
        ArchiveFormat.TarGz => ".tar.gz", ArchiveFormat.TarXz => ".tar.xz", ArchiveFormat.TarBz2 => ".tar.bz2", ArchiveFormat.Tar => ".tar", _ => ".zip",
    };

    public static string Label(ArchiveFormat format) => format switch
    {
        ArchiveFormat.TarGz => "tar.gz (gzip)", ArchiveFormat.TarXz => "tar.xz (xz, сильнее, медленнее)", ArchiveFormat.TarBz2 => "tar.bz2 (bzip2)", ArchiveFormat.Tar => "tar (без сжатия)", _ => "zip",
    };

    /// <summary>Tools the server needs for a format (create and extract).</summary>
    public static string[] Requires(ArchiveFormat format, bool create) => format switch
    {
        ArchiveFormat.TarGz => ["tar", "gzip"], ArchiveFormat.TarXz => ["tar", "xz"], ArchiveFormat.TarBz2 => ["tar", "bzip2"], ArchiveFormat.Tar => ["tar"],
        _ => [create ? "zip" : "unzip"],
    };

    /// <summary>The missing tool for a format, or null when the server has everything.</summary>
    public static string? Missing(ArchiveFormat format, bool create, IReadOnlySet<string> tools) => Requires(format, create).FirstOrDefault(t => !tools.Contains(t));

    public static ArchiveFormat? Detect(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.EndsWith(".tar.gz", StringComparison.Ordinal) || n.EndsWith(".tgz", StringComparison.Ordinal)) return ArchiveFormat.TarGz;
        if (n.EndsWith(".tar.xz", StringComparison.Ordinal) || n.EndsWith(".txz", StringComparison.Ordinal)) return ArchiveFormat.TarXz;
        if (n.EndsWith(".tar.bz2", StringComparison.Ordinal) || n.EndsWith(".tbz2", StringComparison.Ordinal) || n.EndsWith(".tbz", StringComparison.Ordinal)) return ArchiveFormat.TarBz2;
        if (n.EndsWith(".tar", StringComparison.Ordinal)) return ArchiveFormat.Tar;
        if (n.EndsWith(".zip", StringComparison.Ordinal)) return ArchiveFormat.Zip;
        return null;
    }

    /// <summary>The archive's name without its archive extension: «site.tar.gz» → «site».</summary>
    public static string BaseName(string name)
    {
        foreach (var ext in new[] { ".tar.gz", ".tar.xz", ".tar.bz2", ".tgz", ".txz", ".tbz2", ".tbz", ".tar", ".zip" })
            if (name.Length > ext.Length && name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return name[..^ext.Length];
        return name;
    }

    /// <summary>A default archive name: the single selected name, or the folder's name for several.</summary>
    public static string SuggestName(string directory, IReadOnlyList<string> names, ArchiveFormat format)
    {
        var stem = names.Count == 1 ? names[0] : PosixPath.Name(directory) is { Length: > 0 } folder ? folder : "archive";
        return stem + Extension(format);
    }

    /// <summary>Null when the name is usable as a new file name in one folder; otherwise the reason.</summary>
    public static string? ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Укажите имя архива.";
        if (name.Contains('/') || name.Contains('\0')) return "Имя архива не может содержать «/».";
        if (name is "." or "..") return "Недопустимое имя архива.";
        if (name.StartsWith(".sshexplorer-", StringComparison.Ordinal)) return "Это имя зарезервировано для временных файлов.";
        return null;
    }

    static string Members(IEnumerable<string> names) => string.Join(" ", names.Select(n => PosixPath.Quote("./" + n)));

    /// <summary>
    /// Creates <paramref name="archive"/> in <paramref name="directory"/> from the given names (all in that directory).
    /// Exit 17: the target already exists — nothing was written.
    /// </summary>
    public static string CreateCommand(string directory, IReadOnlyList<string> names, string archive, ArchiveFormat format, string temporary)
    {
        if (names.Count == 0) throw new ArgumentException("Нечего архивировать.", nameof(names));
        foreach (var n in names) if (n.Contains('/') || n is "." or "..") throw new ArgumentException("Имя элемента недопустимо: " + n, nameof(names));
        if (ValidateName(archive) is { } problem) throw new ArgumentException(problem, nameof(archive));
        var target = PosixPath.Quote(archive); var part = PosixPath.Quote(temporary);
        var pack = format switch
        {
            ArchiveFormat.TarGz => $"tar -czf {part} {Members(names)}",
            ArchiveFormat.TarXz => $"tar -cJf {part} {Members(names)}",
            ArchiveFormat.TarBz2 => $"tar -cjf {part} {Members(names)}",
            ArchiveFormat.Tar => $"tar -cf {part} {Members(names)}",
            // -y stores links as links (never follows them out of the tree); -q keeps the output small.
            _ => $"zip -r -q -y {part} {Members(names)}",
        };
        // ln is atomic and fails when the name exists; where hard links are not supported, mv -n (never replaces) is the
        // fallback and a temporary file that is still there means the target appeared meanwhile.
        return $"cd {PosixPath.Quote(directory)} || exit 2; if [ -e {target} ] || [ -L {target} ]; then echo 'exists' >&2; exit 17; fi; " +
               $"rm -f {part}; {pack}; rc=$?; if [ $rc -ne 0 ]; then rm -f {part}; exit $rc; fi; " +
               $"if ln {part} {target} 2>/dev/null; then rm -f {part}; elif [ ! -e {target} ] && mv -n {part} {target} 2>/dev/null && [ ! -e {part} ]; then :; " +
               $"else rm -f {part}; echo 'exists' >&2; exit 17; fi";
    }

    /// <summary>Lists the archive's entry names (at most <see cref="ListLimit"/>, one more line marks truncation).</summary>
    public static string ListCommand(string archive, ArchiveFormat format)
    {
        var a = PosixPath.Quote(archive);
        var list = format switch
        {
            ArchiveFormat.TarGz => $"tar -tzf {a}", ArchiveFormat.TarXz => $"tar -tJf {a}", ArchiveFormat.TarBz2 => $"tar -tjf {a}", ArchiveFormat.Tar => $"tar -tf {a}",
            _ => $"unzip -Z1 {a}",
        };
        return $"LC_ALL=C {list} | head -n {(ListLimit + 1).ToString(CultureInfo.InvariantCulture)}";
    }

    public static ArchiveListing ParseListing(string output)
    {
        var entries = output.Replace("\r\n", "\n").Split('\n').Where(l => l.Length > 0).ToList();
        bool truncated = entries.Count > ListLimit; if (truncated) entries.RemoveRange(ListLimit, entries.Count - ListLimit);
        return new ArchiveListing(entries, entries.Where(IsUnsafe).ToArray(), truncated);
    }

    /// <summary>An entry that is absolute or climbs out with «..» would be written outside the destination.</summary>
    public static bool IsUnsafe(string entry)
    {
        var e = entry.Replace('\\', '/');
        return e.StartsWith('/') || e.Split('/').Any(part => part == "..");
    }

    /// <summary>
    /// Extracts into <paramref name="destination"/>. In <see cref="ExtractMode.NewFolder"/> the folder must not exist
    /// (exit 17) and is created; in <see cref="ExtractMode.HereKeepExisting"/> existing files are left as they are.
    /// Ownership from the archive is never applied and absolute or «..» paths are refused by the tools themselves.
    /// </summary>
    public static string ExtractCommand(string archive, ArchiveFormat format, string destination, ExtractMode mode)
    {
        var a = PosixPath.Quote(archive); var d = PosixPath.Quote(destination);
        var keep = mode == ExtractMode.HereKeepExisting;
        var tarKeep = keep ? " --skip-old-files" : "";
        var run = format switch
        {
            ArchiveFormat.TarGz => $"tar -xzf {a} -C {d} --no-same-owner{tarKeep}",
            ArchiveFormat.TarXz => $"tar -xJf {a} -C {d} --no-same-owner{tarKeep}",
            ArchiveFormat.TarBz2 => $"tar -xjf {a} -C {d} --no-same-owner{tarKeep}",
            ArchiveFormat.Tar => $"tar -xf {a} -C {d} --no-same-owner{tarKeep}",
            _ => $"unzip -q {(keep ? "-n" : "-o")} {a} -d {d}",
        };
        var prepare = mode == ExtractMode.NewFolder
            ? $"if [ -e {d} ] || [ -L {d} ]; then echo 'exists' >&2; exit 17; fi; mkdir -p {d} || exit 2; "
            : $"[ -d {d} ] || exit 2; ";
        return "LC_ALL=C; export LC_ALL; " + prepare + run;
    }
}
