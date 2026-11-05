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
        p.Children.Add(Ui.DialogHeading((profile.IsRdp && profile.Rdp.Domain.Length > 0 ? profile.Rdp.Domain + "\\" : "") + profile.Username + "@" + profile.Endpoint));
        if(notice is not null){var warning=Ui.Text(notice,12);warning.TextWrapping=TextWrapping.Wrap;warning.Margin=new Thickness(0,8,0,8);p.Children.Add(warning);}
        p.Children.Add(Ui.Text(profile.IsRdp ? "Пароль Windows" : profile.Authentication == AuthenticationKind.PrivateKey ? "Passphrase приватного ключа (может быть пустой)" : "Пароль", 14));
        var password = new SecretEntry(profile.IsRdp?"Пароль удалённого рабочего стола":profile.Authentication==AuthenticationKind.PrivateKey?"Парольная фраза приватного ключа":"Пароль SSH") { Password = saved ?? "", Margin = new Thickness(0, 10, 0, 12) }; p.Children.Add(password);
        var info = Ui.Text((profile.IsRdp?"Пароль передаётся только элементу удалённого рабочего стола Windows и только после проверки сертификата сервера.\n":"Секрет передаётся только после проверки ключа сервера.\n")+(profile.SaveSecret?"Защищённая копия обновится только после успешного подключения.":"Секрет не сохраняется. Эту настройку можно изменить в профиле."), 12); info.TextWrapping = TextWrapping.Wrap; info.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); p.Children.Add(info);
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
    /// <summary>CI hook: the decision for an RDP certificate instead of the dialog.</summary>
    internal static Func<HostKeyPrompt, TrustDecision>? RdpCertificateOverride { get; set; }
    /// <summary>
    /// The TLS key of a remote desktop server. A new key is confirmed like an SSH host key. A changed key is refused by
    /// default; it can be replaced only after ticking that the new fingerprint was checked (RDP certificates are renewed
    /// by Windows, unlike SSH host keys).
    /// </summary>
    public static TrustDecision RdpCertificate(HostKeyPrompt key,Window? owner=null)
    {
        if(RdpCertificateOverride is { } decide)return decide(key);
        var w = Window(key.Changed?"Сертификат сервера изменён":"Проверка сертификата сервера", 660,owner); var p = new StackPanel { Margin = new Thickness(24) };
        p.Children.Add(Ui.DialogHeading(key.Changed?"Сертификат удалённого рабочего стола изменён":"Доверять этому серверу?"));
        var explanation = Ui.Text(key.Changed
            ?"Ключ сервера не совпадает с сохранённым. Windows обновляет сертификат удалённого рабочего стола по сроку, но так же выглядит и подмена сервера. Пароль не отправлен."
            :"Сервер удалённого рабочего стола впервые предъявил ключ. Сравните отпечаток с данными администратора до входа: пароль отправляется только после вашего решения."); explanation.TextWrapping = TextWrapping.Wrap; explanation.Margin = new Thickness(0, 12, 0, 18); p.Children.Add(explanation);
        var endpoint=Ui.Text($"Адрес: {key.Host}:{key.Port}\nКлюч: {key.Algorithm}");endpoint.TextWrapping=TextWrapping.Wrap;p.Children.Add(endpoint);
        var fp = new TextBox { Text = key.Fingerprint, IsReadOnly = true, FontFamily = new FontFamily("Consolas"), Margin = new Thickness(0, 14, 0, 12) };System.Windows.Automation.AutomationProperties.SetName(fp,"SHA256-отпечаток ключа сервера");p.Children.Add(fp);
        var result = TrustDecision.Reject;
        var cancel=Ui.Button("Не подключаться",()=>w.DialogResult=false);cancel.IsCancel=true;
        if(key.Changed)
        {
            var old=Ui.Text("Ранее: "+key.PreviousFingerprint);old.TextWrapping=TextWrapping.Wrap;old.Margin=new Thickness(0,0,0,12);p.Children.Add(old);
            var checkedBox=new CheckBox{Content=new TextBlock{Text="Я сверил новый отпечаток с администратором сервера",TextWrapping=TextWrapping.Wrap},Margin=new Thickness(0,0,0,6)};p.Children.Add(checkedBox);
            var replace=Ui.Button("Заменить и подключиться",()=>{result=TrustDecision.Permanent;w.DialogResult=true;});replace.IsEnabled=false;replace.Name="RdpReplaceCertificate";
            checkedBox.Checked+=(_,_)=>replace.IsEnabled=true;checkedBox.Unchecked+=(_,_)=>replace.IsEnabled=false;
            w.Content=Ui.DialogSurface(p,replace,cancel);w.Loaded+=(_,_)=>cancel.Focus();
        }
        else
        {
            var permanent=Ui.Button("Доверить и сохранить",()=>{result=TrustDecision.Permanent;w.DialogResult=true;});permanent.SetResourceReference(FrameworkElement.StyleProperty,"PrimaryButtonStyle");
            w.Content=Ui.DialogSurface(p,cancel,Ui.Button("Только этот сеанс",()=>{result=TrustDecision.Session;w.DialogResult=true;}),permanent);
        }
        return ModalRunner.Show(w,Owner(owner),CancellationToken.None)?result:TrustDecision.Reject;
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
    /// <summary>Opens "Параметры" (see <see cref="SettingsWindow"/>).</summary>
    public static void Settings() => Settings("general");
    public static void Settings(string section) { var window = new SettingsWindow(section); window.ShowDialog(); }
    static void TrustManagement()
    {
        var w = Window("Доверенные ключи", 760); w.Height = 450; w.SizeToContent = SizeToContent.Manual; w.ResizeMode = ResizeMode.CanResize;
        var p = new DockPanel { Margin = new Thickness(20) }; var list = new ListBox { DisplayMemberPath = "DisplayName" };
        var info = Ui.Text("Удаление доверия — отдельное действие. При следующем входе ключ нужно проверить снова."); info.TextWrapping = TextWrapping.Wrap; info.Margin = new Thickness(0, 0, 0, 12); DockPanel.SetDock(info, Dock.Top); p.Children.Add(info);
        void Refresh() => list.ItemsSource = App.Store.Trust.Records.Select(r => new TrustRow(r.Host, r.Port, r.Algorithm, r.Fingerprint)).ToList();
        var remove = Ui.AsyncButton("Удалить выбранный ключ…", async () => { if (list.SelectedItem is TrustRow r && Ui.Confirm($"Удалить сохранённое доверие к {r.Host}:{r.Port} ({r.Algorithm})?\n\nВыполняйте это только после проверки нового отпечатка у администратора.")) { await App.Store.Trust.ForgetAsync(r.Host, r.Port, r.Algorithm); Refresh(); } }); DockPanel.SetDock(remove, Dock.Bottom); p.Children.Add(remove); p.Children.Add(list); Refresh(); w.Content = p; w.ShowDialog();
    }
    sealed record TrustRow(string Host, int Port, string Algorithm, string Fingerprint) { public string DisplayName => $"{Host}:{Port} · {Algorithm}\n{Fingerprint}"; }
    internal static Task ClearTransferCacheAsync() => ClearTransferCache();
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
