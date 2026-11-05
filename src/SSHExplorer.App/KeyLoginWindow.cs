using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SSHExplorer.Core;

namespace SSHExplorer.App;

/// <summary>Key files on this computer: an Ed25519 key in <c>%USERPROFILE%\.ssh</c>, with a passphrase added by Windows' own ssh-keygen.</summary>
internal static class LocalKeys
{
    public static string Directory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
    public static string? SshKeygen => File.Exists(Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh-keygen.exe")) ? Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh-keygen.exe") : null;

    public static string? ValidateFileName(string name) =>
        name.Trim().Length == 0 ? "Укажите имя файла ключа." : name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains("..", StringComparison.Ordinal) ? "Имя файла содержит недопустимые символы." : null;

    public static string SuggestName(ServerProfile profile)
    {
        var safe = new string(profile.Name.Select(c => char.IsLetterOrDigit(c) && c < 128 ? char.ToLowerInvariant(c) : '-').ToArray()).Trim('-');
        while (safe.Contains("--", StringComparison.Ordinal)) safe = safe.Replace("--", "-", StringComparison.Ordinal);
        return "sshexplorer_" + (safe.Length > 0 ? safe : "server") + "_ed25519";
    }

    /// <summary>Creates the key files; never overwrites. Returns the private key path and the public key.</summary>
    public static async Task<(string Path, AuthorizedKey Public)> CreateAsync(string fileName, string comment, string? passphrase, CancellationToken ct)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = Path.Combine(Directory, fileName.Trim());
        if (File.Exists(path) || File.Exists(path + ".pub")) throw new IOException($"Файл {path} уже существует — выберите другое имя.");
        if (passphrase is { Length: > 0 })
        {
            var tool = SshKeygen ?? throw new IOException("ssh-keygen не найден: установите компонент Windows «Клиент OpenSSH» или создайте ключ без парольной фразы.");
            var info = new System.Diagnostics.ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in new[] { "-q", "-t", "ed25519", "-f", path, "-C", comment, "-N", passphrase }) info.ArgumentList.Add(a);
            using var process = System.Diagnostics.Process.Start(info) ?? throw new IOException("Не удалось запустить ssh-keygen.");
            var error = await process.StandardError.ReadToEndAsync(ct); await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0 || !File.Exists(path + ".pub")) throw new IOException("ssh-keygen не создал ключ: " + error.Trim());
        }
        else
        {
            var key = SshKeys.GenerateEd25519(comment);
            await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(key.PrivateKeyText), ct);
            await File.WriteAllTextAsync(path + ".pub", key.PublicLine + "\n", ct);
        }
        var (pub, problem) = SshKeys.ParsePublic(await File.ReadAllTextAsync(path + ".pub", ct));
        return (path, pub ?? throw new IOException(problem ?? "Не удалось прочитать открытый ключ."));
    }

    /// <summary>The public half of an existing private key: its .pub file next to it.</summary>
    public static async Task<AuthorizedKey> PublicOfAsync(string privatePath, CancellationToken ct)
    {
        if (!File.Exists(privatePath)) throw new FileNotFoundException("Файл ключа не найден.", privatePath);
        var pubPath = privatePath.EndsWith(".pub", StringComparison.OrdinalIgnoreCase) ? throw new IOException("Выберите закрытый ключ (файл без .pub) — открытый ключ возьмётся из файла рядом.") : privatePath + ".pub";
        if (!File.Exists(pubPath)) throw new IOException($"Рядом с ключом нет открытого ключа {Path.GetFileName(pubPath)}. Создайте его: ssh-keygen -y -f «ключ» > «ключ».pub");
        var (pub, problem) = SshKeys.ParsePublic(await File.ReadAllTextAsync(pubPath, ct));
        return pub ?? throw new IOException(problem ?? "Не удалось прочитать открытый ключ.");
    }

    /// <summary>Fingerprint of the key this profile logs in with, when its .pub is next to it.</summary>
    public static string? CurrentFingerprint(ServerProfile p)
    {
        if (p.Authentication != AuthenticationKind.PrivateKey || !File.Exists(p.PrivateKeyPath + ".pub")) return null;
        try { return SshKeys.ParsePublic(File.ReadAllText(p.PrivateKeyPath + ".pub")).Key?.Fingerprint; } catch (IOException) { return null; }
    }
}

