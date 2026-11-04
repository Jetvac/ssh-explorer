using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using SSHExplorer.Core;

namespace SSHExplorer.App;

public sealed partial class MainWindow
{
    RemoteEditWorkspace? editWorkspace;
    RemoteDocumentsWindow? documentsWindow;
    internal RemoteEditWorkspace EditWorkspace => editWorkspace ??= new RemoteEditWorkspace(App.Store.DirectoryPath);
    internal IRemoteDocumentTransport DocumentTransport(RemoteEditSession document)
    {
        var connected = sessions.Values.Select(v => v.Session).Where(s => s.State == SessionState.Connected && s.SftpAvailable);
        foreach (var session in connected.OrderByDescending(s => s.Id == document.AttachedSessionId))
        { var transport = new SftpDocumentTransport(session); if (transport.Identity == document.Record.Identity) return transport; }
        throw new IOException("Откройте исходный профиль сервера и проверьте SSH host key обычным способом. Документ автоматически к другому серверу не подключается.");
    }
    public void ShowDocuments()
    {
        if (documentsWindow?.IsLoaded == true) { documentsWindow.Activate(); return; }
        documentsWindow = new RemoteDocumentsWindow(this, EditWorkspace) { Owner = this }; documentsWindow.Show();
    }
    internal async Task ClearCompletedDocumentsAsync()
    {
        if (!Ui.Confirm("Удалить рабочие копии и журналы только завершённых документов?\n\nНезавершённые и неподтверждённые документы сохранятся. Окна редактора не закрываются. Если копия ещё нужна, сначала сохраните её отдельно.")) return;
        var count = await EditWorkspace.ClearCompletedAsync(); MessageBox.Show($"Удалено завершённых рабочих копий: {count}.", "Редактируемые документы", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    public async Task EditRemoteTextAsync(ServerSession session, RemoteEntry entry, bool followLink = false)
    {
        if (!entry.IsRegularFile && !entry.IsSymbolicLink) throw new IOException("Выберите один обычный текстовый файл.");
        if (entry.Length > DocumentText.MaximumBytes) throw new IOException("Для редактирования доступны документы до 32 МиБ. Большой файл можно скачать и отправить через очередь передач.");
        var transport = new SftpDocumentTransport(session);
        var existing = EditWorkspace.Documents.FirstOrDefault(d => d.Record.State != RemoteEditState.Completed && d.Record.Identity == transport.Identity && d.Record.Location.Path == entry.FullPath);
        if (existing is null && !ConfirmDocumentOpen(session, entry)) return;
        RemoteEditSession document;
        using var preparing = CancellationTokenSource.CreateLinkedTokenSource(session.Lifetime);
        var loading = Dialogs.Window("Подготовка рабочей копии", 500, this);
        var loadingBody = new StackPanel(); var loadingText = Ui.Text($"Загрузка и проверка текста: {entry.Name}\n{Ui.Size(entry.Length)} · лимит 32 МиБ"); loadingText.TextWrapping = TextWrapping.Wrap; loadingBody.Children.Add(loadingText); loadingBody.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 4, Margin = new Thickness(0, 14, 0, 14) });
        loading.Content = Ui.DialogSurface(loadingBody, Ui.Button("Отмена", () => preparing.Cancel())); loading.Closing += (_, _) => preparing.Cancel(); loading.Show();
        try { document = await EditWorkspace.OpenAsync(transport, entry.FullPath, session.Profile.Name, followLink, ct: preparing.Token); }
        catch (OperationCanceledException) { return; }
        catch (IOException ex) when (ex.InnerException is System.Text.DecoderFallbackException)
        {
            var encoding = ChooseDocumentEncoding(); if (encoding is null) return;
            document = await EditWorkspace.OpenAsync(transport, entry.FullPath, session.Profile.Name, followLink, encoding);
        }
        finally { loading.Close(); }
        ShowDocuments(); documentsWindow!.Select(document); await OpenDocumentEditorAsync(document);
    }
    bool ConfirmDocumentOpen(ServerSession session, RemoteEntry entry)
    {
        var w = Dialogs.Window("Редактировать текст на сервере", 600, this); var body = new StackPanel { Margin = new Thickness(24) };
        body.Children.Add(Ui.DialogHeading("Редактирование в вашем редакторе"));
        var instruction = Ui.Text("Сохраняйте файл в редакторе — изменения будут отправлены на сервер."); instruction.TextWrapping = TextWrapping.Wrap; instruction.Margin = new Thickness(0, 12, 0, 12); body.Children.Add(instruction);
        var target = Ui.Text(session.Profile.Username + "@" + session.Profile.Endpoint + "\n" + entry.FullPath); target.TextWrapping = TextWrapping.Wrap; body.Children.Add(target);
        var limits = Ui.Text("Перед заменой проверяется содержимое и обычные права. ACL, дополнительные атрибуты и поведение жёстких ссылок этот способ не сохраняет. Если они важны, сохраните отдельную копию.", 12); limits.TextWrapping = TextWrapping.Wrap; limits.Margin = new Thickness(0, 12, 0, 12); body.Children.Add(limits);
        var details = Ui.Text("Несохранённый буфер редактора приложению недоступен. Внешнее «Сохранить как» подключается отдельно. При отключении редактор остаётся открыт. Между проверкой содержимого и заменой остаётся окно гонки внешнего писателя.", 12); details.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(new Expander { Header = "Как работает сохранение", Content = details });
        bool accepted = false; var open = Ui.Button("Открыть редактор", () => { accepted = true; w.DialogResult = true; }); open.SetResourceReference(StyleProperty, "PrimaryButtonStyle"); open.IsDefault = true;
        var cancel = Ui.Button("Отмена", () => w.DialogResult = false); cancel.IsCancel = true; w.Content = Ui.DialogSurface(body, cancel, open); w.ShowDialog(); return accepted;
    }
    internal async Task OpenDocumentEditorAsync(RemoteEditSession document)
    {
        try { await ExternalTextEditor.LaunchAsync(document.WorkingPath, App.Store.Settings.ExternalEditorPath); }
        catch (Exception ex)
        {
            var w = Dialogs.Window("Не удалось открыть редактор", 560);
            var content = new StackPanel(); content.Children.Add(Ui.Text(ErrorMessages.Describe(ex))); content.Children.Add(Ui.Text("Рабочая копия сохранена. Можно выбрать установленный текстовый редактор или сохранить файл отдельно."));
            w.Content = Ui.DialogSurface(content, Ui.Button("Закрыть", () => w.Close()), Ui.AsyncButton("Сохранить копию…", () => SaveDocumentLocally(document)), Ui.AsyncButton("Выбрать редактор…", async () => { if (!ChooseTextEditor()) return; await App.Store.SaveAsync(); w.Close(); await ExternalTextEditor.LaunchAsync(document.WorkingPath, App.Store.Settings.ExternalEditorPath); })); w.ShowDialog();
        }
    }
    internal static bool ChooseTextEditor()
    {
        var pick = new OpenFileDialog { Title = "Выберите доверенный установленный текстовый редактор (EXE)", Filter = "Приложение Windows|*.exe", CheckFileExists = true };
        if (pick.ShowDialog() != true) return false;
        if (pick.FileName.StartsWith(@"\\", StringComparison.Ordinal)) throw new IOException("Выберите локально установленный редактор.");
        App.Store.Settings.ExternalEditorPath = pick.FileName; return true;
    }
    internal static string? ChooseDocumentEncoding()
    {
        var w = Dialogs.Window("Кодировка текстового файла", 480); var p = new StackPanel();
        var text = Ui.Text("Автоматическое строгое чтение не удалось. Выберите известную кодировку. Клиент передаёт исходные байты без преобразования."); text.TextWrapping = TextWrapping.Wrap; p.Children.Add(text);
        var choices = new ComboBox { ItemsSource = new[] { "windows-1251", "windows-1252", "utf-8", "utf-16", "utf-16BE" }, SelectedIndex = 0, Margin = new Thickness(0, 12, 0, 0) }; p.Children.Add(choices);
        string? result = null; w.Content = Ui.DialogSurface(p, Ui.Button("Отмена", () => w.Close()), Ui.Button("Использовать", () => { result = (string)choices.SelectedItem; w.DialogResult = true; })); w.ShowDialog(); return result;
    }
    internal static async Task SaveDocumentLocally(RemoteEditSession document)
    {
        var save = new SaveFileDialog { Title = "Сохранить локальную копию", FileName = "document" + Path.GetExtension(document.WorkingPath), Filter = "Все файлы|*.*" };
        if (save.ShowDialog() != true) return;
        var source = File.Exists(document.WorkingPath) ? document.WorkingPath : document.SnapshotPath;
        var bytes = source == document.WorkingPath ? await document.ReadWorkingSnapshotAsync() : await File.ReadAllBytesAsync(source); await RemoteEditSession.AtomicWriteAsync(save.FileName, bytes);
    }
    internal async Task<bool> PrepareDocumentsForCloseAsync(Guid sessionId)
    {
        if (editWorkspace is null) return true;
        var documents = editWorkspace.Documents.Where(d => d.AttachedSessionId == sessionId && d.Record.State != RemoteEditState.Completed).ToArray();
        foreach (var document in documents) await document.CheckAsync();
        if (documents.Any(d => d.HasUnconfirmed))
        {
            int answer = 0; var w = Dialogs.Window("Есть неподтверждённые изменения", 620); var p = new StackPanel();
            var info = Ui.Text("Дождитесь подтверждения отправки либо оставьте рабочие копии и отключитесь. Несохранённые буферы внешнего редактора не видны; его окна останутся открыты."); info.TextWrapping = TextWrapping.Wrap; p.Children.Add(info);
            w.Content = Ui.DialogSurface(p, Ui.Button("Отмена", () => w.Close()), Ui.Button("Оставить копии и отключиться", () => { answer = 2; w.DialogResult = true; }), Ui.Button("Дождаться сохранения", () => { answer = 1; w.DialogResult = true; })); w.ShowDialog();
            if (answer == 0) return false;
            if (answer == 1)
            {
                foreach (var document in documents)
                { if (document.Record.State is RemoteEditState.Conflict or RemoteEditState.Unknown or RemoteEditState.FormatConfirmation) { ShowDocuments(); documentsWindow!.Select(document); return false; } await document.ResumeAsync(DocumentTransport(document)); }
                if (documents.Any(d => d.HasUnconfirmed)) { ShowDocuments(); return false; }
            }
        }
        foreach (var document in documents) await document.DetachAsync(); return true;
    }
}

