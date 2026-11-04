using System.IO;
using System.Windows;
using System.Windows.Media;
using SSHExplorer.Core;

namespace SSHExplorer.App;

public enum FileIconKind { Folder, Document, Text, Image, Audio, Video, Archive, Code, Pdf, Executable, Link, Special }

// Classification plus a neutral loading fallback. Actual file objects use ShellFileIcon.
public static class FileSystemIcons
{
    public static FileIconKind Kind(RemoteEntry entry)
    {
        if (entry.IsSymbolicLink) return FileIconKind.Link;
        if (entry.IsDirectory) return FileIconKind.Folder;
        if (!entry.IsRegularFile) return FileIconKind.Special;
        return Path.GetExtension(entry.Name).ToLowerInvariant() switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".svg" => FileIconKind.Image,
            ".mp3" or ".wav" or ".flac" or ".ogg" or ".m4a" => FileIconKind.Audio,
            ".mp4" or ".mkv" or ".webm" or ".avi" or ".mov" => FileIconKind.Video,
            ".zip" or ".gz" or ".bz2" or ".xz" or ".tar" or ".7z" or ".rar" => FileIconKind.Archive,
            ".cs" or ".py" or ".js" or ".ts" or ".json" or ".yaml" or ".yml" or ".xml" or ".html" or ".css" or ".sh" or ".toml" or ".conf" => FileIconKind.Code,
            ".txt" or ".md" or ".log" or ".csv" or ".ini" => FileIconKind.Text,
            ".pdf" => FileIconKind.Pdf,
            _ when (entry.Permissions & 0x49) != 0 => FileIconKind.Executable,
            _ => FileIconKind.Document
        };
    }
    static readonly Dictionary<bool, ImageSource> fallback = [];
    public static ImageSource For(RemoteEntry entry)
    {
        bool folder = entry.IsDirectory && !entry.IsSymbolicLink;
        if (fallback.TryGetValue(folder, out var image)) return image;
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 24, 24));
            dc.DrawGeometry(null, new Pen(Brushes.Gray, 1.2), Geometry.Parse(folder ? "M3,7 L3,20 21,20 21,7 12,7 10,4 3,4 Z" : "M6,3 L15,3 20,8 20,22 4,22 4,3 Z M15,3 L15,8 20,8"));
        }
        drawing.Freeze(); image = new DrawingImage(drawing); image.Freeze(); fallback[folder] = image; return image;
    }
}
