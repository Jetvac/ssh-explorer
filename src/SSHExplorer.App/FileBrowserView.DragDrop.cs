using System.Collections.Specialized;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using SSHExplorer.Core;
using SSHExplorer.App.ViewModels;

namespace SSHExplorer.App;

public sealed partial class FileBrowserView
{
    Control? dropTarget; TreeViewItem? hoverNode; DateTime hoverSince;
    Point? dragStart; bool preparingDrag;
    readonly Dictionary<string, StringCollection> preparedFiles = [];
    static readonly Dictionary<string, ClipboardToken> remoteClipboardTokens = [];
    async Task CopySelection(bool cut)
    {
        if (!viewModel.HasSelection || nameEdit?.IsActive == true) return;
        var selected = Selected(); if (selected.Count == 0) return; var payload = new RemoteClipboard(session, selected.Select(r => r.Entry.FullPath).ToArray(), cut, new WeakReference<FileBrowserView>(this));
        var data = RemoteData(payload); Clipboard.SetDataObject(data, false); var clipboardSequence = WindowsClipboard.CurrentSequence; foreach (var r in all) { r.IsCut = cut && selected.Contains(r); r.Notify(); }
        if (cut) { viewModel.Message = "Объекты вырезаны. Вставьте их в папку этого приложения. Для передачи в Windows используйте копирование."; return; }
        viewModel.Message = "Подготовка файлов для системного буфера Windows…";
        var files = await PrepareWindowsFiles(payload);
        if (files is not null)
        {
            if (WindowsClipboard.CurrentSequence != clipboardSequence) { viewModel.Message = "Подготовка завершена, но буфер обмена уже изменён. Повторите копирование, чтобы поместить файлы в Windows."; return; }
            data.SetFileDropList(files); Clipboard.SetDataObject(data, false); viewModel.Message = "Файлы скопированы. Можно вставить в Windows Explorer или серверную вкладку.";
        }
    }
    async Task Paste()
    {
        if (!CanPaste()) return;
        var data = Clipboard.GetDataObject(); var clipboardSequence = WindowsClipboard.CurrentSequence; if (data is null || string.IsNullOrEmpty(CurrentPath)) return;
        if (GetRemote(data) is RemoteClipboard remote)
        {
            var job = App.Transfers.Copy(remote.Session, remote.Paths, session, CurrentPath, remote.Cut); main.ShowTransfers(); await WaitAndRefresh(job);
            if (remote.Cut && job.State == TransferState.Completed && remote.SourceView?.TryGetTarget(out var sourceView) == true && !ReferenceEquals(sourceView, this) && !sourceView.disposed) await sourceView.RefreshAsync();
            if (job.State == TransferState.Completed && remote.Cut) { foreach (var r in all) { r.IsCut = false; r.Notify(); } if (WindowsClipboard.CurrentSequence == clipboardSequence) Clipboard.Clear(); }
        }
        else if (data.GetData(DataFormats.FileDrop) is string[] local) { var job = App.Transfers.Upload(session, local, CurrentPath); main.ShowTransfers(); await WaitAndRefresh(job); }
        else viewModel.Message = "В буфере нет файлов для вставки.";
    }
    async Task<StringCollection?> PrepareWindowsFiles(RemoteClipboard payload)
    {
        var signatures = new List<string>(); bool cacheable = true;
        foreach (var path in payload.Paths) { var e = await payload.Session.Files.StatAsync(path, lifetime.Token); if (!e.IsRegularFile || e.IsSymbolicLink) { cacheable = false; break; } signatures.Add($"{payload.Session.Id}|{e.FullPath}|{e.Length}|{e.LastWriteTimeUtc.Ticks}"); }
        var cacheKey = string.Join("\0", signatures.Order(StringComparer.Ordinal)); if (cacheable && preparedFiles.TryGetValue(cacheKey, out var ready) && ready.Cast<string>().All(File.Exists)) return ready;
        var temp = Path.Combine(App.Store.DirectoryPath, "TransferCache", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
        var job = App.Transfers.Download(payload.Session, payload.Paths, temp); await App.Transfers.WaitAsync(job, lifetime.Token); if (job.State != TransferState.Completed) { ReportTransfer(job); return null; }
        var paths = Directory.GetFileSystemEntries(temp); var files = new StringCollection(); files.AddRange(paths); if (cacheable) { if (preparedFiles.Count > 64) preparedFiles.Clear(); preparedFiles[cacheKey] = files; } return files;
    }
    void ConfigureDrag(Control control)
    {
        control.AllowDrop = true;
        control.PreviewMouseLeftButtonDown += (_, e) => dragStart = nameEdit?.IsActive != true && FindAncestor<TextBox>(e.OriginalSource as DependencyObject) is null && ItemAt(e.OriginalSource as DependencyObject) is not null ? e.GetPosition(control) : null;
        control.PreviewMouseMove += async (_, e) =>
        {
            if (nameEdit?.IsActive == true || lassoOwner is not null || preparingDrag || e.LeftButton != MouseButtonState.Pressed || dragStart is not Point start || (e.GetPosition(control) - start).Length < 10 || Selected().Count == 0) return;
            if (FindAncestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject) is not null) return;
            dragStart = null; preparingDrag = true;
            try
            {
                // Freeze the source before a destination tab activates. No file content is downloaded here.
                var entries = Selected().Select(r => r.Entry).ToArray();
                var payload = new RemoteClipboard(session, entries.Select(x => x.FullPath).ToArray(), false, new WeakReference<FileBrowserView>(this));
                var token = RegisterRemote(payload);
                var data = await NativeDragDrop.ShellVirtualDataObject.CreateAsync(session, entries, token, App.Transfers);
                viewModel.Message = "Перетащите в папку. В Windows и другую сессию — копирование; внутри сессии — перемещение, Ctrl — копирование.";
                if (Mouse.LeftButton != MouseButtonState.Pressed) { data.Cancel(); return; }
                NativeDragDrop.NativeFileDrag.Run(data);
                if (data.IsExtracting) { viewModel.Message = "Windows Explorer получает файлы. Прогресс и отмена доступны в очереди передач."; main.ShowTransfers(); }
                else if (data.Failure is { } failure) viewModel.Message = failure;
                else if (data.Job?.State == TransferState.Completed) viewModel.Message = "Передача файлов в Windows завершена.";
            }
            catch (Exception ex) { viewModel.Message = ErrorMessages.Describe(ex); }
            finally { preparingDrag = false; main.CancelDragFeedback(); }
        };
        control.DragOver += (_, e) => { var row = ItemAt(e.OriginalSource as DependencyObject); DropOver(e, row?.Entry.IsDirectory == true && !row.Entry.IsSymbolicLink ? row.Entry.FullPath : CurrentPath, control); };
        control.DragLeave += (_, _) => ClearDropTarget();
        control.Drop += async (_, e) =>
        {
            e.Handled = true; var row = ItemAt(e.OriginalSource as DependencyObject);
            var destination = row?.Entry.IsDirectory == true && !row.Entry.IsSymbolicLink ? row.Entry.FullPath : CurrentPath;
            var effect = CanDropTo(e.Data, destination, e.KeyStates); e.Effects = effect; ClearDropTarget();
            if (effect != DragDropEffects.None) await Ui.Guard(() => DropData(e.Data, destination, e.KeyStates));
        };
    }
    internal DragDropEffects DragEffect(IDataObject data, DragDropKeyStates keys)
        => CanDropTo(data, CurrentPath, keys);
    internal DragDropEffects CanDropTo(IDataObject data, string path, DragDropKeyStates keys)
    {
        if (creating || nameEdit?.IsBusy == true || string.IsNullOrEmpty(path) || !session.SftpAvailable || session.Lifetime.IsCancellationRequested) return DragDropEffects.None;
        if ((keys & (DragDropKeyStates.ControlKey | DragDropKeyStates.ShiftKey)) == (DragDropKeyStates.ControlKey | DragDropKeyStates.ShiftKey)) return DragDropEffects.None;
        if (GetRemote(data) is RemoteClipboard payload)
        {
            var sameUserEndpoint = HostTrustStore.NormalizeHost(payload.Session.Profile.Host) == HostTrustStore.NormalizeHost(session.Profile.Host) && payload.Session.Profile.Port == session.Profile.Port && payload.Session.Profile.Username == session.Profile.Username;
            if (sameUserEndpoint && payload.Paths.Any(p => PosixPath.IsSameOrDescendant(path, p) || PosixPath.Combine(path, PosixPath.Name(p)) == p)) return DragDropEffects.None;
            if ((keys & DragDropKeyStates.ShiftKey) != 0 && payload.Session.Id != session.Id) return DragDropEffects.None;
            return payload.Session.Id == session.Id && (keys & DragDropKeyStates.ControlKey) == 0 ? DragDropEffects.Move : DragDropEffects.Copy;
        }
        if ((keys & DragDropKeyStates.ShiftKey) != 0) return DragDropEffects.None;
        return data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
    }
    void ClearDropTarget()
    {
        if (dropTarget is not null) { Ui.SetIsDropTarget(dropTarget, false); dropTarget.ClearValue(BackgroundProperty); dropTarget.ClearValue(BorderBrushProperty); } dropTarget = null;
        hoverNode = null; NativeDragDrop.NativeFileDrag.InternalTargetSession = null;
    }
    void DropOver(DragEventArgs e, string path, Control control)
    {
        e.Effects = CanDropTo(e.Data, path, e.KeyStates);
        e.Handled = true; if (e.Effects != DragDropEffects.None)
        {
            var target = (Control?)FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) ?? FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) ?? control;
            if (dropTarget != target) { ClearDropTarget(); dropTarget = target; }
            NativeDragDrop.NativeFileDrag.InternalTargetSession = session.Id;
            Ui.SetIsDropTarget(target, true);
            target.SetResourceReference(BackgroundProperty, "DropTarget"); target.SetResourceReference(BorderBrushProperty, "Accent");
            viewModel.Message = $"{(e.Effects == DragDropEffects.Move ? "Переместить" : "Копировать")} в {session.Profile.Name}: {path}";
            if (control is TreeViewItem node) { if (hoverNode != node) { hoverNode = node; hoverSince = DateTime.UtcNow; } else if ((DateTime.UtcNow - hoverSince).TotalMilliseconds > 700) node.IsExpanded = true; }
            var scroller = FindAncestor<ScrollViewer>(control) ?? FindVisualChild<ScrollViewer>(control);
            if (scroller is not null) { var y = e.GetPosition(scroller).Y; if (y < 28) scroller.LineUp(); else if (y > scroller.ActualHeight - 28) scroller.LineDown(); }
        }
        else ClearDropTarget();
    }
    async Task DropData(IDataObject data, string path, DragDropKeyStates keys)
    {
        var effect = CanDropTo(data, path, keys); if (effect == DragDropEffects.None) return;
        TransferJob? job = null;
        if (GetRemote(data) is RemoteClipboard payload) job = App.Transfers.Copy(payload.Session, payload.Paths, session, path, effect == DragDropEffects.Move);
        else if (data.GetData(DataFormats.FileDrop) is string[] local) job = App.Transfers.Upload(session, local, path);
        if (job is not null)
        {
            main.ShowTransfers(); await WaitAndRefresh(job);
            if (effect == DragDropEffects.Move && job.State == TransferState.Completed && GetRemote(data)?.SourceView?.TryGetTarget(out var sourceView) == true && !ReferenceEquals(sourceView, this) && !sourceView.disposed) await sourceView.RefreshAsync();
        }
    }
    static FileItemViewModel? ItemAt(DependencyObject? source) => FindAncestor<DataGridRow>(source)?.Item as FileItemViewModel ?? FindAncestor<ListBoxItem>(source)?.DataContext as FileItemViewModel;
    static DataObject RemoteData(RemoteClipboard payload)
    {
        var token = RegisterRemote(payload);
        var data = new DataObject(); data.SetData("SSHExplorer.RemoteFiles", new MemoryStream(Encoding.UTF8.GetBytes(token), false), false); return data;
    }
    static string RegisterRemote(RemoteClipboard payload)
    {
        var token = Guid.NewGuid().ToString("N"); if (remoteClipboardTokens.Count >= 64) remoteClipboardTokens.Remove(remoteClipboardTokens.Keys.First()); remoteClipboardTokens[token] = new ClipboardToken(payload.Session.Id, new WeakReference<ServerSession>(payload.Session), payload.Paths.ToArray(), payload.Cut, payload.SourceView); return token;
    }
    static RemoteClipboard? GetRemote(IDataObject data)
    {
        // Only an opaque raw token crosses OLE. Live sessions are never serialized or deserialized.
        if (!data.GetDataPresent("SSHExplorer.RemoteFiles", false)) return null;
        var raw = data.GetData("SSHExplorer.RemoteFiles", false); var token = raw is MemoryStream stream ? Encoding.UTF8.GetString(stream.ToArray()).TrimEnd('\0') : raw as string;
        return token is not null && remoteClipboardTokens.TryGetValue(token, out var payload) && payload.Session.TryGetTarget(out var owner) && owner.State is not SessionState.Closing and not SessionState.Closed ? new RemoteClipboard(owner, payload.Paths, payload.Cut, payload.SourceView) : null;
    }
    bool CanPaste()
    {
        if (!viewModel.CanCreate || nameEdit?.IsActive == true) return false;
        try { return Clipboard.GetDataObject() is { } data && CanPasteData(data); }
        catch (System.Runtime.InteropServices.ExternalException) { return false; }
    }
    internal bool CanPasteData(IDataObject data)
    {
        if (!viewModel.CanCreate || session.Lifetime.IsCancellationRequested) return false;
        if (GetRemote(data) is { } remote)
            return remote.Paths.Length > 0 && remote.Session.Files.Available && !remote.Paths.Any(p =>
                remote.Session.Id == session.Id && (PosixPath.IsSameOrDescendant(CurrentPath, p) || remote.Cut && PosixPath.Combine(CurrentPath, PosixPath.Name(p)) == p));
        return data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 };
    }
    void DisposeDragDrop()
    {
        ClearDropTarget();
        foreach (var token in remoteClipboardTokens.Where(x => x.Value.SessionId == session.Id).Select(x => x.Key).ToArray()) remoteClipboardTokens.Remove(token);
        preparedFiles.Clear();
    }
    internal void CancelDragFeedback() { dragStart = null; ClearDropTarget(); }
    sealed record RemoteClipboard(ServerSession Session, string[] Paths, bool Cut, WeakReference<FileBrowserView>? SourceView = null);
    sealed record ClipboardToken(Guid SessionId, WeakReference<ServerSession> Session, string[] Paths, bool Cut, WeakReference<FileBrowserView>? SourceView);
}
