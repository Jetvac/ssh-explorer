using System.IO;
using System.Windows.Media;
using SSHExplorer.Core;

namespace SSHExplorer.App.ViewModels;

public sealed class FileItemViewModel(RemoteEntry entry) : ObservableObject
{
    bool showExtensions = true, isCut;
    ImageSource? thumbnail;
    FileNameEdit? edit;
    public RemoteEntry Entry => entry;
    public FileNameEdit? Edit { get => edit; set => Set(ref edit, value); }
    public void UpdateEntry(RemoteEntry value) { entry = value; Notify(); }
    public string Name => entry.Name;
    public bool ShowExtensions { get => showExtensions; set { if (Set(ref showExtensions, value)) Raise(nameof(DisplayName)); } }
    public bool IsCut { get => isCut; set { if (Set(ref isCut, value)) Raise(nameof(Opacity)); } }
    public double Opacity => IsCut ? .45 : 1;
    public string DisplayName => ShowExtensions || entry.IsDirectory || entry.IsSymbolicLink ? entry.Name : Path.GetFileNameWithoutExtension(entry.Name);
    public FileIconKind IconKind => FileSystemIcons.Kind(entry);
    public ImageSource FileIcon => FileSystemIcons.For(entry);
    public bool CanPreview => entry.IsRegularFile && !entry.IsSymbolicLink;
    public string Type => entry.TypeName;
    public string Size => entry.SizeText;
    public DateTime? Modified => entry.LastWriteTimeUtc == DateTime.MinValue ? null : entry.LastWriteTimeUtc.ToLocalTime();
    public string Permissions => entry.PermissionsText;
    public string Owner => IdText(entry.UserId);
    public string Group => IdText(entry.GroupId);
    public string TileDetails => entry.TypeName + "\n" + entry.SizeText + "  ·  " + TimeText(entry.LastWriteTimeUtc);
    public ImageSource? Thumbnail { get => thumbnail; set => Set(ref thumbnail, value); }
    public bool LoadingThumbnail { get; set; }
    public void Notify() => Raise(null);
    public static bool IsImage(RemoteEntry e) => e.IsRegularFile && !e.IsSymbolicLink && new[] { ".png", ".jpg", ".jpeg" }.Contains(Path.GetExtension(e.Name), StringComparer.OrdinalIgnoreCase);
    public static string TimeText(DateTime time) => time == DateTime.MinValue ? "Недоступно" : time.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss");
    public static string IdText(int value) => value < 0 ? "Недоступно" : value.ToString();
}
