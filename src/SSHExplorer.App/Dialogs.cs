using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SSHExplorer.Core;

namespace SSHExplorer.App;

public static class Dialogs
{
    static readonly Dictionary<(Guid, int, bool, bool, string), ConflictDecision> conflictPolicies = [];
    static Window Window(string title, double width = 560) => new() { Title = title, Width = width, SizeToContent = SizeToContent.Height, MaxHeight = 850, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Application.Current.MainWindow, ShowInTaskbar = false };
    static TextBox Field(Panel p, string label, string value)
    {
        var l = Ui.Text(label); l.Margin = new Thickness(0, 9, 0, 5); p.Children.Add(l);
        var t = new TextBox { Text = value }; System.Windows.Automation.AutomationProperties.SetName(t, label); p.Children.Add(t); return t;
    }
    public static ServerProfile? Profile(ServerProfile? old = null)
    {
        var w = Window(old is null ? "Добавить сервер" : "Изменить сервер");
        var p = new StackPanel { Margin = new Thickness(24) };
        p.Children.Add(Ui.Text("Подключение по SSH", 22, true));
        var name = Field(p, "Название", old?.Name ?? "");
        var host = Field(p, "DNS-имя, IPv4 или IPv6", old?.Host ?? "");
        var port = Field(p, "Порт", (old?.Port ?? 22).ToString());
        var user = Field(p, "Пользователь Linux", old?.Username ?? "");
        p.Children.Add(Ui.Text("Способ входа"));
        var auth = new ComboBox { ItemsSource = new[] { "Пароль", "Приватный ключ", "Keyboard-interactive" }, SelectedIndex = (int)(old?.Authentication ?? AuthenticationKind.Password), Margin = new Thickness(0, 6, 0, 0) }; p.Children.Add(auth);
        var key = Field(p, "Приватный ключ (локальный файл)", old?.PrivateKeyPath ?? "");
        var choose = Ui.Button("Выбрать ключ…", () => { var f = new OpenFileDialog { Title = "Приватный ключ SSH", CheckFileExists = true }; if (f.ShowDialog(w) == true) key.Text = f.FileName; }); choose.HorizontalAlignment = HorizontalAlignment.Left; p.Children.Add(choose);
        void KeyState() { key.IsEnabled = choose.IsEnabled = auth.SelectedIndex == 1; }
        auth.SelectionChanged += (_, _) => KeyState(); KeyState();
        var initial = Field(p, "Стартовая папка", old?.InitialPath ?? "~");
        var timeout = Field(p, "Таймаут подключения, секунд", (old?.TimeoutSeconds ?? 20).ToString());
        var save = new CheckBox { Content = "Сохранять пароль / passphrase с защитой Windows", IsChecked = old?.SaveSecret ?? false, Margin = new Thickness(0, 14, 0, 12) }; p.Children.Add(save);
        ServerProfile? result = null;
        var ok = Ui.Button("Сохранить", () =>
        {
            try
            {
                result = new ServerProfile { Id = old?.Id ?? Guid.NewGuid(), Name = name.Text.Trim(), Host = host.Text.Trim().Trim('[', ']'), Port = int.Parse(port.Text), Username = user.Text.Trim(), Authentication = (AuthenticationKind)auth.SelectedIndex, PrivateKeyPath = key.Text.Trim(), InitialPath = initial.Text.Trim(), TimeoutSeconds = Math.Clamp(int.Parse(timeout.Text), 3, 300), SaveSecret = save.IsChecked == true, Bookmarks = old?.Bookmarks.ToList() ?? [] };
                result.Validate(); w.DialogResult = true;
            }
            catch (Exception ex) { MessageBox.Show(w, ex.Message, "Проверьте профиль", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }); ok.IsDefault = true;
        var cancel = Ui.Button("Отмена", () => w.DialogResult = false); cancel.IsCancel = true;
        var row = Ui.Row(ok, cancel); row.HorizontalAlignment = HorizontalAlignment.Right; p.Children.Add(row);
        w.Content = new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; w.Loaded += (_, _) => name.Focus();
        return w.ShowDialog() == true ? result : null;
    }
    public static SessionCredentials? Credentials(ServerProfile profile, string? saved)
    {
        var w = Window("Вход — " + profile.Name, 480); var p = new StackPanel { Margin = new Thickness(24) };
        p.Children.Add(Ui.Text(profile.Username + "@" + profile.Endpoint, 18, true));
        p.Children.Add(Ui.Text(profile.Authentication == AuthenticationKind.PrivateKey ? "Passphrase приватного ключа (может быть пустой)" : "Пароль", 14));
        var password = new PasswordBox { Password = saved ?? "", Margin = new Thickness(0, 14, 0, 16) }; p.Children.Add(password);
        var info = Ui.Text("Секрет передаётся только после проверки ключа сервера.", 12); info.TextWrapping = TextWrapping.Wrap; info.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); p.Children.Add(info);
        var ok = Ui.Button("Подключиться", () => w.DialogResult = true); ok.IsDefault = true;
        var cancel = Ui.Button("Отмена", () => w.DialogResult = false); cancel.IsCancel = true;
        var row = Ui.Row(ok, cancel); row.HorizontalAlignment = HorizontalAlignment.Right; row.Margin = new Thickness(0, 18, 0, 0); p.Children.Add(row); w.Content = p; w.Loaded += (_, _) => password.Focus();
        return w.ShowDialog() == true ? new SessionCredentials(password.Password) : null;
    }
    public static TrustDecision Trust(HostKeyPrompt key)
    {
        if (key.Changed) { Ui.Error($"Ключ сервера {key.Host}:{key.Port} изменён. Подключение заблокировано.\n\nРанее: {key.PreviousFingerprint}\nСейчас: {key.Fingerprint}\n\nПроверьте ключ у администратора. Замена доступна отдельно в настройках доверенных ключей."); return TrustDecision.Reject; }
        var w = Window("Проверка ключа сервера", 660); var p = new StackPanel { Margin = new Thickness(24) };
        p.Children.Add(Ui.Text("Доверять этому серверу?", 22, true));
        var explanation = Ui.Text("Сравните отпечаток с данными администратора сервера до входа. Это ключ сервера, а не ваш приватный ключ."); explanation.TextWrapping = TextWrapping.Wrap; explanation.Margin = new Thickness(0, 12, 0, 18); p.Children.Add(explanation);
        p.Children.Add(Ui.Text($"Адрес: {key.Host}:{key.Port}\nАлгоритм: {key.Algorithm}"));
        var fp = new TextBox { Text = key.Fingerprint, IsReadOnly = true, FontFamily = new FontFamily("Consolas"), Margin = new Thickness(0, 14, 0, 18) }; p.Children.Add(fp);
        var result = TrustDecision.Reject;
        p.Children.Add(Ui.Row(Ui.Button("Доверить и сохранить", () => { result = TrustDecision.Permanent; w.DialogResult = true; }), Ui.Button("Только эта сессия", () => { result = TrustDecision.Session; w.DialogResult = true; }), Ui.Button("Отмена", () => w.DialogResult = false)));
        w.Content = p; w.ShowDialog(); return result;
    }
    public static string? Interactive(InteractivePrompt prompt)
    {
        var w = Window("Дополнительная проверка SSH", 520); var p = new StackPanel { Margin = new Thickness(24) }; var text = Ui.Text(prompt.Instruction + "\n" + prompt.Prompt); text.TextWrapping = TextWrapping.Wrap; p.Children.Add(text);
        Control input = prompt.Echo ? new TextBox() : new PasswordBox(); input.Margin = new Thickness(0, 12, 0, 16); p.Children.Add(input);
        var ok = Ui.Button("Продолжить", () => w.DialogResult = true); ok.IsDefault = true; var cancel = Ui.Button("Отмена", () => w.DialogResult = false); cancel.IsCancel = true; p.Children.Add(Ui.Row(ok, cancel)); w.Content = p; w.Loaded += (_, _) => input.Focus();
        return w.ShowDialog() == true ? input is PasswordBox password ? password.Password : ((TextBox)input).Text : null;
    }
    public static ConflictDecision Conflict(TransferConflict c)
    {
        var key = (c.JobId ?? Guid.Empty, c.Attempt, c.SourceIsDirectory, c.DestinationIsDirectory, c.Reason ?? "");
        if (c.JobId is not null && conflictPolicies.TryGetValue(key, out var remembered)) return remembered;
        var w = Window("Конфликт имён", 620); var p = new StackPanel { Margin = new Thickness(24) }; p.Children.Add(Ui.Text("Назначение уже существует", 21, true));
        var t = Ui.Text($"Источник: {c.Source}\n{(c.SourceIsDirectory ? "Папка" : Ui.Size(c.SourceLength))}\n\nНазначение: {c.Destination}\n{(c.DestinationIsDirectory ? "Папка" : Ui.Size(c.DestinationLength))}" + (string.IsNullOrEmpty(c.Reason) ? "" : "\n\n" + c.Reason)); t.TextWrapping = TextWrapping.Wrap; t.Margin = new Thickness(0, 16, 0, 18); p.Children.Add(t);
        var result = ConflictDecision.Cancel;
        var applyAll = new CheckBox { Content = "Применить к аналогичным конфликтам этой передачи", IsEnabled = c.JobId is not null, Margin = new Thickness(0, 0, 0, 12) }; p.Children.Add(applyAll);
        foreach (var (label, decision) in new[] { ("Заменить / объединить папки", ConflictDecision.Replace), ("Пропустить", ConflictDecision.Skip), ("Сохранить оба", ConflictDecision.KeepBoth), ("Отменить передачу", ConflictDecision.Cancel) })
        { var b = Ui.Button(label, () => { result = decision; w.DialogResult = true; }); b.HorizontalContentAlignment = HorizontalAlignment.Left; p.Children.Add(b); }
        w.Content = p; w.ShowDialog(); if (applyAll.IsChecked == true && result != ConflictDecision.Cancel && c.JobId is not null) { if (conflictPolicies.Count > 128) conflictPolicies.Clear(); conflictPolicies[key] = result; } return result;
    }
    public static void Settings()
    {
        var w = Window("Параметры SSH Explorer", 570); var p = new StackPanel { Margin = new Thickness(24) }; var s = App.Store.Settings;
        p.Children.Add(Ui.Text("Параметры", 24, true)); p.Children.Add(Ui.Text("Тема приложения"));
        var themes = new ComboBox { ItemsSource = new[] { "Как в Windows", "Светлая", "Тёмная" }, SelectedIndex = s.Theme == "Light" ? 1 : s.Theme == "Dark" ? 2 : 0, Margin = new Thickness(0, 10, 0, 10) }; p.Children.Add(themes);
        var poll = Field(p, "Обновление диспетчера задач, секунд (1–60)", s.PollIntervalSeconds.ToString());
        var font = Field(p, "Размер шрифта терминала (8–36)", s.TerminalFontSize.ToString());
        var maxBytes = Field(p, "Лимит изображения для предпросмотра, МБ (1–200)", (s.PreviewMaxBytes / 1024 / 1024).ToString());
        var pixels = Field(p, "Лимит изображения, мегапикселей (1–100)", (s.PreviewMaxPixels / 1_000_000).ToString());
        var parallel = Field(p, "Одновременных запросов миниатюр (1–4, для новых вкладок)", s.PreviewParallelism.ToString());
        var cache = Field(p, "Кэш изображений на вкладку, МБ (16–1024)", (s.PreviewCacheBytes / 1024 / 1024).ToString());
        p.Children.Add(Ui.Button("Доверенные ключи серверов…", TrustManagement));
        p.Children.Add(Ui.AsyncButton("Очистить локальные копии для Windows…", ClearTransferCache));
        var path = Ui.Text("Настройки и защищённые секреты: " + App.Store.DirectoryPath, 12); path.TextWrapping = TextWrapping.Wrap; path.Margin = new Thickness(0, 14, 0, 14); path.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); p.Children.Add(path);
        p.Children.Add(Ui.Text("F5 в файловом менеджере очищает кэш миниатюр текущей вкладки.", 12));
        p.Children.Add(Ui.AsyncButton("Сохранить", async () => { s.PollIntervalSeconds = Math.Clamp(int.Parse(poll.Text), 1, 60); s.TerminalFontSize = Math.Clamp(int.Parse(font.Text), 8, 36); s.PreviewMaxBytes = Math.Clamp(long.Parse(maxBytes.Text), 1, 200) * 1024 * 1024; s.PreviewMaxPixels = Math.Clamp(long.Parse(pixels.Text), 1, 100) * 1_000_000; s.PreviewParallelism = Math.Clamp(int.Parse(parallel.Text), 1, 4); s.PreviewCacheBytes = Math.Clamp(long.Parse(cache.Text), 16, 1024) * 1024 * 1024; s.Theme = themes.SelectedIndex == 1 ? "Light" : themes.SelectedIndex == 2 ? "Dark" : "System"; Theme.Apply(s.Theme); await App.Store.SaveAsync(); w.DialogResult = true; })); w.Content = new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; w.ShowDialog();
    }
    static void TrustManagement()
    {
        var w = Window("Доверенные ключи", 760); w.Height = 450; w.SizeToContent = SizeToContent.Manual; w.ResizeMode = ResizeMode.CanResize;
        var p = new DockPanel { Margin = new Thickness(20) }; var list = new ListBox { DisplayMemberPath = "DisplayName" };
        var info = Ui.Text("Удаление доверия — отдельное действие. При следующем входе ключ нужно проверить снова."); info.TextWrapping = TextWrapping.Wrap; info.Margin = new Thickness(0, 0, 0, 12); DockPanel.SetDock(info, Dock.Top); p.Children.Add(info);
        void Refresh() => list.ItemsSource = App.Store.Trust.Records.Select(r => new TrustRow(r.Host, r.Port, r.Algorithm, r.Fingerprint)).ToList();
        var remove = Ui.AsyncButton("Удалить выбранный ключ…", async () => { if (list.SelectedItem is TrustRow r && Ui.Confirm($"Удалить сохранённое доверие к {r.Host}:{r.Port} ({r.Algorithm})?\n\nВыполняйте это только после проверки нового отпечатка у администратора.")) { await App.Store.Trust.ForgetAsync(r.Host, r.Port, r.Algorithm); Refresh(); } }); DockPanel.SetDock(remove, Dock.Bottom); p.Children.Add(remove); p.Children.Add(list); Refresh(); w.Content = p; w.ShowDialog();
    }
    sealed record TrustRow(string Host, int Port, string Algorithm, string Fingerprint) { public string DisplayName => $"{Host}:{Port} · {Algorithm}\n{Fingerprint}"; }
    static async Task ClearTransferCache()
    {
        if (App.Transfers.Jobs.Any(j => j.State is TransferState.Queued or TransferState.Running)) throw new InvalidOperationException("Дождитесь завершения передач перед очисткой локальных копий.");
        if (!Ui.Confirm("Удалить подготовленные локальные копии для буфера и перетаскивания Windows?\n\nРанее скопированные файловые объекты в буфере могут перестать открываться. Файлы на сервере сохранятся.")) return;
        var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(App.Store.DirectoryPath, "TransferCache"));
        await Task.Run(() =>
        {
            if (!System.IO.Directory.Exists(root)) return;
            foreach (var dir in System.IO.Directory.EnumerateDirectories(root))
            {
                var full = System.IO.Path.GetFullPath(dir);
                if (!full.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(System.IO.Path.GetFileName(full), "N", out _) || (System.IO.File.GetAttributes(full) & System.IO.FileAttributes.ReparsePoint) != 0) continue;
                System.IO.Directory.Delete(full, true);
            }
        });
    }
}