/// <summary>
/// «Вход по ключу»: create an Ed25519 key here (or take an existing one), add its public half to the server, prove
/// a new connection logs in with it, and only then switch the profile. Each step is shown as it happens.
/// </summary>
public sealed class KeyLoginWindow : Window
{
    readonly KeyLoginService service;
    readonly Func<ServerProfile, string?, Task> saveProfile;
    readonly ServerProfile profile;
    readonly RadioButton create = new() { Name = "KeyCreate", IsChecked = true, Content = "Создать новый ключ Ed25519", Margin = new Thickness(0, 4, 0, 2) };
    readonly RadioButton existing = new() { Name = "KeyExisting", Content = "Использовать мой ключ", Margin = new Thickness(0, 8, 0, 2) };
    readonly TextBox fileName = new() { Name = "KeyFileName", MinHeight = 32 }, comment = new() { Name = "KeyComment", MinHeight = 32 }, existingPath = new() { Name = "KeyExistingPath", MinHeight = 32 };
    readonly PasswordBox passphrase = new() { Name = "KeyPassphrase", MinHeight = 32 }, repeat = new() { Name = "KeyPassphraseRepeat", MinHeight = 32 }, existingPassphrase = new() { Name = "KeyExistingPassphrase", MinHeight = 32 };
    readonly CheckBox switchProfile = new() { Name = "KeySwitchProfile", IsChecked = true, Margin = new Thickness(0, 10, 0, 0) };
    readonly CheckBox savePassphrase = new() { Name = "KeySavePassphrase", IsChecked = true, Content = "Сохранить парольную фразу ключа с защитой Windows", Margin = new Thickness(0, 6, 0, 0) };
    readonly StackPanel createPanel = new() { Margin = new Thickness(26, 4, 0, 4) }, existingPanel = new() { Margin = new Thickness(26, 4, 0, 4) };
    readonly TextBlock log = Ui.Text("", 12), problem = Ui.Text("", 12);
    readonly Button run, close;
    bool finished;
    public string? KeyPath { get; private set; }
    public bool Switched { get; private set; }
    internal string Log => log.Text;
    internal string? Problem => problem.Text.Length > 0 ? problem.Text : null;
    internal Button RunButton => run;
    internal void UseExisting(string path, string phrase = "") { existing.IsChecked = true; existingPath.Text = path; existingPassphrase.Password = phrase; Update(); }
    internal void SetNew(string name, string phrase = "", string again = "") { create.IsChecked = true; fileName.Text = name; passphrase.Password = phrase; repeat.Password = again; Update(); }