internal sealed class RemoteDocumentsWindow : Window
{
    readonly MainWindow main;
    readonly RemoteEditWorkspace workspace;
    readonly DataGrid list = new();
    readonly TextBlock detail = Ui.Text("");
    readonly ScrollViewer detailScroller = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    readonly ProgressBar sendingProgress = new() { Height = 3, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 7), Maximum = 100 };
    readonly Dictionary<string, Button> commands = [];
    readonly Func<RemoteEditSession, IRemoteDocumentTransport> resolve;
    readonly Func<RemoteEditSession, Task> openEditor;
    RemoteEditSession? Selected => list.SelectedItem as RemoteEditSession;
    public RemoteDocumentsWindow(MainWindow main, RemoteEditWorkspace workspace, Func<RemoteEditSession, IRemoteDocumentTransport>? resolve = null, Func<RemoteEditSession, Task>? openEditor = null)
    {
        this.main = main; this.workspace = workspace; this.resolve = resolve ?? main.DocumentTransport; this.openEditor = openEditor ?? main.OpenDocumentEditorAsync; Title = "Редактируемые документы"; Width = 980; Height = 540; MinWidth = 700; MinHeight = 380; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Style = (Style)Application.Current.FindResource(typeof(Window));
        var root = new DockPanel(); root.SetResourceReference(Panel.BackgroundProperty, "Surface"); var heading = Ui.Text("Редактируемые документы", 20, true); heading.Margin = new Thickness(20, 16, 20, 10); DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        var bar = new ResponsiveCommandBar(More) { Margin = new Thickness(12, 0, 12, 8) };
        Add("open", "\uE70F", "Открыть редактор", () => this.openEditor(Require()), 90);
        Add("check", Ui.RefreshGlyph, "Проверить изменения", () => Require().CheckAsync(), 80);
        Add("send", Ui.UploadGlyph, "Отправить сейчас", Send, 100);
        Add("retry", Ui.RefreshGlyph, "Повторить / возобновить", Send, 70);
        Add("local", Ui.DownloadGlyph, "Сохранить локально", () => MainWindow.SaveDocumentLocally(Require()), 60);
        Add("finish", "\uE73E", "Завершить", Finish, 30);
        void Add(string key, string glyph, string title, Func<Task> action, int priority) { var b = Ui.AsyncCommandButton(glyph, title, action); commands[key] = b; bar.Add(key, b, () => Ui.AsyncMenu(title, action, glyph: glyph), priority); }
        DockPanel.SetDock(bar, Dock.Top); root.Children.Add(bar);
        var notice = Ui.Text("Ctrl+S в редакторе → обнаружение сохранённой копии → SFTP → подтверждённый хеш. Несохранённый буфер и внешнее «Сохранить как» автоматически не отправляются.", 12); notice.TextWrapping = TextWrapping.Wrap; notice.Margin = new Thickness(20, 0, 20, 8); DockPanel.SetDock(notice, Dock.Top); root.Children.Add(notice);
        var detailBody = new StackPanel(); detailBody.Children.Add(sendingProgress); detailBody.Children.Add(detail); detailScroller.Content = detailBody;
        var footer = new Border { Child = detailScroller, Padding = new Thickness(18, 12, 18, 12), MinHeight = 90, MaxHeight = 172 }; footer.SetResourceReference(StyleProperty, "StatusBarStyle"); detail.TextWrapping = TextWrapping.Wrap; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        SizeChanged += (_, _) => footer.MaxHeight = Math.Clamp(ActualHeight * .32, 100, 180);
        list.SetResourceReference(StyleProperty, "HomeTableStyle"); list.SelectionMode = DataGridSelectionMode.Single;
        MainWindow.AddColumn(list, "Сервер", "Record.ServerName", 170); MainWindow.AddColumn(list, "Удалённый путь", "Record.Location.Path", 330); MainWindow.AddColumn(list, "Состояние", "StatusText", 150);
        list.Columns[1].Width = new DataGridLength(1, DataGridLengthUnitType.Star); list.Columns[1].MinWidth = 240; list.Columns[2].Width = 190;
        list.SelectionChanged += (_, _) => RefreshDetails(); root.Children.Add(list); Content = root;
        workspace.Changed += WorkspaceChanged; Closed += (_, _) => workspace.Changed -= WorkspaceChanged; Refresh();
    }
    RemoteEditSession Require() => Selected ?? throw new IOException("Выберите документ.");
    void WorkspaceChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => Refresh());
    public void Select(RemoteEditSession document) { list.SelectedItem = document; list.ScrollIntoView(document); RefreshDetails(); }
    void Refresh()
    {
        if (!IsLoaded && list.ItemsSource is not null) return;
        var selected = Selected; var items = workspace.Documents.Where(d => d.Record.State != RemoteEditState.Completed).ToArray();
        if (list.Items.Count != items.Length) { list.ItemsSource = items; list.SelectedItem = selected ?? items.FirstOrDefault(); } else list.Items.Refresh();
        RefreshDetails();
    }
    static string StateName(RemoteEditState state) => state switch
    { RemoteEditState.Preparing => "Подготовка", RemoteEditState.Open => "Рабочая копия открыта", RemoteEditState.Pending => "Ожидает отправки", RemoteEditState.Uploading => "Отправка и проверка", RemoteEditState.Confirmed => "Сохранено на сервере", RemoteEditState.Conflict => "Конфликт версий", RemoteEditState.Error => "Ошибка", RemoteEditState.Paused => "Приостановлено", RemoteEditState.Unknown => "Результат неизвестен", RemoteEditState.FormatConfirmation => "Изменён формат", _ => "Завершено" };
    void RefreshDetails()
    {
        var document = Selected;
        sendingProgress.Visibility = document?.IsBusy == true ? Visibility.Visible : Visibility.Collapsed;
        sendingProgress.IsIndeterminate = document?.IsBusy == true && document.BytesTransferred == 0 && UiMotion.Enabled;
        sendingProgress.Value = document?.Record.SnapshotLength > 0 ? Math.Clamp(100d * document.BytesTransferred / document.Record.SnapshotLength, 0, 100) : 0;
        foreach (var (key, button) in commands) button.IsEnabled = document is not null && (!document.IsBusy || key == "local");
        if (document is null) { detail.Text = workspace.UnreadableJournals > 0 ? $"Есть непрочитанные журналы: {workspace.UnreadableJournals}. Исходные папки сохранены: {workspace.Root}" : "Нет открытых документов. Выберите текстовый файл и команду «Редактировать в Блокноте»."; return; }
        detail.Text = $"{StateName(document.Record.State)} · {document.Record.Identity.Display}\n{document.Record.Location.Path}{(document.Record.Location.Path != document.Record.Location.TargetPath ? " → " + document.Record.Location.TargetPath : "")}\n{document.Record.Message}\n{document.Record.Format.Description} · поколение {document.Record.Generation} · подтверждённый SHA-256 {document.Record.BaselineHash[..Math.Min(12, document.Record.BaselineHash.Length)]}";
        commands["send"].IsEnabled = !document.IsBusy && document.Record.State is not RemoteEditState.Conflict and not RemoteEditState.Unknown;
    }
    async Task Send()
    {
        var d = Require(); bool approve = d.Record.State == RemoteEditState.FormatConfirmation;
        if (approve && !Ui.Confirm(d.Record.Message + "\n\nОтправить именно эту сохранённую версию?")) return;
        await d.ResumeAsync(resolve(d), approve); Refresh();
    }
    async Task Finish()
    {
        var d = Require(); await d.CheckAsync();
        if (d.HasUnconfirmed) { if (!Ui.Confirm("Есть неподтверждённые изменения. Завершить отслеживание, оставив рабочую копию и журнал на диске?\n\nНесохранённый буфер внешнего редактора останется в его окне.")) return; }
        await d.CompleteAsync(d.HasUnconfirmed); Refresh();
    }
    ContextMenu More()
    {
        var menu = new ContextMenu(); var d = Selected;
        void Item(string name, Func<Task> action) { var item = Ui.AsyncMenu(name, action); item.IsEnabled = d is not null && !d.IsBusy; menu.Items.Add(item); }
        Item("Получить серверную версию…", async () => { var value = await d!.ReviewServerAsync(resolve(d)); var save = new SaveFileDialog { FileName = "server-version" + Path.GetExtension(d.WorkingPath) }; if (save.ShowDialog(this) == true) await RemoteEditSession.AtomicWriteAsync(save.FileName, value.Bytes); });
        Item("Заменить проверенную серверную версию…", async () => { var remote = await d!.ReviewServerAsync(resolve(d)); if (Ui.Confirm($"Заменить серверную версию {remote.Hash[..12]}?\n\n{d.Record.Identity.Display}\n{d.Record.Location.TargetPath}\n\nНовое внешнее изменение снова остановит замену.")) await d.ReplaceApprovedVersionAsync(remote); });
        Item("Сохранить на сервер рядом…", async () => { await d!.AttachForReviewAsync(resolve(d)); var path = Ui.Prompt("Отдельная серверная копия", "Полный новый POSIX-путь (существующий объект не заменяется)", d.Record.Location.TargetPath + ".edited"); if (path is not null) await d.SaveRemoteCopyAsync(path); });
        menu.Items.Add(new Separator());
        Item("Подключить копию из «Сохранить как»…", async () => { var pick = new OpenFileDialog { Title = "Выберите сохранённую локальную копию" }; if (pick.ShowDialog(this) == true && Ui.Confirm($"Использовать выбранную копию для документа?\n\n{d!.Record.Identity.Display}\n{d.Record.Location.Path}\n\nОтправка требует команды «Отправить сейчас».")) await d.UseLocalCopyAsync(pick.FileName); });
        var pause = Ui.AsyncMenu("Приостановить / отменить отправку", async () => await d!.DetachAsync()); pause.IsEnabled = d is not null; menu.Items.Add(pause);
        return menu;
    }
}
