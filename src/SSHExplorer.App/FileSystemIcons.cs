using System.IO;
using System.Windows;
using System.Windows.Media;
using SSHExplorer.Core;

namespace SSHExplorer.App;

public enum FileIconKind { Folder, Document, Text, Image, Audio, Video, Archive, Code, Pdf, Executable, Link, Special }

// Small vector artwork for file objects, distinct from Fluent command glyphs.
// Frozen DrawingImages scale with DPI and never inspect/execute a remote file.
public static class FileSystemIcons
{
    static readonly Dictionary<(FileIconKind Kind, bool Link, bool Contrast, Color Ink, Color Paper), ImageSource> cache = [];
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
    public static ImageSource For(RemoteEntry entry)
    {
        var contrast = SystemParameters.HighContrast;
        var ink = contrast ? SystemColors.WindowTextColor : Colors.Transparent;
        var key = (Kind(entry), entry.IsSymbolicLink, contrast, ink, contrast ? SystemColors.WindowColor : Colors.Transparent);
        if (cache.TryGetValue(key, out var existing)) return existing;
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            // Invisible bounds keep every category at the same visual baseline.
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 24, 24));
            // A system-color plate keeps an icon readable on both active and
            // inactive High Contrast selection backgrounds.
            if (contrast) dc.DrawRoundedRectangle(SystemColors.WindowBrush, null, new Rect(0, 0, 24, 24), 2, 2);
            Brush ColorBrush(string value) => contrast ? new SolidColorBrush(ink) : (Brush)new BrushConverter().ConvertFromString(value)!;
            var color = ColorBrush(key.Item1 switch { FileIconKind.Folder => "#E9AB13", FileIconKind.Image => "#0078D4", FileIconKind.Audio => "#9460C8", FileIconKind.Video or FileIconKind.Pdf => "#D34E54", FileIconKind.Archive => "#B17D28", FileIconKind.Code => "#26845B", FileIconKind.Link => "#7867BF", FileIconKind.Special => "#69727A", _ => "#5693B9" });
            var white = contrast ? SystemColors.WindowBrush : Brushes.White;
            var pen = new Pen(color, 1.3);
            void Shape(string path, Brush fill, Pen? outline = null) => dc.DrawGeometry(fill, outline, Geometry.Parse(path));
            if (key.Item1 == FileIconKind.Folder)
            {
                Shape("M2,5 Q2,3 4,3 L9,3 11,5 20,5 Q22,5 22,7 L22,19 Q22,21 20,21 L4,21 Q2,21 2,19 Z", ColorBrush("#CE9310"));
                Shape("M2,8 L22,8 22,19 Q22,21 20,21 L4,21 Q2,21 2,19 Z", color);
                Shape("M3,9 L21,9 21,11 3,11 Z", contrast ? white : ColorBrush("#FFD978"));
            }
            else if (key.Item1 == FileIconKind.Special)
            {
                Shape("M8,2 L16,2 22,8 22,16 16,22 8,22 2,16 2,8 Z", color);
                Shape("M11,6 L13,6 13,14 11,14 Z M11,17 L13,17 13,19 11,19 Z", white);
            }
            else if (key.Item1 == FileIconKind.Link)
            {
                dc.DrawRoundedRectangle(color, null, new Rect(3, 3, 18, 18), 3, 3);
                Shape("M6,17 L6,12 Q6,8 11,8 L13,8 13,5 19,10 13,15 13,12 11,12 Q10,12 10,14 L10,17 Z", white);
            }
            else
            {
                Shape("M6,2 L15,2 21,8 21,22 4,22 4,4 Q4,2 6,2 Z", white, pen);
                Shape("M15,2 L15,8 21,8 Z", color);
                switch (key.Item1)
                {
                    case FileIconKind.Image:
                        dc.DrawRectangle(color, null, new Rect(6, 10, 13, 9));
                        Shape("M7,18 L11,13 14,16 16,14 18,18 Z", white); dc.DrawEllipse(white, null, new Point(16.5, 12), 1, 1); break;
                    case FileIconKind.Audio:
                        Shape("M12,10 L17,9 17,17 15,17 15,12 12,13 12,19 10,19 10,12 Z", color); dc.DrawEllipse(color, null, new Point(9, 19), 2, 1.6); dc.DrawEllipse(color, null, new Point(14, 17), 2, 1.6); break;
                    case FileIconKind.Video:
                        dc.DrawRoundedRectangle(color, null, new Rect(6, 10, 13, 9), 1, 1); Shape("M11,12 L16,14.5 11,17 Z", white); break;
                    case FileIconKind.Archive:
                        Shape("M11,3 L14,3 14,6 11,6 Z M11,8 L14,8 14,11 11,11 Z M11,13 L14,13 14,16 11,16 Z M10,18 L15,18 15,21 10,21 Z", color); break;
                    case FileIconKind.Code:
                        dc.DrawGeometry(null, new Pen(color, 1.8), Geometry.Parse("M10,11 L7,14 10,17 M15,11 L18,14 15,17 M13.5,10 L11.5,18")); break;
                    case FileIconKind.Executable:
                        dc.DrawRectangle(color, null, new Rect(6, 10, 13, 9)); dc.DrawGeometry(null, new Pen(white, 1.4), Geometry.Parse("M8,12 L10,14 8,16 M12,16 L16,16")); break;
                    case FileIconKind.Pdf:
                        dc.DrawRoundedRectangle(color, null, new Rect(6, 11, 13, 8), 1, 1); dc.DrawGeometry(null, new Pen(white, 1.5), Geometry.Parse("M8,17 L10,13 12,17 M9,16 L11,16 M14,13 L14,17 17,17")); break;
                    default:
                        for (int y = 11; y <= 18; y += 3) dc.DrawRectangle(color, null, new Rect(7, y, key.Item1 == FileIconKind.Text ? 11 : 8, 1)); break;
                }
            }
        }
        drawing.Freeze(); var source = new DrawingImage(drawing); source.Freeze(); cache[key] = source; return source;
    }
}
