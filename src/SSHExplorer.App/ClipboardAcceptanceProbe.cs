using System.Collections.Specialized;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SSHExplorer.Core;

namespace SSHExplorer.App;

public sealed partial class MainWindow
{
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();

    // Isolated loopback test only: exercises the SAME actions as Ctrl+V/Ctrl+C with actual Windows CF_HDROP.
    // The previous clipboard is restored only if nobody has changed it since our last write.
    internal async Task RunClipboardProbeAsync()
    {
        var session = sessions.Values.First(v => v.Session.State == SessionState.Connected).Session;
        var browser = FindProbeVisual<FileBrowserView>(this) ?? throw new InvalidOperationException("Файловый экран не открыт.");
        var originalPath = browser.CurrentPath;
        var remote = PosixPath.Combine(session.Files.HomeDirectory, "sshx-clipboard-" + Guid.NewGuid().ToString("N"));
        var local = Path.Combine(App.Store.DirectoryPath, "ClipboardProbe"); Directory.CreateDirectory(local);
        var source = Path.Combine(local, "clipboard-fixture.txt");
        var bytes = Encoding.UTF8.GetBytes("SSH Explorer native CF_HDROP / Привет из Windows.\n");
        await File.WriteAllBytesAsync(source, bytes);
        var prior = Clipboard.GetDataObject(); uint ourSequence = 0;
        try
        {
            await session.Files.CreateDirectoryAsync(remote);
            await browser.NavigateAsync(remote);
            var data = new DataObject(); data.SetFileDropList(new StringCollection { source });
            Clipboard.SetDataObject(data, true); ourSequence = GetClipboardSequenceNumber();
            await InvokeProbeAction(browser, "Paste");
            var uploaded = await session.Files.ReadBytesAsync(PosixPath.Combine(remote, Path.GetFileName(source)), 1024 * 1024);
            if (ourSequence != GetClipboardSequenceNumber()) throw new InvalidOperationException("Пользователь изменил буфер во время проверки; дальнейшая запись отменена.");
            var grid = FindProbeVisual<DataGrid>(browser) ?? throw new InvalidOperationException("Таблица не открыта.");
            grid.SelectedItem = grid.Items.Cast<object>().First(item => item.GetType().GetProperty("Name")?.GetValue(item) as string == Path.GetFileName(source));
            var copyAction = InvokeProbeAction(browser, "CopySelection", false);
            byte[]? OurToken() => (Clipboard.GetDataObject()?.GetData("SSHExplorer.RemoteFiles") as MemoryStream)?.ToArray();
            var initialToken = OurToken(); ourSequence = GetClipboardSequenceNumber();
            await copyAction;
            var currentToken = OurToken();
            if (initialToken is null || currentToken is null || !initialToken.SequenceEqual(currentToken))
            { ourSequence = 0; throw new InvalidOperationException("Буфер изменён пользователем; его новое содержимое сохранено."); }
            ourSequence = GetClipboardSequenceNumber();
            var drop = Clipboard.GetFileDropList();
            var downloaded = drop.Count == 1 && File.Exists(drop[0]) ? await File.ReadAllBytesAsync(drop[0]!) : [];
            var output = Clipboard.GetDataObject();
            var opaqueToken = output?.GetData("SSHExplorer.RemoteFiles") is MemoryStream stream && stream.Length == 32;
            var uploadOk = SHA256.HashData(bytes).SequenceEqual(SHA256.HashData(uploaded));
            var downloadOk = bytes.SequenceEqual(downloaded);
            var report = new { WindowsCfHdropToRealSftp = uploadOk, RealSftpToWindowsCfHdrop = downloadOk, RealPreparedLocalFile = drop.Count == 1 && File.Exists(drop[0]), OpaqueTokenWithoutObjectSerialization = opaqueToken, ActualExplorerKeyboardOrDragTest = false, Passed = uploadOk && downloadOk && opaqueToken };
            await File.WriteAllTextAsync(Path.Combine(App.Store.DirectoryPath, "clipboard-probe.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            if (ourSequence != 0 && GetClipboardSequenceNumber() == ourSequence)
            {
                if (prior is not null) Clipboard.SetDataObject(prior, false); else Clipboard.Clear();
            }
            await session.Files.DeleteAsync(remote);
            await browser.NavigateAsync(originalPath);
        }
    }
    private static async Task InvokeProbeAction(FileBrowserView browser, string name, params object[] arguments)
    {
        var method = typeof(FileBrowserView).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance) ?? throw new MissingMethodException(name);
        if (method.Invoke(browser, arguments) is Task action) await action;
    }
    private static T? FindProbeVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); if (child is T found) return found;
            if (FindProbeVisual<T>(child) is { } nested) return nested;
        }
        return null;
    }
}
