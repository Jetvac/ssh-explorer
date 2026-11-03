using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SSHExplorer.App;
using SSHExplorer.App.ViewModels;
using SSHExplorer.Core;

internal static class Program
{
    static App _app=null!;
    static Window _owner=null!;
    static string _root="";
    static int _passed;
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            _root=Path.Combine(Environment.CurrentDirectory,".tools","profile-regression",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(_root);
            _app=new App();_app.InitializeComponent();_app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            SetStore(NewStore("startup"));Theme.Apply("Light");
            var windowStyle=new Style(typeof(Window),(Style)_app.Resources[typeof(Window)]);
            windowStyle.Setters.Add(new Setter(Window.ShowActivatedProperty,false));windowStyle.Setters.Add(new Setter(Window.WindowStateProperty,WindowState.Minimized));_app.Resources[typeof(Window)]=windowStyle;
            bool noWindows=args.Contains("--no-windows",StringComparer.Ordinal);
            _owner=new Window{Title="Isolated STA profile regression",ShowInTaskbar=false,ShowActivated=false,WindowState=WindowState.Minimized};_app.MainWindow=_owner;if(!noWindows)_owner.Show();
            ValidateFields();StorageTransactions();ViewModelFailures();
            if(noWindows)UnshownMainSelectionScenario();else{DialogScenarios();MainWindowScenario();}
            int fixtureIndex=Array.IndexOf(args,"--ssh-fixture");
            if(fixtureIndex>=0){if(fixtureIndex+1>=args.Length)throw new ArgumentException("--ssh-fixture requires isolated configuration path");ProfileConnectionScenario(args[fixtureIndex+1]);}
            _owner.Close();_app.Shutdown();Console.WriteLine($"RESULT: {_passed} profile validation, persistence and STA dialog checks passed");return 0;
        }
        catch(Exception ex){Console.WriteLine(ex);return 1;}
    }
    static void Check(bool condition,string label){if(!condition)throw new Exception("FAIL "+label);_passed++;Console.WriteLine("PASS "+label);}
    static AppStore NewStore(string name)=>new(Path.Combine(_root,name));
    static void SetStore(AppStore store)=>typeof(App).GetProperty(nameof(App.Store),BindingFlags.Public|BindingFlags.Static)!.SetValue(null,store);
    static ProfileViewModel ValidModel(IProfilePersistence? persistence=null)=>new(persistence??new ProfilePersistence(NewStore("vm-"+Guid.NewGuid().ToString("N")))){ProfileName="Тестовый сервер",Host="offline.example.invalid",Username="tester"};
    static T Pump<T>(Task<T> task)
    {
        if(!task.IsCompleted){var frame=new DispatcherFrame();_ = task.ContinueWith(_=>_app.Dispatcher.BeginInvoke(()=>frame.Continue=false),TaskScheduler.Default);Dispatcher.PushFrame(frame);}
        return task.GetAwaiter().GetResult();
    }
    static void Pump(Task task)=>Pump(Complete(task));
    static async Task<bool> Complete(Task task){await task;return true;}
    static void ValidateFields()
    {
        var model=ValidModel();Check(model.Validate(""),"offline DNS and Cyrillic display name save without network access");
        foreach(var host in new[]{"127.0.0.1","2001:db8::1","[2001:db8::1]","localhost","пример.рф"}){model.Host=host;Check(model.Validate(""),"valid host accepted: "+host);}
        foreach(var host in new[]{"","https://example.com","host:22","999.1.1.1","[broken","name/path","bad host"}){model.Host=host;Check(!model.Validate("")&&!string.IsNullOrEmpty(model.HostError),"invalid host stays inline: "+(host.Length==0?"empty":host));}
        model.Host="offline.example.invalid";model.ProfileName="";Check(!model.Validate("")&&model.NameError.Length>0,"empty name stays inline");model.ProfileName="Тест";
        model.Username="";Check(!model.Validate("")&&model.UserError.Length>0,"empty username stays inline");model.Username="tester";
        foreach(var port in new[]{"0","65536","abc","-1","999999999999"}){model.Port=port;Check(!model.Validate("")&&model.PortError.Length>0,"invalid port is rejected without clamp: "+port);}model.Port="22";
        foreach(var timeout in new[]{"2","121","-1","abc","999999999999"}){model.Timeout=timeout;Check(!model.Validate("")&&model.TimeoutError.Length>0,"invalid timeout is rejected without clamp: "+timeout);}model.Timeout="20";
        model.InitialPath="relative/folder";Check(!model.Validate("")&&model.InitialPathError.Length>0,"relative initial path is rejected inline");model.InitialPath="~/каталог с пробелом";
        model.AuthenticationIndex=1;model.PrivateKeyPath=Path.Combine(_root,"missing-key");Check(!model.Validate("")&&model.KeyError.Length>0,"missing key file is rejected without connecting");
        string key=Path.Combine(_root,"key-placeholder");File.WriteAllText(key,"not-used-for-authentication-in-this-form-test");model.PrivateKeyPath=key;Check(model.Validate(""),"existing key path with empty passphrase can be saved offline");
        model.AuthenticationIndex=0;model.SaveSecret=true;Check(!model.Validate("")&&model.SecretError.Length>0,"password-save opt-in requires a password");Check(model.Validate("test-only-secret"),"explicit password-save opt-in accepts entered secret");
    }
    static void StorageTransactions()
    {
        var store=NewStore("transaction");string secret="test-secret-"+Guid.NewGuid().ToString("N");
        var profile=new ServerProfile{Name="Сохранённый сервер",Host="offline.example.invalid",Username="tester",SaveSecret=true};
        Pump(store.SaveProfileAsync(profile,secret));Check(store.Profiles.Count==1,"transaction commits one in-memory profile");
        var reopened=new AppStore(store.DirectoryPath);Check(reopened.Profiles.Single().Id==profile.Id&&Pump(reopened.ReadSecretAsync(profile.Id))==secret,"profile and DPAPI secret survive restart");
        Check(!Directory.EnumerateFiles(store.DirectoryPath).Any(path=>File.ReadAllText(path).Contains(secret,StringComparison.Ordinal)),"settings, backup and encrypted files never contain plaintext secret");
        byte[] originalSettings=File.ReadAllBytes(Path.Combine(store.DirectoryPath,"settings.json"));
        using(var locked=new FileStream(Path.Combine(store.DirectoryPath,"settings.json"),FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            bool failed=false;try{Pump(store.SaveProfileAsync(new ServerProfile{Id=profile.Id,Name="Unsaved replacement",Host="second.invalid",Username="tester",SaveSecret=true},"replacement-secret"));}catch(IOException){failed=true;}
            Check(failed,"real Windows sharing violation rejects atomic settings commit");
            Check(store.Profiles.Single().Name==profile.Name&&Pump(store.ReadSecretAsync(profile.Id))==secret,"failed edit preserves existing in-memory profile and original secret");
        }
        Check(File.ReadAllBytes(Path.Combine(store.DirectoryPath,"settings.json")).SequenceEqual(originalSettings),"failed settings replacement leaves exact original JSON bytes");
        Check(Directory.EnumerateFiles(store.DirectoryPath,"secret-*.dpapi").Count()==1,"failed settings commit removes only its staged encrypted secret");
        using(var locked=new FileStream(Path.Combine(store.DirectoryPath,"settings.json"),FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            bool failed=false;try{Pump(store.DeleteProfileAsync(profile.Id));}catch(IOException){failed=true;}Check(failed&&store.Profiles.Count==1,"failed profile deletion preserves in-memory and persisted profile");
        }
        var noSecret=new ServerProfile{Id=profile.Id,Name="Отредактирован",Host="2001:db8::1",Username="tester",SaveSecret=false};Pump(store.SaveProfileAsync(noSecret,null));
        Check(Pump(store.ReadSecretAsync(profile.Id))is null&&!Directory.EnumerateFiles(store.DirectoryPath,"secret-*.dpapi").Any(),"disabling secret persistence removes the saved encrypted secret");
        reopened=new AppStore(store.DirectoryPath);Check(reopened.Profiles.Single().Name=="Отредактирован"&&reopened.Profiles.Single().Id==profile.Id,"edit preserves stable profile ID after restart");
        Pump(reopened.DeleteProfileAsync(profile.Id));Check(new AppStore(store.DirectoryPath).Profiles.Count==0,"successful deletion survives restart");
        var legacy=NewStore("legacy");var old=new ServerProfile{Name="Legacy",Host="legacy.invalid",Username="tester",SaveSecret=true};legacy.Profiles.Add(old);Pump(legacy.SaveAsync());Pump(legacy.SaveSecretAsync(old.Id,secret));
        var legacyReloaded=new AppStore(legacy.DirectoryPath);Check(Pump(legacyReloaded.ReadSecretAsync(old.Id))==secret,"legacy profile-ID DPAPI file remains readable");
        Pump(legacyReloaded.SaveProfileAsync(old,secret));Check(Pump(new AppStore(legacy.DirectoryPath).ReadSecretAsync(old.Id))==secret,"legacy profile migrates to transactional secret reference");
    }
    static void ViewModelFailures()
    {
        var store=NewStore("vm-write-failure");var model=ValidModel(new ProfilePersistence(store));
        Directory.CreateDirectory(Path.Combine(store.DirectoryPath,"settings.json"));
        Check(!Pump(model.SaveAsync(""))&&model.Status.Contains("Введённые значения сохранены",StringComparison.Ordinal),"real settings write error is reported inline");
        Check(model.ProfileName=="Тестовый сервер"&&model.Host=="offline.example.invalid"&&store.Profiles.Count==0&&!model.IsSaving,"failed save retains all input and creates no partial profile");
        Directory.Delete(Path.Combine(store.DirectoryPath,"settings.json"));Check(Pump(model.SaveAsync(""))&&store.Profiles.Count==1,"same form input saves successfully after storage is repaired");
        var denyStore=NewStore("secret-write-denied");var directory=new DirectoryInfo(denyStore.DirectoryPath);var original=directory.GetAccessControl();
        var denied=directory.GetAccessControl();var sid=WindowsIdentity.GetCurrent().User!;
        denied.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.CreateFiles|FileSystemRights.WriteData,InheritanceFlags.None,PropagationFlags.None,AccessControlType.Deny));
        try
        {
            directory.SetAccessControl(denied);var deniedModel=ValidModel(new ProfilePersistence(denyStore));deniedModel.SaveSecret=true;
            Check(!Pump(deniedModel.SaveAsync("not-logged-secret"))&&denyStore.Profiles.Count==0,"real denied secret-file write creates no partial profile");
            Check(deniedModel.ProfileName=="Тестовый сервер"&&!deniedModel.IsSaving,"secret storage failure preserves input and re-enables save");
        }
        finally{directory.SetAccessControl(original);}
        var blocked=new BlockingPersistence(new ProfilePersistence(NewStore("double-save")));var reentrant=ValidModel(blocked);
        var first=reentrant.SaveAsync("");Check(reentrant.IsSaving&&!Pump(reentrant.SaveAsync(""))&&blocked.SaveCalls==1,"rapid double-save performs exactly one persistence operation");
        blocked.Release.TrySetResult();Check(Pump(first)&&reentrant.SavedProfile is not null,"original save completes after duplicate click is ignored");
    }
    static void DialogScenarios()
    {
        var store=NewStore("dialog-save");SetStore(store);
        ServerProfile? saved=RunStaticDialog(dialog=>
        {
            Fill(dialog);Check(Logical<Expander>(dialog).Single().IsExpanded==false,"advanced connection fields start collapsed");
            var key=Logical<TextBox>(dialog).Single(x=>AutomationProperties.GetName(x)=="Приватный ключ");Check(!key.IsVisible,"private-key block occupies no layout in password mode");
            var save=FindButton(dialog,"Сохранить");var cancel=FindButton(dialog,"Отмена");Check(save.IsDefault&&cancel.IsCancel,"dialog exposes correct Enter and Escape default actions");
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        Check(saved is not null&&store.Profiles.Count==1,"actual Dialogs.Profile opens, atomically saves and ignores double click");
        Check(new AppStore(store.DirectoryPath).Profiles.Single().Host=="offline.example.invalid","actual modal save persists offline profile after restart");
        var cancelled=RunStaticDialog(dialog=>FindButton(dialog,"Отмена").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));Check(cancelled is null&&store.Profiles.Count==1,"cancel button leaves profiles unchanged");
        cancelled=RunStaticDialog(dialog=>dialog.Close());Check(cancelled is null&&store.Profiles.Count==1,"window close leaves profiles unchanged and dialog can reopen");
        int phase=0;
        saved=RunStaticDialog(dialog=>
        {
            if(phase++==0){Fill(dialog);Field(dialog,"Порт").Text="invalid";FindButton(dialog,"Сохранить").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}
            else{Check(dialog is ProfileDialog{Model.PortError.Length:>0}&&Field(dialog,"Название").Text=="STA сервер","actual modal validation preserves input without closing");Field(dialog,"Порт").Text="22";FindButton(dialog,"Сохранить").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}
        },repeat:true);
        Check(saved is not null&&store.Profiles.Count==2,"same modal dialog saves corrected port without reopening");
        var failedStore=NewStore("dialog-failure");Directory.CreateDirectory(Path.Combine(failedStore.DirectoryPath,"settings.json"));SetStore(failedStore);phase=0;
        saved=RunStaticDialog(dialog=>
        {
            if(phase++==0){Fill(dialog);FindButton(dialog,"Сохранить").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}
            else if(dialog is ProfileDialog d&&!d.Model.IsSaving&&d.Model.Status.Contains("Не удалось",StringComparison.Ordinal))
            {
                Check(Field(dialog,"Название").Text=="STA сервер"&&failedStore.Profiles.Count==0,"actual modal persistence failure retains input without a partial profile");
                Directory.Delete(Path.Combine(failedStore.DirectoryPath,"settings.json"));FindButton(dialog,"Сохранить").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
        },repeat:true);
        Check(saved is not null&&failedStore.Profiles.Count==1,"modal remains usable and saves after write failure is repaired");
        var old=failedStore.Profiles.Single();saved=RunStaticDialog(dialog=>{Field(dialog,"Название").Text="Исправленное имя";FindButton(dialog,"Сохранить").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));},old);
        Check(saved?.Id==old.Id&&failedStore.Profiles.Count==1&&failedStore.Profiles.Single().Name=="Исправленное имя","actual edit modal updates the existing profile without duplication");
    }
    static void MainWindowScenario()
    {
        var store=NewStore("main-window");SetStore(store);var main=new MainWindow{ShowActivated=false,WindowState=WindowState.Minimized,ShowInTaskbar=false};_app.MainWindow=main;main.Show();
        var content=(FrameworkElement)main.Content;content.Measure(new Size(1320,800));content.Arrange(new Rect(0,0,1320,800));content.UpdateLayout();
        for(int iteration=0;iteration<2;iteration++)
        {
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(40)};int ticks=0;
            timer.Tick+=(_,_)=>
            {
                if(++ticks>100)throw new TimeoutException("MainWindow AddProfile did not display dialog");
                if(_app.Windows.Cast<Window>().FirstOrDefault(x=>x is ProfileDialog)is not Window dialog)return;
                timer.Stop();Fill(dialog);Field(dialog,"Название").Text="Main profile "+iteration;FindButton(dialog,"Сохранить").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            var add=Logical<Button>(main).First(x=>AutomationProperties.GetName(x).Contains("Добавить сервер",StringComparison.Ordinal));
            timer.Start();add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));timer.Stop();
            var grid=Logical<DataGrid>(main).First();Check(store.Profiles.Count==iteration+1&&grid.Items.Count==iteration+1,"full MainWindow AddProfile refreshes server list, iteration "+(iteration+1));
            grid.SelectedIndex=0;
        }
        Check(new AppStore(store.DirectoryPath).Profiles.Count==2,"full MainWindow AddProfile persistence survives restart with current selection");main.Close();_app.MainWindow=_owner;
    }
    static void UnshownMainSelectionScenario()
    {
        var store=NewStore("unshown-main");SetStore(store);
        foreach(var name in new[]{"Первый профиль","Второй профиль"})Pump(store.SaveProfileAsync(new ServerProfile{Name=name,Host="offline.example.invalid",Username="tester"},null));
        var main=new MainWindow();_app.MainWindow=main;
        var content=(FrameworkElement)main.Content;content.Measure(new Size(1320,800));content.Arrange(new Rect(0,0,1320,800));content.UpdateLayout();
        var grid=Logical<DataGrid>(main).First();Check(grid.Items.Count==2,"unshown real MainWindow realizes persisted profiles through actual binding");
        int changes=0;grid.SelectionChanged+=(_,_)=>{if(++changes>80)throw new Exception("Selection refresh re-entered more than80 times");};
        grid.SelectedIndex=0;
        Check(grid.SelectedItem is ServerProfileViewModel,"saved-row selection returns without recursive TabControl refresh");
        Guid selected=((ServerProfileViewModel)grid.SelectedItem).Profile.Id;
        var refresh=typeof(MainWindow).GetMethod("RefreshHome",BindingFlags.Instance|BindingFlags.NonPublic)!;
        for(int i=0;i<10;i++)refresh.Invoke(main,null);
        Check(grid.SelectedItem is ServerProfileViewModel row&&row.Profile.Id==selected&&grid.Items.Count==2,"repeated actual RefreshHome preserves selection and read-only source binding");
        var tabs=Logical<TabControl>(main).First();var other=new TabItem{Header="Isolated test tab",Content=new TextBlock{Text="No server commands"}};tabs.Items.Add(other);tabs.SelectedItem=other;tabs.SelectedIndex=0;
        Check(grid.SelectedItem is ServerProfileViewModel again&&again.Profile.Id==selected,"genuine tab switching keeps selected profile without routed child-selection feedback");
        main.Close();_app.MainWindow=_owner;
    }
    static void ProfileConnectionScenario(string configurationFile)
    {
        var configs=new List<(string Path,bool Key)>{(configurationFile,false)};
        string keyConfig=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configurationFile))!,"integration-key.json");if(File.Exists(keyConfig))configs.Add((keyConfig,true));
        else if(File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configurationFile))!,"client-key"))&&File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configurationFile))!,"key-passphrase.txt")))configs.Add((configurationFile,true));
        foreach(var(configPath,key)in configs)
        {
            using var doc=System.Text.Json.JsonDocument.Parse(File.ReadAllText(configPath));var cfg=doc.RootElement;
            string Read(string name)=>cfg.GetProperty(name).GetString()??"";
            var store=NewStore(key?"connect-key":"connect-password");SetStore(store);
            string fixtureDirectory=Path.GetDirectoryName(Path.GetFullPath(configPath))!;
            string privateKey=key?(cfg.TryGetProperty("PrivateKeyPath",out var keyPath)?keyPath.GetString()??"":Path.Combine(fixtureDirectory,"client-key")):"";
            string secretFile=key&&(!cfg.TryGetProperty("Authentication",out var auth)||auth.GetString()!="PrivateKey")?Path.Combine(fixtureDirectory,"key-passphrase.txt"):Read("SecretFile");
            var model=new ProfileViewModel(new ProfilePersistence(store)){ProfileName="Изолированный профиль",Host=Read("Host"),Port=cfg.GetProperty("Port").GetInt32().ToString(),Username=Read("Username"),SaveSecret=true,AuthenticationIndex=key?1:0,PrivateKeyPath=privateKey};
            string secret=File.ReadAllText(secretFile).TrimEnd('\r','\n');
            Check(Pump(model.SaveAsync(secret)),"connection fixture profile commits transactionally: "+(key?"encrypted key":"password"));
            var reopened=new AppStore(store.DirectoryPath);var profile=reopened.Profiles.Single();
            var session=new ServerSession(profile,reopened);
            try
            {
                string restored=Pump(reopened.ReadSecretAsync(profile.Id))??throw new Exception("Saved fixture secret was not restored");
                Pump(session.ConnectAsync(new SessionCredentials(restored),prompt=>prompt.Fingerprint==Read("ExpectedHostFingerprint")?Task.FromResult(TrustDecision.Permanent):throw new Exception("Fixture host fingerprint mismatch")));
                Check(session.SftpAvailable&&session.ShellAvailable&&Pump(session.ExecuteAsync("printf profile-auth-alive")).Output=="profile-auth-alive","persisted DPAPI secret restores actual SSH/SFTP authentication: "+(key?"encrypted key":"password"));
                Pump(reopened.DeleteProfileAsync(profile.Id));
                Check(new AppStore(store.DirectoryPath).Profiles.Count==0&&Pump(session.ExecuteAsync("printf session-independent")).Output=="session-independent","deleting local profile preserves active remote session: "+(key?"encrypted key":"password"));
            }
            finally{Pump(session.DisposeAsync().AsTask());}
        }
    }
    static ServerProfile? RunStaticDialog(Action<Window> step,ServerProfile? old=null,bool repeat=false)
    {
        Exception? failure=null;var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(60)};int ticks=0;
        timer.Tick+=(_,_)=>
        {
            var dialog=_app.Windows.Cast<Window>().FirstOrDefault(x=>x is ProfileDialog);if(dialog is null)return;
            try{if(++ticks>100)throw new TimeoutException("Modal profile scenario did not complete");if(!repeat)timer.Stop();step(dialog);}
            catch(Exception ex){failure=ex;timer.Stop();dialog.Close();}
        };
        timer.Start();var result=Dialogs.Profile(old);timer.Stop();if(failure is not null)throw failure;return result;
    }
    static void Fill(Window dialog)
    {Field(dialog,"Название").Text="STA сервер";Field(dialog,"Адрес сервера").Text="offline.example.invalid";Field(dialog,"Пользователь Linux").Text="tester";}
    static TextBox Field(Window dialog,string name)=>Logical<TextBox>(dialog).Single(x=>AutomationProperties.GetName(x)==name);
    static Button FindButton(Window dialog,string text)=>Logical<Button>(dialog).First(x=>x.Content?.ToString()==text);
    static IEnumerable<T> Logical<T>(DependencyObject parent)where T:DependencyObject
    {
        foreach(var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {if(child is T typed)yield return typed;foreach(var nested in Logical<T>(child))yield return nested;}
    }
    sealed class BlockingPersistence(IProfilePersistence inner):IProfilePersistence
    {
        public TaskCompletionSource Release{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);public int SaveCalls{get;private set;}
        public Task<string?> ReadSecretAsync(Guid id,CancellationToken ct=default)=>inner.ReadSecretAsync(id,ct);
        public async Task<ProfileCommitResult> SaveAsync(ServerProfile profile,string? secret,CancellationToken ct=default){SaveCalls++;await Release.Task.WaitAsync(ct);return await inner.SaveAsync(profile,secret,ct);}
    }
}

