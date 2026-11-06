using System.Windows;
using System.Windows.Controls;
using SSHExplorer.Core;

namespace SSHExplorer.App;

/// <summary>0.5: imports of remote desktop connections and Home folder defaults (mRemoteNG-style inheritance).</summary>
public sealed partial class MainWindow
{
    /// <summary>Test hooks: the .rdp files to read, and the answer of the mRemoteNG password question.</summary>
    internal Func<string[]?>? RdpFilesOverride;
    internal Func<MRemoteNgImport, bool, (bool Import, string? Password)?>? MRemoteNgPasswordOverride;
    internal Func<FolderSettingsWindow, bool?>? FolderWindowOverride;

    internal async Task ImportRdpFilesAsync()
    {
        if (profileEditorOpen || closing) return;
        var files = RdpFilesOverride?.Invoke();
        if (RdpFilesOverride is null)
        {
            var pick = new Microsoft.Win32.OpenFileDialog { Title = "Файлы подключения к удалённому рабочему столу", Filter = "Подключение к удалённому рабочему столу (*.rdp)|*.rdp|Все файлы|*.*", CheckFileExists = true, Multiselect = true };
            if (pick.ShowDialog(this) != true) return;
            files = pick.FileNames;
        }
        if (files is not { Length: > 0 }) return;
        var candidates = new List<ImportCandidate>(); var skipped = new List<string>();
        foreach (var file in files)
        {
            try
            {
                var info = new System.IO.FileInfo(file);
                if (info.Length > 1024 * 1024) { skipped.Add(System.IO.Path.GetFileName(file) + ": файл слишком большой для .rdp."); continue; }
                var values = RdpFile.Parse(RdpFile.Decode(await System.IO.File.ReadAllBytesAsync(file, windowLifetime.Token)));
                if (!values.ContainsKey("full address")) { skipped.Add(System.IO.Path.GetFileName(file) + ": в файле нет адреса сервера (full address)."); continue; }
                candidates.Add(RdpFile.ToCandidate(System.IO.Path.GetFileNameWithoutExtension(file), values, Environment.UserName, App.Store.Profiles.Concat(candidates.Select(c => c.Profile)).ToArray()));
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { skipped.Add(System.IO.Path.GetFileName(file) + ": " + ErrorMessages.Describe(ex)); }
        }
        await ImportProfilesAsync(files.Length == 1 ? files[0] : $"Файлы .rdp: {files.Length}", candidates, skipped);
    }

    /// <summary>Servers remembered by the Windows client (read only; it stores no passwords there).</summary>
    internal async Task ImportRdpHistoryAsync()
    {
        if (profileEditorOpen || closing) return;
        var recent = new List<string>(); var users = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using (var mru = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Terminal Server Client\Default", writable: false))
                if (mru is not null) foreach (var name in mru.GetValueNames().Where(n => n.StartsWith("MRU", StringComparison.OrdinalIgnoreCase)).OrderBy(n => n, StringComparer.Ordinal)) if (mru.GetValue(name) is string address) recent.Add(address);
            using var servers = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Terminal Server Client\Servers", writable: false);
            if (servers is not null)
                foreach (var name in servers.GetSubKeyNames()) { using var key = servers.OpenSubKey(name, writable: false); users[name] = key?.GetValue("UsernameHint") as string; }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException) { Ui.Error("Не удалось прочитать историю подключений: " + ErrorMessages.Describe(ex)); return; }
        if (recent.Count == 0 && users.Count == 0) { Ui.Info("«Подключение к удалённому рабочему столу» ещё не запоминало серверов на этом компьютере."); return; }
        await ImportProfilesAsync("Подключение к удалённому рабочему столу · история", RdpFile.FromHistory(recent, users, Environment.UserName, App.Store.Profiles), []);
    }

    /// <summary>
    /// mRemoteNG: read without passwords first; if the file has passwords or is encrypted as a whole, ask whether to
    /// import them and for the mRemoteNG password (empty — its built-in one), then read again with that answer.
    /// </summary>
    internal async Task ImportMRemoteNgAsync()
    {
        if (profileEditorOpen || closing) return;
        var path = MRemoteNgPathOverride?.Invoke() ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "mRemoteNG", "confCons.xml");
        if (MRemoteNgPathOverride is null)
        {
            var pick = new Microsoft.Win32.OpenFileDialog { Title = "Подключения mRemoteNG", Filter = "Подключения mRemoteNG (confCons.xml)|*.xml|Все файлы|*.*", CheckFileExists = true };
            if (System.IO.File.Exists(path)) { pick.InitialDirectory = System.IO.Path.GetDirectoryName(path); pick.FileName = System.IO.Path.GetFileName(path); }
            if (pick.ShowDialog(this) != true) return;
            path = pick.FileName;
        }
        var info = new System.IO.FileInfo(path);
        if (!info.Exists || info.Length > 64L * 1024 * 1024) { Ui.Error(info.Exists ? "Файл слишком большой для списка подключений." : "Файл не найден: " + path); return; }
        var xml = await System.IO.File.ReadAllTextAsync(path, windowLifetime.Token);
        var result = MRemoteNg.Read(xml, Environment.UserName, App.Store.Profiles);
        string? filePassword = null, problem = null;
        (bool Import, string? Password)? Ask(bool fileLocked) => MRemoteNgPasswordOverride is { } decide ? decide(result, fileLocked) : AskMRemoteNgPassword(result, fileLocked, problem);
        // 1. A file encrypted as a whole needs its password before anything can be listed.
        while (result.NeedsPassword && result.Candidates.Count == 0)
        {
            if (Ask(true) is not { } answer) return;
            filePassword = answer.Password;
            result = MRemoteNg.Read(xml, Environment.UserName, App.Store.Profiles, password: filePassword);
            if (result.NeedsPassword && result.Candidates.Count == 0) { if (MRemoteNgPasswordOverride is not null) { Ui.Error(result.Skipped.FirstOrDefault() ?? "Пароль mRemoteNG не подходит."); return; } problem = "Пароль mRemoteNG не подходит. Проверьте раскладку и регистр."; }
        }
        // 2. Saved passwords inside are imported only when asked for.
        problem = null;
        while (result.HasPasswords && result.Secrets.Count == 0 && result.FolderSecrets.Count == 0)
        {
            if (Ask(false) is not { Import: true } answer) break;
            var next = MRemoteNg.Read(xml, Environment.UserName, App.Store.Profiles, importPasswords: true, password: answer.Password ?? filePassword);
            if (next.NeedsPassword) { if (MRemoteNgPasswordOverride is not null) break; problem = "Пароль mRemoteNG не подходит. Проверьте раскладку и регистр."; continue; }
            result = next; break;
        }
        if (result.Candidates.Count == 0 && result.Skipped.Count > 0 && result.Skipped[0].StartsWith("Файл", StringComparison.Ordinal)) { Ui.Error(result.Skipped[0]); return; }
        await ImportProfilesAsync("mRemoteNG · " + path, result.Candidates, result.Skipped, null, result.Secrets, result.Folders, result.FolderSecrets);
    }