    public KeyLoginWindow(KeyLoginService service, ServerProfile profile, Func<ServerProfile, string?, Task> saveProfile)
    {
        this.service = service; this.profile = profile; this.saveProfile = saveProfile;
        Title = "Вход по ключу"; Width = 680; SizeToContent = SizeToContent.Height; MaxHeight = Math.Max(480, SystemParameters.WorkArea.Height - 60); ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(StyleProperty, "DialogWindowStyle");
        var body = new StackPanel { Margin = new Thickness(24) };
        body.Children.Add(Ui.DialogHeading("Вход по ключу"));
        body.Children.Add(Muted($"Ключ создаётся на этом компьютере, на «{profile.Name}» отправляется только открытый ключ. Вход проверяется новым подключением, и только после этого профиль переключается.", new Thickness(0, 2, 0, 10)));
        body.Children.Add(create);
        fileName.Text = LocalKeys.SuggestName(profile); comment.Text = Environment.UserName + "@" + Environment.MachineName + " (SSH Explorer)";
        createPanel.Children.Add(Field("Файл в " + LocalKeys.Directory, fileName));
        createPanel.Children.Add(Field("Комментарий (виден на сервере)", comment));
        var phrases = new Grid(); phrases.ColumnDefinitions.Add(new ColumnDefinition()); phrases.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) }); phrases.ColumnDefinitions.Add(new ColumnDefinition());
        var p1 = Field("Парольная фраза (необязательно)", passphrase); phrases.Children.Add(p1); var p2 = Field("Повторите", repeat); Grid.SetColumn(p2, 2); phrases.Children.Add(p2);
        createPanel.Children.Add(phrases);
        if (LocalKeys.SshKeygen is null) { passphrase.IsEnabled = repeat.IsEnabled = false; createPanel.Children.Add(Muted("Парольная фраза недоступна: нет ssh-keygen (компонент Windows «Клиент OpenSSH»). Ключ будет без неё — храните файл только у себя.", new Thickness(0, 2, 0, 0))); }
        else createPanel.Children.Add(Muted("Без фразы ключ работает как пароль, лежащий в файле: доступ к нему есть у вашей учётной записи Windows.", new Thickness(0, 2, 0, 0)));
        body.Children.Add(createPanel);
        body.Children.Add(existing);
        var pick = Ui.Button("Обзор…", () => { var d = new Microsoft.Win32.OpenFileDialog { Title = "Закрытый ключ SSH", InitialDirectory = System.IO.Directory.Exists(LocalKeys.Directory) ? LocalKeys.Directory : null, CheckFileExists = true }; if (d.ShowDialog(this) == true) existingPath.Text = d.FileName; });
        var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.Children.Add(existingPath); pick.Margin = new Thickness(8, 0, 0, 0); Grid.SetColumn(pick, 1); row.Children.Add(pick);
        AutomationProperties.SetName(existingPath, "Закрытый ключ");
        existingPanel.Children.Add(row); existingPanel.Children.Add(Field("Парольная фраза ключа, если есть", existingPassphrase));
        body.Children.Add(existingPanel);
        switchProfile.Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = $"После успешной проверки переключить профиль «{profile.Name}» на вход по этому ключу" + (profile.SaveSecret && profile.Authentication != AuthenticationKind.PrivateKey ? " (сохранённый пароль будет удалён)" : "") };
        body.Children.Add(switchProfile); body.Children.Add(savePassphrase);
        log.Name = "KeyLog"; log.TextWrapping = TextWrapping.Wrap; log.Margin = new Thickness(0, 12, 0, 0); body.Children.Add(log);
        problem.Name = "KeyProblem"; problem.TextWrapping = TextWrapping.Wrap; problem.Margin = new Thickness(0, 6, 0, 0); problem.SetResourceReference(TextBlock.ForegroundProperty, "DangerText"); body.Children.Add(problem);
        close = Ui.Button("Отмена", () => DialogResult = finished); close.IsCancel = true;
        run = Ui.AsyncButton("Установить и проверить", () => finished ? Task.Run(() => Dispatcher.Invoke(() => DialogResult = true)) : RunAsync()); run.Name = "KeyRun"; run.IsDefault = true; run.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        Content = Ui.DialogSurface(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, close, run);
        create.Checked += (_, _) => Update(); existing.Checked += (_, _) => Update();
        foreach (var b in new[] { fileName, comment, existingPath }) b.TextChanged += (_, _) => Update();
        foreach (var b in new[] { passphrase, repeat, existingPassphrase }) b.PasswordChanged += (_, _) => Update();
        Closed += (_, _) => { passphrase.Clear(); repeat.Clear(); existingPassphrase.Clear(); };
        Update();
    }

    static TextBlock Muted(string text, Thickness margin) { var t = Ui.Text(text, 12); t.TextWrapping = TextWrapping.Wrap; t.Margin = margin; t.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); return t; }
    static StackPanel Field(string label, Control box) { var p = new StackPanel { Margin = new Thickness(0, 0, 0, 8) }; var l = Ui.Text(label, 12); l.Margin = new Thickness(0, 0, 0, 4); l.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); p.Children.Add(l); p.Children.Add(box); AutomationProperties.SetName(box, label); return p; }

    string Phrase => create.IsChecked == true ? passphrase.Password : existingPassphrase.Password;

    void Update()
    {
        if (finished) return;
        createPanel.Visibility = create.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        existingPanel.Visibility = existing.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        savePassphrase.Visibility = Phrase.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        string? error = create.IsChecked == true
            ? LocalKeys.ValidateFileName(fileName.Text) ?? SshKeys.ValidateComment(comment.Text) ?? (passphrase.Password != repeat.Password ? "Парольные фразы не совпадают." : passphrase.Password.Length is > 0 and < 6 ? "Парольная фраза — не короче 6 символов." : null)
            : existingPath.Text.Trim().Length == 0 ? "Выберите файл закрытого ключа." : null;
        bool quiet = error is not null && (create.IsChecked == true ? passphrase.Password.Length > 0 && repeat.Password.Length == 0 : existingPath.Text.Length == 0);
        problem.Text = error is null || quiet ? "" : error; run.IsEnabled = error is null;
    }

    void Step(string text) => log.Text += (log.Text.Length > 0 ? "\n" : "") + text;

    /// <summary>Creates or reads the key, installs it, verifies a new key login and switches the profile.</summary>
    internal async Task RunAsync()
    {
        problem.Text = ""; log.Text = ""; run.IsEnabled = false; create.IsEnabled = existing.IsEnabled = false;
        var phrase = Phrase.Length > 0 ? Phrase : null;
        try
        {
            string path; AuthorizedKey pub;
            if (create.IsChecked == true) { (path, pub) = await LocalKeys.CreateAsync(fileName.Text, comment.Text, phrase, CancellationToken.None); Step($"✓ Ключ создан: {path}" + (phrase is null ? " (без парольной фразы)" : " (с парольной фразой)")); }
            else { path = existingPath.Text.Trim(); pub = await LocalKeys.PublicOfAsync(path, CancellationToken.None); Step($"✓ Ключ: {path}"); }
            Step("  Отпечаток: " + pub.Fingerprint);
            KeyPath = path;
            Step(await service.InstallAsync(pub) ? "✓ Открытый ключ добавлен в ~/.ssh/authorized_keys на сервере." : "✓ Этот ключ уже был на сервере.");
            Step("… Проверка: новое подключение по ключу" + (service.Session.Route is { } route ? $" через {route}" : "") + "…");
            if (await service.VerifyAsync(path, phrase) is { } failure)
            {
                Step("✗ Вход по ключу не прошёл: " + failure);
                problem.Text = "Профиль не изменён. Частые причины: на сервере запрещён вход по ключу (PubkeyAuthentication no) или неверные права ~/.ssh. Ключ на сервере остался — его можно удалить в списке.";
                return;
            }
            Step("✓ Вход по ключу работает.");
            if (switchProfile.IsChecked == true)
            {
                var updated = KeyLoginService.WithKey(profile, path); updated.SaveSecret = phrase is not null && savePassphrase.IsChecked == true;
                await saveProfile(updated, updated.SaveSecret ? phrase : null);
                Switched = true; Step($"✓ Профиль «{profile.Name}» теперь входит по ключу.");
            }
            finished = true; run.Content = "Готово"; close.Content = "Закрыть";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            problem.Text = ErrorMessages.Describe(ex);
        }
        finally { run.IsEnabled = true; if (!finished) { create.IsEnabled = existing.IsEnabled = true; Update(); } }
    }
}
