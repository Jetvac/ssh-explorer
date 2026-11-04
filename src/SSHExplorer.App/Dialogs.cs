using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Data;
using System.Globalization;
using Microsoft.Win32;
using SSHExplorer.Core;

namespace SSHExplorer.App;

public static class Dialogs
{
    static readonly Dictionary<(Guid, int, bool, bool, string), ConflictDecision> conflictPolicies = [];
    static Window? Owner(Window? owner)=>owner??Application.Current?.MainWindow;
    internal static Window Window(string title, double width = 560,Window? owner=null)
    {
        var window=new Window { Title = title, Width = width, SizeToContent = SizeToContent.Height, MaxHeight = Math.Max(320,SystemParameters.WorkArea.Height-48), ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen, ShowInTaskbar = false };
        window.SetResourceReference(FrameworkElement.StyleProperty,"DialogWindowStyle");
        var parent=Owner(owner);
        if(parent is not null&&(parent.IsLoaded||new System.Windows.Interop.WindowInteropHelper(parent).Handle!=IntPtr.Zero)){window.Owner=parent;window.WindowStartupLocation=WindowStartupLocation.CenterOwner;}
        return window;
    }
    static TextBox Field(Panel p, string label, string value)
    {
        var l = Ui.Text(label,12);l.TextWrapping=TextWrapping.Wrap;l.SetResourceReference(TextBlock.ForegroundProperty,"Muted"); l.Margin = new Thickness(0, 9, 0, 5); p.Children.Add(l);
        var t = new TextBox { Text = value }; System.Windows.Automation.AutomationProperties.SetName(t, label); p.Children.Add(t);
        var error=Ui.Text("",12);error.SetResourceReference(FrameworkElement.StyleProperty,"ValidationMessageStyle");error.SetBinding(TextBlock.TextProperty,new Binding{Source=t,Path=new PropertyPath(Ui.ValidationMessageProperty)});error.SetBinding(UIElement.VisibilityProperty,new Binding{Source=t,Path=new PropertyPath(Ui.ValidationMessageProperty),Converter=new NonEmptyTextVisibilityConverter()});p.Children.Add(error);
        t.TextChanged+=(_,_)=>Ui.SetValidationMessage(t,"");return t;
    }
    public static ServerProfile? Profile(ServerProfile? old = null)
    {
        var dialog=new ProfileDialog(new ProfilePersistence(App.Store),old,Application.Current.MainWindow);
        return ModalRunner.Show(dialog,Owner(null))?dialog.SavedProfile:null;
    }
    public static Task<ServerProfile?> ProfileAsync(ServerProfile? old=null)=>Task.FromResult(Profile(old));
    public static Task<ProfileSubmission?> ProfileSubmissionAsync(ServerProfile? old=null,Window? owner=null,CancellationToken ct=default)
    {
        if(ct.IsCancellationRequested)return Task.FromResult<ProfileSubmission?>(null);
        var dialog=new ProfileDialog(new ProfilePersistence(App.Store),old,Owner(owner),allowConnect:true);
        return Task.FromResult(ModalRunner.Show(dialog,Owner(owner),ct)?dialog.Submission:null);
    }
    public static SessionCredentials? Credentials(ServerProfile profile, string? saved,Window? owner=null,CancellationToken ct=default,string? notice=null)
    {
        if(ct.IsCancellationRequested)return null;
        var w = Window("Вход — " + profile.Name, 480,owner); var p = new StackPanel { Margin = new Thickness(24) };
        p.Children.Add(Ui.DialogHeading(profile.Username + "@" + profile.Endpoint));
        if(notice is not null){var warning=Ui.Text(notice,12);warning.TextWrapping=TextWrapping.Wrap;warning.Margin=new Thickness(0,8,0,8);p.Children.Add(warning);}
        p.Children.Add(Ui.Text(profile.Authentication == AuthenticationKind.PrivateKey ? "Passphrase приватного ключа (может быть пустой)" : "Пароль", 14));
        var password = new SecretEntry(profile.Authentication==AuthenticationKind.PrivateKey?"Парольная фраза приватного ключа":"Пароль SSH") { Password = saved ?? "", Margin = new Thickness(0, 10, 0, 12) }; p.Children.Add(password);
        var info = Ui.Text("Секрет передаётся только после проверки ключа сервера.\n"+(profile.SaveSecret?"Защищённая копия обновится только после успешного подключения.":"Секрет не сохраняется. Эту настройку можно изменить в профиле."), 12); info.TextWrapping = TextWrapping.Wrap; info.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); p.Children.Add(info);
        var ok = Ui.Button("Подключиться", () => w.DialogResult = true); ok.IsDefault = true;ok.SetResourceReference(FrameworkElement.StyleProperty,"PrimaryButtonStyle");
        var cancel = Ui.Button("Отмена", () => w.DialogResult = false); cancel.IsCancel = true;
        w.Content = Ui.DialogSurface(p,cancel,ok); w.Loaded += (_, _) => password.FocusInput();
        try{return ModalRunner.Show(w,Owner(owner),ct)?new SessionCredentials(password.Password):null;}
        finally{password.Clear();}
    }
    public static TrustDecision Trust(HostKeyPrompt key,Window? owner=null,CancellationToken ct=default)
    {
        if(ct.IsCancellationRequested)return TrustDecision.Reject;
        var w = Window("Проверка ключа сервера", 660,owner); var p = new StackPanel { Margin = new Thickness(24) };
        p.Children.Add(Ui.DialogHeading(key.Changed?"Ключ сервера изменён — вход заблокирован":"Доверять этому серверу?"));
        var explanation = Ui.Text("Сравните отпечаток с данными администратора сервера до входа. Это ключ сервера, а не ваш приватный ключ."); explanation.TextWrapping = TextWrapping.Wrap; explanation.Margin = new Thickness(0, 12, 0, 18); p.Children.Add(explanation);
        var endpoint=Ui.Text($"Адрес: {key.Host}:{key.Port}\nАлгоритм: {key.Algorithm}");endpoint.TextWrapping=TextWrapping.Wrap;p.Children.Add(endpoint);
        var fp = new TextBox { Text = key.Fingerprint, IsReadOnly = true, FontFamily = new FontFamily("Consolas"), Margin = new Thickness(0, 14, 0, 18) };System.Windows.Automation.AutomationProperties.SetName(fp,"SHA256-отпечаток ключа сервера");p.Children.Add(fp);
        var result = TrustDecision.Reject;
        var cancel=Ui.Button("Отмена",()=>w.DialogResult=false);cancel.IsCancel=true;
        if(key.Changed)
        {
            var old=Ui.Text("Ранее: "+key.PreviousFingerprint+"\nПроверьте новый ключ у администратора. Замена доверия доступна отдельным действием в настройках.");old.TextWrapping=TextWrapping.Wrap;p.Children.Add(old);w.Content=Ui.DialogSurface(p,cancel);
        }
        else
        {
            var permanent=Ui.Button("Доверить и сохранить",()=>{result=TrustDecision.Permanent;w.DialogResult=true;});permanent.SetResourceReference(FrameworkElement.StyleProperty,"PrimaryButtonStyle");
            w.Content=Ui.DialogSurface(p,cancel,Ui.Button("Только эта сессия",()=>{result=TrustDecision.Session;w.DialogResult=true;}),permanent);
        }
        return ModalRunner.Show(w,Owner(owner),ct)?result:TrustDecision.Reject;
    }
    public static string? Interactive(InteractivePrompt prompt,Window? owner=null,CancellationToken ct=default)
    {
        if(ct.IsCancellationRequested)return null;
        var w = Window("Дополнительная проверка SSH", 520,owner); var p = new StackPanel { Margin = new Thickness(24) }; var text = Ui.Text(prompt.Instruction + "\n" + prompt.Prompt); text.TextWrapping = TextWrapping.Wrap; p.Children.Add(text);
        Control input = prompt.Echo ? new TextBox() : new SecretEntry("Ответ на дополнительный запрос SSH"); input.Margin = new Thickness(0, 12, 0, 16); p.Children.Add(input);
        var ok = Ui.Button("Продолжить", () => w.DialogResult = true); ok.IsDefault = true;ok.SetResourceReference(FrameworkElement.StyleProperty,"PrimaryButtonStyle"); var cancel = Ui.Button("Отмена", () => w.DialogResult = false); cancel.IsCancel = true;w.Content = Ui.DialogSurface(p,cancel,ok); w.Loaded += (_, _) =>{if(input is SecretEntry secret)secret.FocusInput();else input.Focus();};
        try{return ModalRunner.Show(w,Owner(owner),ct)?input is SecretEntry secret?secret.Password:((TextBox)input).Text:null;}
        finally{if(input is SecretEntry secret)secret.Clear();else ((TextBox)input).Clear();}
    }
    public static ConflictDecision Conflict(TransferConflict c)
    {
        var key = (c.JobId ?? Guid.Empty, c.Attempt, c.SourceIsDirectory, c.DestinationIsDirectory, c.Reason ?? "");
        if (c.JobId is not null && conflictPolicies.TryGetValue(key, out var remembered)) return remembered;
        var w = Window("Конфликт имён", 620); var p = new StackPanel { Margin = new Thickness(24) }; p.Children.Add(Ui.DialogHeading("Назначение уже существует"));
        var t = Ui.Text($"Источник: {c.Source}\n{(c.SourceIsDirectory ? "Папка" : Ui.Size(c.SourceLength))}\n\nНазначение: {c.Destination}\n{(c.DestinationIsDirectory ? "Папка" : Ui.Size(c.DestinationLength))}" + (string.IsNullOrEmpty(c.Reason) ? "" : "\n\n" + c.Reason)); t.TextWrapping = TextWrapping.Wrap; t.Margin = new Thickness(0, 16, 0, 18); p.Children.Add(t);
        var result = ConflictDecision.Cancel;
        var applyAll = new CheckBox { Content = new TextBlock{Text="Применить к аналогичным конфликтам этой передачи",TextWrapping=TextWrapping.Wrap}, IsEnabled = c.JobId is not null, Margin = new Thickness(0, 0, 0, 12) };System.Windows.Automation.AutomationProperties.SetName(applyAll,"Применить к аналогичным конфликтам этой передачи"); p.Children.Add(applyAll);
        foreach (var (label, decision) in new[] { ("Заменить / объединить папки", ConflictDecision.Replace), ("Пропустить", ConflictDecision.Skip), ("Сохранить оба", ConflictDecision.KeepBoth) })
        { var b = Ui.Button(label, () => { result = decision; w.DialogResult = true; }); b.HorizontalContentAlignment = HorizontalAlignment.Left; p.Children.Add(b); }
        var cancel=Ui.Button("Отменить передачу",()=>w.DialogResult=false);cancel.IsCancel=true;w.Content = Ui.DialogSurface(p,cancel); w.ShowDialog(); if (applyAll.IsChecked == true && result != ConflictDecision.Cancel && c.JobId is not null) { if (conflictPolicies.Count > 128) conflictPolicies.Clear(); conflictPolicies[key] = result; } return result;
    }
    public static void Settings()
    {
        var w = Window("Параметры SSH Explorer", 570); var p = new StackPanel { Margin = new Thickness(24) }; var s = App.Store.Settings;
        p.Children.Add(Ui.DialogHeading("Параметры")); p.Children.Add(Ui.Text("Тема приложения"));
        var version=Ui.Text($"SSH Explorer {BuildInfo.DisplayVersion}\nСборка: {BuildInfo.Commit}",12);version.TextWrapping=TextWrapping.Wrap;p.Children.Add(version);
        p.Children.Add(Ui.AsyncButton("Скопировать путь к диагностике",()=>{Clipboard.SetText(System.IO.Path.Combine(App.Store.DirectoryPath,"Logs"));return Task.CompletedTask;}));
        var themes = new ComboBox { ItemsSource = new[] { "Как в Windows", "Светлая", "Тёмная" }, SelectedIndex = s.Theme == "Light" ? 1 : s.Theme == "Dark" ? 2 : 0, Margin = new Thickness(0, 10, 0, 10) };System.Windows.Automation.AutomationProperties.SetName(themes,"Тема приложения"); p.Children.Add(themes);
        var poll = Field(p, "Обновление диспетчера задач, секунд (1–60)", s.PollIntervalSeconds.ToString());
        var font = Field(p, "Размер шрифта терминала (8–36)", s.TerminalFontSize.ToString());
        var maxBytes = Field(p, "Лимит изображения для предпросмотра, МБ (1–200)", (s.PreviewMaxBytes / 1024 / 1024).ToString());
        var pixels = Field(p, "Лимит изображения, мегапикселей (1–100)", (s.PreviewMaxPixels / 1_000_000).ToString());
        var parallel = Field(p, "Одновременных запросов миниатюр (1–4, для новых вкладок)", s.PreviewParallelism.ToString());
        var cache = Field(p, "Кэш изображений на вкладку, МБ (16–1024)", (s.PreviewCacheBytes / 1024 / 1024).ToString());
        var editor = Field(p, "Текстовый редактор (полный путь EXE; пусто — Блокнот Windows)", s.ExternalEditorPath);
        p.Children.Add(Ui.Button("Выбрать установленный редактор…", () => { var pick = new Microsoft.Win32.OpenFileDialog { Filter = "Редактор Windows|*.exe", CheckFileExists = true }; if (pick.ShowDialog(w) == true) editor.Text = pick.FileName; }));
        p.Children.Add(Ui.Button("Доверенные ключи серверов…", TrustManagement));
        p.Children.Add(Ui.AsyncButton("Очистить локальные копии для Windows…", ClearTransferCache));
        p.Children.Add(Ui.AsyncButton("Очистить завершённые документы редактора…", () => ((MainWindow)Application.Current.MainWindow).ClearCompletedDocumentsAsync()));
        var path = Ui.Text("Настройки и защищённые секреты: " + App.Store.DirectoryPath, 12); path.TextWrapping = TextWrapping.Wrap; path.Margin = new Thickness(0, 14, 0, 14); path.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); p.Children.Add(path);
        p.Children.Add(Ui.Text("F5 в файловом менеджере очищает кэш миниатюр текущей вкладки.", 12));
        var save=Ui.AsyncButton("Сохранить",async()=>
        {
            var ranges=new[]{(poll,1L,60L),(font,8L,36L),(maxBytes,1L,200L),(pixels,1L,100L),(parallel,1L,4L),(cache,16L,1024L)};
            var values=new List<long>();TextBox? firstError=null;
            foreach(var (field,min,max) in ranges)
            {
                if(!long.TryParse(field.Text,NumberStyles.Integer,CultureInfo.CurrentCulture,out var value)||value<min||value>max)
                {Ui.SetValidationMessage(field,$"Введите целое число от {min} до {max}.");firstError??=field;}
                else {Ui.SetValidationMessage(field,"");values.Add(value);}
            }
            if(firstError is not null){firstError.Focus();firstError.SelectAll();firstError.BringIntoView();return;}
            if (!string.IsNullOrWhiteSpace(editor.Text) && (!System.IO.Path.IsPathFullyQualified(editor.Text) || editor.Text.StartsWith(@"\\") || !editor.Text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(editor.Text))) { Ui.SetValidationMessage(editor, "Выберите существующий локальный EXE редактора."); editor.Focus(); return; }
            s.ExternalEditorPath = editor.Text;
            s.PollIntervalSeconds=(int)values[0];s.TerminalFontSize=(int)values[1];s.PreviewMaxBytes=values[2]*1024*1024;s.PreviewMaxPixels=values[3]*1_000_000;s.PreviewParallelism=(int)values[4];s.PreviewCacheBytes=values[5]*1024*1024;
            s.Theme=themes.SelectedIndex==1?"Light":themes.SelectedIndex==2?"Dark":"System";Theme.Apply(s.Theme);await App.Store.SaveAsync();w.DialogResult=true;
        });
        save.IsDefault=true;save.SetResourceReference(FrameworkElement.StyleProperty,"PrimaryButtonStyle");var cancel=Ui.Button("Отмена",()=>w.DialogResult=false);cancel.IsCancel=true;w.Content=Ui.DialogSurface(p,cancel,save);w.ShowDialog();
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