    (bool Import, string? Password)? AskMRemoteNgPassword(MRemoteNgImport file, bool fileLocked, string? problem)
    {
        var w = Dialogs.Window("Пароли mRemoteNG", 560, this); var p = new StackPanel { Margin = new Thickness(24) };
        p.Children.Add(Ui.DialogHeading(fileLocked ? "Файл mRemoteNG зашифрован" : "Импортировать пароли?"));
        var text = Ui.Text(fileLocked
            ? "Файл зашифрован целиком паролем mRemoteNG. Введите его, чтобы прочитать подключения."
            : "В файле есть сохранённые пароли. По умолчанию они не переносятся. Если отметить импорт, они будут расшифрованы и сохранены с защитой Windows для вашей учётной записи — нигде не показываются.");
        text.TextWrapping = TextWrapping.Wrap; text.Margin = new Thickness(0, 10, 0, 12); p.Children.Add(text);
        var import = new CheckBox { Content = "Импортировать пароли", IsChecked = problem is not null, Margin = new Thickness(0, 0, 0, 10), Name = "MRemoteNgImportPasswords" };
        if (!fileLocked) p.Children.Add(import);
        var label = Ui.Text(fileLocked ? "Пароль mRemoteNG («Параметры» → «Безопасность»)." : "Пароль mRemoteNG («Параметры» → «Безопасность»). Оставьте пустым, если свой пароль не задавали или файл уже открыт им.", 12); label.TextWrapping = TextWrapping.Wrap; label.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); p.Children.Add(label);
        var secret = new SecretEntry("Пароль mRemoteNG") { Margin = new Thickness(0, 8, 0, 6) }; p.Children.Add(secret);
        if (problem is not null) { var error = Ui.Text(problem, 12); error.SetResourceReference(StyleProperty, "ValidationMessageStyle"); p.Children.Add(error); }
        void Sync() => secret.IsEnabled = fileLocked || import.IsChecked == true;
        import.Checked += (_, _) => Sync(); import.Unchecked += (_, _) => Sync(); Sync();
        var ok = Ui.Button(fileLocked ? "Открыть" : "Продолжить", () => w.DialogResult = true); ok.IsDefault = true; ok.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        var cancel = Ui.Button(fileLocked ? "Отмена" : "Без паролей", () => w.DialogResult = false); cancel.IsCancel = true;
        w.Content = Ui.DialogSurface(p, cancel, ok); w.Loaded += (_, _) => secret.FocusInput();
        try
        {
            var shown = ModalRunner.Show(w, this, windowLifetime.Token);
            if (!shown) return fileLocked ? null : (false, null);
            return (fileLocked || import.IsChecked == true, secret.Password.Length > 0 ? secret.Password : null);
        }
        finally { secret.Clear(); }
    }

    /// <summary>Imported folder values join the Home folders; an existing folder keeps what it already has.</summary>
    async Task SaveImportedFoldersAsync(IReadOnlyList<FolderDefaults> folders, IReadOnlyDictionary<Guid, string> secrets, IReadOnlyCollection<ServerProfile> accepted)
    {
        bool changed = false;
        foreach (var folder in folders)
        {
            if (!accepted.Any(p => ProfileInheritance.Normalize(p.Group).StartsWith(ProfileInheritance.Normalize(folder.Path), StringComparison.CurrentCultureIgnoreCase))) continue;
            var existing = App.Store.Settings.Folders.FirstOrDefault(f => string.Equals(ProfileInheritance.Normalize(f.Path), ProfileInheritance.Normalize(folder.Path), StringComparison.CurrentCultureIgnoreCase));
            var target = existing ?? folder.Clone();
            if (existing is null) { target.HasPassword = false; App.Store.Settings.Folders.Add(target); }
            else { if (target.Username.Length == 0) target.Username = folder.Username; if (target.Domain.Length == 0) target.Domain = folder.Domain; target.Rdp ??= folder.Rdp?.Clone(); }
            if (!target.HasPassword && secrets.TryGetValue(folder.Id, out var password)) { await App.Store.SaveSecretAsync(target.Id, password, windowLifetime.Token); target.HasPassword = true; }
            changed = true;
        }
        if (changed) await App.Store.SaveAsync(windowLifetime.Token);
    }

    /// <summary>«Параметры папки…»: the user, domain, password and RDP options the folder's servers may inherit.</summary>
    internal async Task EditFolderAsync(string group)
    {
        group = ProfileInheritance.Normalize(group);
        if (group.Length == 0) { Ui.Info("Сервер не в папке. Укажите «Группу на «Главной»» в его профиле."); return; }
        var folders = App.Store.Settings.Folders;
        var existing = folders.FirstOrDefault(f => string.Equals(ProfileInheritance.Normalize(f.Path), group, StringComparison.CurrentCultureIgnoreCase));
        var members = App.Store.Profiles.Where(p => ProfileInheritance.Normalize(p.Group).StartsWith(group, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        var window = new FolderSettingsWindow(group, existing, members) { Owner = this };
        if ((FolderWindowOverride?.Invoke(window) ?? ModalRunner.Show(window, this, windowLifetime.Token)) != true) return;
        var result = window.Result!;
        if (existing is null) { result.Path = group; folders.Add(result); }
        else { existing.Username = result.Username; existing.Domain = result.Domain; existing.Rdp = result.Rdp; existing.RecordHistory = result.RecordHistory; existing.CustomMetrics = result.CustomMetrics; result = existing; }
        if (window.NewPassword is { Length: > 0 } password) { await App.Store.SaveSecretAsync(result.Id, password, windowLifetime.Token); result.HasPassword = true; }
        else if (window.ClearPassword) { try { App.Store.DeleteSecret(result.Id); } catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { } result.HasPassword = false; }
        window.ForgetPassword();
        await App.Store.SaveAsync(windowLifetime.Token);
        RefreshHome();
    }
}

/// <summary>Values a Home folder gives to the servers that inherit them.</summary>
public sealed class FolderSettingsWindow : Window
{
    readonly TextBox user = new() { MinHeight = 34 }, domain = new() { MinHeight = 34 };
    readonly SecretEntry password = new("Пароль папки") { MinHeight = 34 };
    readonly CheckBox clear = new() { Content = "Удалить сохранённый пароль папки", Margin = new Thickness(0, 6, 0, 0) };
    readonly ComboBox rdpFrom = new() { MinHeight = 34 };
    readonly CheckBox recordHistory = new() { Name = "FolderRecordHistory", Content = "Записывать историю метрик серверов папки в фоне", Margin = new Thickness(0, 12, 0, 0) };
    readonly Button customMetrics = new() { Name = "FolderCustomMetrics", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
    List<CustomMetricDefinition> metrics;
    internal CheckBox RecordHistoryBox => recordHistory;
    readonly ServerProfile[] rdpSources;
    readonly FolderDefaults? existing;
    public FolderDefaults? Result { get; private set; }
    internal string? NewPassword { get; private set; }
    internal bool ClearPassword { get; private set; }
    internal TextBox UserBox => user;
    internal TextBox DomainBox => domain;
    internal SecretEntry PasswordBox => password;
    internal ComboBox RdpFrom => rdpFrom;
    internal string Summary { get; }

    public FolderSettingsWindow(string path, FolderDefaults? current, IReadOnlyCollection<ServerProfile> members)
    {
        existing = current;
        SetResourceReference(StyleProperty, "DialogWindowStyle");
        Title = "Параметры папки «" + path + "»"; Width = 540; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var p = new StackPanel { Margin = new Thickness(24) };
        p.Children.Add(Ui.DialogHeading("Папка «" + path + "»"));
        int inheriting = members.Count(m => m.Inherit != InheritFlags.None);
        Summary = $"Серверов в папке: {members.Count}, наследуют её значения: {inheriting}.";
        var intro = Ui.Text("Серверы, у которых в профиле отмечено «Взять из папки», берут эти значения из ближайшей папки, где они заданы — как в mRemoteNG. " + Summary, 12);
        intro.TextWrapping = TextWrapping.Wrap; intro.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); intro.Margin = new Thickness(0, 8, 0, 6); p.Children.Add(intro);
        void Field(string label, Control input) { var l = Ui.Text(label, 12); l.Margin = new Thickness(0, 10, 0, 5); l.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); p.Children.Add(l); System.Windows.Automation.AutomationProperties.SetName(input, label); p.Children.Add(input); }
        user.Text = current?.Username ?? ""; domain.Text = current?.Domain ?? "";
        Field("Пользователь", user); Field("Домен (для RDP)", domain); Field(current?.HasPassword == true ? "Пароль (сохранён; оставьте пустым, чтобы не менять)" : "Пароль (необязательно)", password);
        if (current?.HasPassword == true) p.Children.Add(clear);
        rdpSources = members.Where(m => m.IsRdp).OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var items = new List<string> { current?.Rdp is null ? "Не задавать" : "Не задавать (сейчас заданы — будут убраны)" };
        if (current?.Rdp is not null) items.Insert(0, "Оставить как есть");
        items.AddRange(rdpSources.Select(s => "Как у «" + s.Name + "»"));
        rdpFrom.ItemsSource = items; rdpFrom.SelectedIndex = 0;
        Field("Параметры удалённого рабочего стола для наследования", rdpFrom);
        // Metric history (0.6): every SSH server of the folder records in the background and runs the folder's metrics.
        recordHistory.IsChecked = current?.RecordHistory == true; p.Children.Add(recordHistory);
        metrics = current?.CustomMetrics.Select(m => m.Clone()).ToList() ?? [];
        void UpdateMetricsLabel() => customMetrics.Content = metrics.Count == 0 ? "Свои метрики папки…" : $"Свои метрики папки ({metrics.Count})…";
        UpdateMetricsLabel();
        customMetrics.Click += (_, _) => { if (CustomMetricsWindow.Edit(this, metrics, null, false, "Свои метрики — папка «" + path + "»") is { } edited) { metrics = edited.ToList(); UpdateMetricsLabel(); } };
        p.Children.Add(customMetrics);
        var note = Ui.Text("Пароль папки хранится с защитой Windows, как пароли серверов.", 12); note.TextWrapping = TextWrapping.Wrap; note.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); note.Margin = new Thickness(0, 10, 0, 0); p.Children.Add(note);
        var save = Ui.Button("Сохранить", Accept); save.IsDefault = true; save.SetResourceReference(StyleProperty, "PrimaryButtonStyle"); save.Name = "FolderSave";
        var cancel = Ui.Button("Отмена", () => DialogResult = false); cancel.IsCancel = true;
        Content = Ui.DialogSurface(p, cancel, save);
        Loaded += (_, _) => user.Focus();
    }

    internal void Accept()
    {
        var d = domain.Text.Trim();
        if (d.Any(c => char.IsControl(c) || c is '\\' or '/' or '@')) { Ui.SetValidationMessage(domain, "Домен: имя без \\, / и @."); return; }
        int index = rdpFrom.SelectedIndex; int offset = existing?.Rdp is not null ? 2 : 1;
        RdpOptions? rdp = existing?.Rdp is not null && index == 0 ? existing.Rdp : index >= offset ? rdpSources[index - offset].Rdp.Clone() : null;
        Result = new FolderDefaults { Id = existing?.Id ?? Guid.NewGuid(), Path = existing?.Path ?? "", Username = user.Text.Trim(), Domain = d, HasPassword = existing?.HasPassword == true, Rdp = rdp, RecordHistory = recordHistory.IsChecked == true, CustomMetrics = metrics.Select(m => m.Clone()).ToList() };
        NewPassword = password.Password.Length > 0 ? password.Password : null; ClearPassword = clear.IsChecked == true;
        if (IsLoaded && IsVisible) DialogResult = true;
    }

    internal void ForgetPassword() { NewPassword = null; password.Clear(); }
}
