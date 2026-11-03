using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.CompilerServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
            _app=new App(startDesktop:false);_app.InitializeComponent();_app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var startupStore=NewStore("startup");SetStore(startupStore);
            // Application's constructor queues OnStartup even without Application.Run.
            // Consume it before constructing any Window, with desktop startup suppressed.
            var startupFrame=new DispatcherFrame();
            _app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,()=>startupFrame.Continue=false);
            Dispatcher.PushFrame(startupFrame);
            Check(_app.StartupCallbackObserved&&_app.DesktopStartupCount==0&&ReferenceEquals(App.Store,startupStore)&&_app.Windows.Count==0,"queued startup is consumed with zero desktop starts, no windows and unchanged isolated store identity");
            Console.WriteLine("SAFETY startupCallbackObserved=true; desktopStartupCount=0; startupWindows=0; startupStoreIdentityUnchanged=true");
            Theme.Apply("Light");
            var windowStyle=new Style(typeof(Window),(Style)_app.Resources[typeof(Window)]);
            windowStyle.Setters.Add(new Setter(Window.ShowActivatedProperty,false));windowStyle.Setters.Add(new Setter(Window.WindowStateProperty,WindowState.Minimized));_app.Resources[typeof(Window)]=windowStyle;
            bool noWindows=args.Contains("--no-windows",StringComparer.Ordinal);
            if(!noWindows&&!string.Equals(Environment.GetEnvironmentVariable("CI"),"true",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Window scenarios run only in CI. Use --no-windows on the host.");
            _owner=new Window{Title="Isolated STA profile regression",ShowInTaskbar=false,ShowActivated=false,WindowState=WindowState.Minimized};_app.MainWindow=_owner;if(!noWindows)_owner.Show();
            int reuseIndex=Array.IndexOf(args,"--saved-reuse");
            if(reuseIndex>=0)
            {
                if(noWindows||reuseIndex+2>=args.Length)throw new ArgumentException("--saved-reuse requires CI fixture and isolated store paths");
                SavedReuseScenario(args[reuseIndex+1],args[reuseIndex+2]);_owner.Close();_app.Shutdown();Console.WriteLine($"RESULT: {_passed} ordinary saved-credential restart checks passed");return 0;
            }
            if(!args.Contains("--ordinary-only",StringComparer.Ordinal))
            {
                ValidateFields();StorageTransactions();ViewModelFailures();SecretControls();SubmissionTransactions();CloneCompatibility();CancelledPrompts();DelayedSecretComponents();
                if(noWindows)UnshownMainSelectionScenario();else{DialogScenarios();MainWindowScenario();ModalCancellationScenarios();}
            }
            int fixtureIndex=Array.IndexOf(args,"--ssh-fixture");
            if(fixtureIndex>=0){if(fixtureIndex+1>=args.Length)throw new ArgumentException("--ssh-fixture requires isolated configuration path");ProfileConnectionScenario(args[fixtureIndex+1]);}
            int baselineIndex=Array.IndexOf(args,"--baseline-core");
            if(baselineIndex>=0){if(!noWindows||baselineIndex+1>=args.Length)throw new ArgumentException("--baseline-core requires --no-windows and published beta.1 Core path");PublishedBaselineCompatibility(args[baselineIndex+1]);}
            int ordinaryIndex=Array.IndexOf(args,"--ordinary-fixture");
            if(ordinaryIndex>=0){if(noWindows||ordinaryIndex+1>=args.Length)throw new ArgumentException("--ordinary-fixture requires CI dialog mode and fixture.json");OrdinaryPasswordScenario(args[ordinaryIndex+1]);}
            if(noWindows)
            {
                Check(_app.DesktopStartupCount==0&&_app.Windows.Cast<Window>().All(window=>!window.IsVisible&&new System.Windows.Interop.WindowInteropHelper(window).Handle==IntPtr.Zero),"isolated headless run ends with zero desktop starts and no visible window or native window handle");
                Console.WriteLine("SAFETY finalDesktopStartupCount=0; visibleWindows=0; nativeWindowHandles=0");
            }
            _owner.Close();_app.Shutdown();Console.WriteLine($"RESULT: {_passed} profile validation, persistence and {(noWindows?"unshown WPF component":"STA dialog")} checks passed");return 0;
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
    static void SecretControls()
    {
        const string exact="  пароль ' \" Ω\t  ";var entry=new SecretEntry();
        var masked=Logical<PasswordBox>(entry).Single();var text=Logical<TextBox>(entry).Single();var toggle=Logical<ToggleButton>(entry).Single();
        masked.Password=exact;Check(entry.Password==exact&&text.Text==exact,"actual masked control preserves Unicode, quotes and surrounding whitespace");
        int revision=entry.EditRevision;toggle.IsChecked=true;Check(entry.IsRevealed&&entry.Password==exact&&entry.EditRevision==revision,"reveal preserves exact value without editing or network work");
        string edited=exact+"new ";text.Text=edited;toggle.IsChecked=false;
        Check(entry.Password==edited&&masked.Password==edited&&!entry.IsRevealed,"editing revealed control synchronizes exact secret back to masked control");
        entry.Clear();Check(entry.Password.Length==0&&text.Text.Length==0&&!entry.IsRevealed,"clearing secret control clears both presentations");
        var dialog=new ProfileDialog(new ProfilePersistence(NewStore("control-profile")),allowConnect:true);
        Check(FindButton(dialog,"Сохранить и подключиться").IsDefault&&!FindButton(dialog,"Сохранить").IsDefault,"new profile has explicit connect default and separate Save action without showing a window");dialog.Close();
    }
    static void SubmissionTransactions()
    {
        const string exact="  точный ' \" Ω пароль  ";var store=NewStore("submissions");var model=ValidModel(new ProfilePersistence(store));
        var submission=Pump(model.SubmitAsync(exact,true));Check(submission is {ConnectNow:true}&&submission.Credentials?.Secret==exact,"save-and-connect returns the exact in-memory credential");
        Check(Pump(store.ReadSecretAsync(submission!.Profile.Id))is null&&!Directory.EnumerateFiles(store.DirectoryPath,"secret-*.dpapi").Any(),"without opt-in save-and-connect writes no secret file");
        Check(!submission.ToString().Contains(exact,StringComparison.Ordinal),"profile submission diagnostic text contains no secret");
        var optedStore=NewStore("deferred");var opted=ValidModel(new ProfilePersistence(optedStore));opted.SaveSecret=true;
        var pending=Pump(opted.SubmitAsync(exact,true))!;Check(pending.Credentials?.Secret==exact&&Pump(optedStore.ReadSecretAsync(pending.Profile.Id))is null,"new opted-in profile defers secret persistence until authenticated");
        Check(Pump(optedStore.SaveAuthenticatedSecretAsync(pending.Profile,exact))&&Pump(new AppStore(optedStore.DirectoryPath).ReadSecretAsync(pending.Profile.Id))==exact,"successful authentication commits exact DPAPI secret transactionally");
        var editing=new ProfileViewModel(new ProfilePersistence(optedStore),pending.Profile);Check(Pump(editing.LoadSecretAsync())==exact,"existing saved secret is available without manual re-entry");
        var retry=Pump(editing.SubmitAsync("wrong entered value",true))!;
        Check(Pump(optedStore.ReadSecretAsync(retry.Profile.Id))==exact,"unverified replacement credentials never overwrite a good saved secret");
        using(var locked=new FileStream(Path.Combine(optedStore.DirectoryPath,"settings.json"),FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            bool failed=false;try{Pump(optedStore.SaveAuthenticatedSecretAsync(retry.Profile,"other authenticated value"));}catch(IOException){failed=true;}
            Check(failed&&Pump(optedStore.ReadSecretAsync(retry.Profile.Id))==exact,"failed post-auth settings commit preserves old good secret");
        }
        Check(Directory.EnumerateFiles(optedStore.DirectoryPath,"secret-*.dpapi").Count()==1,"post-auth failure removes its unreferenced secret file");
        Check(Pump(editing.SaveAsync(""))&&Pump(optedStore.ReadSecretAsync(pending.Profile.Id))==exact,"separate Save with blank field preserves existing protected secret");
        var changed=new ProfileViewModel(new ProfilePersistence(optedStore),retry.Profile){Username="different-user"};Pump(changed.SubmitAsync("",true,false));
        Check(!Pump(optedStore.SaveAuthenticatedSecretAsync(retry.Profile,"late response"))&&Pump(optedStore.ReadSecretAsync(retry.Profile.Id))==exact,"late authenticated result cannot update a changed authentication identity");
        var current=optedStore.Profiles.Single();Pump(optedStore.DeleteProfileAsync(current.Id));
        Check(!Pump(optedStore.SaveAuthenticatedSecretAsync(current,exact)),"late success cannot recreate a deleted profile");
        var separate=ValidModel(new ProfilePersistence(NewStore("save-only")));separate.SaveSecret=true;
        var only=Pump(separate.SubmitAsync(exact,false));Check(only is {ConnectNow:false,Credentials:null},"separate Save has no connection side effect or credential result");
        var missing=ValidModel(new ProfilePersistence(NewStore("omitted")));var without=Pump(missing.SubmitAsync("",true,false));
        Check(without is {ConnectNow:true,Credentials:null},"untouched password field requests normal credential prompt instead of invented empty password");
        var lostStore=NewStore("removed-secret");var lostProfile=new ServerProfile{Name="removed secret",Host="lost.invalid",Username="tester",SaveSecret=true};Pump(lostStore.SaveProfileAsync(lostProfile,exact));
        var lostModel=new ProfileViewModel(new ProfilePersistence(lostStore),lostProfile);Pump(lostModel.LoadSecretAsync());foreach(var file in Directory.EnumerateFiles(lostStore.DirectoryPath,"secret-*.dpapi"))File.Delete(file);
        Check(!Pump(lostModel.SaveAsync(""))&&lostModel.Status.Contains("Не удалось",StringComparison.Ordinal)&&!lostModel.IsSaving,"removed saved-secret file is an inline recoverable error instead of an async handler crash");
        Check(Pump(lostModel.SaveAsync(exact))&&Pump(lostStore.ReadSecretAsync(lostProfile.Id))==exact,"same form recovers by entering a replacement secret after missing-file error");
        var keyStore=NewStore("empty-passphrase");var keyModel=ValidModel(new ProfilePersistence(keyStore));keyModel.AuthenticationIndex=1;keyModel.PrivateKeyPath=Path.Combine(_root,"key-placeholder");keyModel.SaveSecret=true;
        var keySubmission=Pump(keyModel.SubmitAsync("",true,false))!;Check(keySubmission.Credentials?.Secret==""&&Pump(keyStore.ReadSecretAsync(keySubmission.Profile.Id))is null,"explicit empty key passphrase remains an in-memory credential distinct from missing secret");
        Check(Pump(keyStore.SaveAuthenticatedSecretAsync(keySubmission.Profile,""))&&Pump(new AppStore(keyStore.DirectoryPath).ReadSecretAsync(keySubmission.Profile.Id))=="","empty authenticated passphrase survives DPAPI restart without becoming null");
    }
    static void CloneCompatibility()
    {
        const string exact="  beta.1 сохранённый секрет Ω  ";var original=NewStore("beta1-source");
        var profile=new ServerProfile{Name="Beta.1 clone",Host="clone.invalid",Username="tester",SaveSecret=true};Pump(original.SaveProfileAsync(profile,exact));original.Settings.Theme="Dark";original.Settings.PollIntervalSeconds=7;Pump(original.SaveAsync());
        byte[] key=Encoding.UTF8.GetBytes("isolated-generated-trust-fixture");
        var trusted=new[]{new TrustedHostKey{Host=profile.Host,Port=22,Algorithm="fixture-algorithm",PublicKey=Convert.ToBase64String(key),Fingerprint=HostTrustStore.Fingerprint(key),TrustedAtUtc=DateTime.UtcNow}};
        File.WriteAllText(Path.Combine(original.DirectoryPath,"hostkeys.json"),System.Text.Json.JsonSerializer.Serialize(trusted));
        var sourceHashes=Directory.EnumerateFiles(original.DirectoryPath).ToDictionary(p=>Path.GetFileName(p),p=>SHA256.HashData(File.ReadAllBytes(p)));
        var target=Path.Combine(_root,"beta2-clone");Directory.CreateDirectory(target);foreach(var path in Directory.EnumerateFiles(original.DirectoryPath))File.Copy(path,Path.Combine(target,Path.GetFileName(path)));
        var clone=new AppStore(target);Check(clone.Profiles.Single().Id==profile.Id&&clone.Settings.Theme=="Dark"&&clone.Settings.PollIntervalSeconds==7,"schema1 beta.1 fixture clone retains profile ID and settings");
        Check(Pump(clone.ReadSecretAsync(profile.Id))==exact&&clone.Trust.Records.Single().Fingerprint==HostTrustStore.Fingerprint(key),"schema1 cloned DPAPI secret and trusted key remain readable");
        Pump(clone.SaveProfileAsync(clone.Profiles.Single(),null));Check(Pump(new AppStore(target).ReadSecretAsync(profile.Id))==exact,"beta.2 save and restart keep cloned protected secret reference");
        Check(sourceHashes.All(pair=>SHA256.HashData(File.ReadAllBytes(Path.Combine(original.DirectoryPath,pair.Key!))).SequenceEqual(pair.Value)),"clone compatibility checks never alter original isolated beta.1 fixture bytes");
    }
    static void PublishedBaselineCompatibility(string baselineCore)
    {
        baselineCore=Path.GetFullPath(baselineCore);string baselineDirectory=Path.GetDirectoryName(baselineCore)!;
        var binaries=Directory.EnumerateFiles(baselineDirectory,"*.dll").ToDictionary(p=>p,p=>SHA256.HashData(File.ReadAllBytes(p)));
        string original=Path.Combine(_root,"published-beta1-source");Directory.CreateDirectory(original);
        const string secret="  published beta.1 ' \" пароль Ω  ";Guid profileId=Guid.NewGuid();byte[] key=Encoding.UTF8.GetBytes("isolated-published-beta1-host-key");
        CreatePublishedBaselineStore(baselineCore,original,profileId,secret,key);
        var sourceHashes=Directory.EnumerateFiles(original).ToDictionary(p=>Path.GetFileName(p),p=>SHA256.HashData(File.ReadAllBytes(p)));
        string cloneDirectory=Path.Combine(_root,"published-beta1-clone");Directory.CreateDirectory(cloneDirectory);
        foreach(var source in Directory.EnumerateFiles(original))File.Copy(source,Path.Combine(cloneDirectory,Path.GetFileName(source)));
        var clone=new AppStore(cloneDirectory);var profile=clone.Profiles.Single();
        Check(profile.Id==profileId&&profile.Bookmarks.SequenceEqual(new[]{"/baseline bookmark Ω"})&&clone.Settings.Theme=="Dark"&&clone.Settings.PollIntervalSeconds==7,"exact published beta.1 Core store clone retains profile ID, bookmarks and settings");
        Check(Pump(clone.ReadSecretAsync(profileId))==secret&&clone.Trust.Records.Single().Fingerprint==HostTrustStore.Fingerprint(key),"exact published beta.1 Core DPAPI secret and persisted host trust remain readable");
        profile.Name="Beta.2 edited clone";Pump(clone.SaveProfileAsync(profile,null));var reopened=new AppStore(cloneDirectory);
        Check(reopened.Profiles.Single().Name=="Beta.2 edited clone"&&reopened.Profiles.Single().Id==profileId&&Pump(reopened.ReadSecretAsync(profileId))==secret,"beta.2 edit and restart preserve exact published beta.1 credential reference");
        Check(sourceHashes.All(p=>SHA256.HashData(File.ReadAllBytes(Path.Combine(original,p.Key))).SequenceEqual(p.Value)),"exact published beta.1 source store bytes stay unchanged while its clone is edited");
        Check(binaries.All(p=>SHA256.HashData(File.ReadAllBytes(p.Key)).SequenceEqual(p.Value)),"published beta.1 Core and sibling DLL bytes are unchanged after Core-only loading");
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void CreatePublishedBaselineStore(string baselineCore,string directory,Guid profileId,string secret,byte[] key)
    {
        var context=new AssemblyLoadContext("PublishedBeta1Core-"+Guid.NewGuid().ToString("N"),isCollectible:true);
        context.Resolving+=(_,name)=>
        {
            // The legacy application assembly is never loaded or invoked. Only Core dependencies
            // can resolve from its already-published directory; writes go to our isolated fixture.
            if(name.Name is "SSHExplorer" or "SSHExplorer.App")throw new InvalidOperationException("Baseline compatibility must remain Core-only");
            string candidate=Path.Combine(Path.GetDirectoryName(baselineCore)!,name.Name+".dll");return File.Exists(candidate)?context.LoadFromAssemblyPath(candidate):null;
        };
        try
        {
            var assembly=context.LoadFromAssemblyPath(baselineCore);
            Check(AssemblyLoadContext.GetLoadContext(assembly)==context&&assembly.GetName().Name=="SSHExplorer.Core","published beta.1 Core loads in a separate assembly context without its application");
            Console.WriteLine("BASELINE Core informational version: "+assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
            Console.WriteLine("BASELINE Core SHA256: "+Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(baselineCore))).ToLowerInvariant());
            Type storeType=assembly.GetType("SSHExplorer.Core.AppStore",true)!,profileType=assembly.GetType("SSHExplorer.Core.ServerProfile",true)!;
            object store=Activator.CreateInstance(storeType,[directory])!,profile=Activator.CreateInstance(profileType)!;
            void Set(object target,string property,object value)=>target.GetType().GetProperty(property)!.SetValue(target,value);
            Set(profile,"Id",profileId);Set(profile,"Name","Published beta.1 fixture");Set(profile,"Host","published-beta1.invalid");Set(profile,"Username","fixture");Set(profile,"SaveSecret",true);
            ((ICollection<string>)profileType.GetProperty("Bookmarks")!.GetValue(profile)!).Add("/baseline bookmark Ω");
            var save=storeType.GetMethods().Single(m=>m.Name=="SaveProfileAsync"&&m.GetParameters().Length==3);Pump((Task)save.Invoke(store,[profile,secret,CancellationToken.None])!);
            object settings=storeType.GetProperty("Settings")!.GetValue(store)!;Set(settings,"Theme","Dark");Set(settings,"PollIntervalSeconds",7);
            Pump((Task)storeType.GetMethod("SaveAsync")!.Invoke(store,[CancellationToken.None])!);
            Type promptType=assembly.GetType("SSHExplorer.Core.HostKeyPrompt",true)!,decisionType=assembly.GetType("SSHExplorer.Core.TrustDecision",true)!;
            var callbackMethod=typeof(Program).GetMethod(nameof(BaselineAccept),BindingFlags.NonPublic|BindingFlags.Static)!.MakeGenericMethod(promptType,decisionType);
            Type callbackType=typeof(Func<,>).MakeGenericType(promptType,typeof(Task<>).MakeGenericType(decisionType));var callback=Delegate.CreateDelegate(callbackType,callbackMethod);
            object trust=storeType.GetProperty("Trust")!.GetValue(store)!;
            var verify=trust.GetType().GetMethod("VerifyAsync",BindingFlags.Instance|BindingFlags.NonPublic)!;
            Pump((Task)verify.Invoke(trust,["published-beta1.invalid",22,"fixture-algorithm",key,new HashSet<string>(),callback,CancellationToken.None])!);
            Check(!context.Assemblies.Any(a=>a.GetName().Name is "SSHExplorer" or "SSHExplorer.App"),"legacy store creation invokes Core persistence and trust APIs without loading App or constructing windows");
        }
        finally{context.Unload();}
    }
    static Task<TDecision> BaselineAccept<TPrompt,TDecision>(TPrompt _)where TDecision:struct,Enum=>Task.FromResult(Enum.Parse<TDecision>("Permanent"));
    static void CancelledPrompts()
    {
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();var profile=new ServerProfile{Name="cancelled",Host="cancelled.invalid",Username="tester"};int before=_app.Windows.Count;
        Check(Dialogs.Credentials(profile,null,_owner,cancelled.Token)is null&&Dialogs.Interactive(new InteractivePrompt("","One-time password:",false),_owner,cancelled.Token)is null,"pre-cancelled credentials and interactive prompts return without opening windows");
        Check(Dialogs.Trust(new HostKeyPrompt(profile.Host,22,"test","SHA256:test",null,false),_owner,cancelled.Token)==TrustDecision.Reject&&Pump(Dialogs.ProfileSubmissionAsync(owner:_owner,ct:cancelled.Token))is null&&_app.Windows.Count==before,"pre-cancelled trust and profile prompts create no stale windows");
    }
    static void ModalCancellationScenarios()
    {
        var profile=new ServerProfile{Name="attempt cancellation",Host="fixture.invalid",Username="fixture"};
        foreach(var kind in new[]{"credentials","interactive","trust","profile"})
        {
            DumpWindowState("before-"+kind);
            if(new System.Windows.Interop.WindowInteropHelper(_owner).Handle==IntPtr.Zero)throw new InvalidOperationException("CI modal fixture requester has no existing HWND before "+kind);
            if(_app.Windows.Cast<Window>().Any(w=>w!=_owner&&w.IsVisible))throw new InvalidOperationException("CI modal fixture has a previous visible window before "+kind);
            using var cancelled=new CancellationTokenSource();bool observed=false;Exception? error=null;var watch=Stopwatch.StartNew();
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(30)};
            timer.Tick+=(_,_)=>
            {
                var modal=_app.Windows.Cast<Window>().FirstOrDefault(w=>w!=_owner&&w.IsVisible);
                if(modal is not null)
                {
                    DumpWindowState("observed-"+kind,modal);
                    try{Check(modal.Owner==_owner,"visible "+kind+" prompt is owned by its requesting window");observed=true;}
                    catch(Exception ex){error=ex;}
                    timer.Stop();_ = Task.Run(cancelled.Cancel);
                }
                else if(watch.Elapsed>TimeSpan.FromSeconds(8)){error=new TimeoutException("CI modal was not displayed");timer.Stop();cancelled.Cancel();}
            };
            object? answer;timer.Start();
            try
            {
                answer=kind switch
                {
                    "credentials"=>Dialogs.Credentials(profile,null,_owner,cancelled.Token),
                    "interactive"=>Dialogs.Interactive(new InteractivePrompt("fixture","One-time password:",false),_owner,cancelled.Token),
                    "trust"=>Dialogs.Trust(new HostKeyPrompt(profile.Host,22,"fixture","SHA256:fixture",null,false),_owner,cancelled.Token),
                    _=>Pump(Dialogs.ProfileSubmissionAsync(owner:_owner,ct:cancelled.Token))
                };
            }
            finally{timer.Stop();}
            if(error is not null)throw error;
            Check(observed&&cancelled.IsCancellationRequested&&(answer is null||answer is TrustDecision.Reject),"worker cancellation closes actual "+kind+" modal without accepting stale input");
            Check(!_app.Windows.Cast<Window>().Any(w=>w!=_owner&&w.IsVisible),"cancelled "+kind+" leaves no visible prompt");
        }
        var owner=new Window{Title="Attempt owner",ShowActivated=false,ShowInTaskbar=false,WindowState=WindowState.Minimized};owner.Show();
        var closing=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(40)};bool child=false;
        closing.Tick+=(_,_)=>{if(_app.Windows.Cast<Window>().Any(w=>w.Owner==owner&&w.IsVisible)){child=true;closing.Stop();owner.Close();}};
        closing.Start();SessionCredentials? result;try{result=Dialogs.Credentials(profile,null,owner);}finally{closing.Stop();if(owner.IsVisible)owner.Close();}
        Check(child&&result is null,"closing requesting window cancels its actual credentials modal");
    }
    static void DumpWindowState(string stage,Window? selected=null)
    {
        static string TitleKind(Window window)=>window.Title switch
        {
            "Isolated STA profile regression"=>"fixture-owner",
            "Attempt owner"=>"close-owner",
            "Вход — attempt cancellation"=>"credentials",
            "Дополнительная проверка SSH"=>"interactive",
            "Проверка ключа сервера"=>"trust",
            "Добавить сервер"=>"add-profile",
            "Изменить сервер"=>"edit-profile",
            _=>window is MainWindow?"main-window":"unrecognized"
        };
        var windows=_app.Windows.Cast<Window>().Take(12).Select(window=>new
        {
            type=window.GetType().Name,titleKind=TitleKind(window),id=RuntimeHelpers.GetHashCode(window),
            requester=ReferenceEquals(window,_owner),selected=ReferenceEquals(window,selected),
            ownerIsRequester=ReferenceEquals(window.Owner,_owner),ownerType=window.Owner?.GetType().Name,
            ownerId=window.Owner is null?(int?)null:RuntimeHelpers.GetHashCode(window.Owner),
            window.IsVisible,window.IsLoaded,state=window.WindowState.ToString(),
            hasHwnd=new System.Windows.Interop.WindowInteropHelper(window).Handle!=IntPtr.Zero
        }).ToArray();
        // Titles are classified from a fixed allowlist; never print arbitrary titles or control values.
        Console.WriteLine("WINDOW_SNAPSHOT "+System.Text.Json.JsonSerializer.Serialize(new{stage,selectedId=selected is null?(int?)null:RuntimeHelpers.GetHashCode(selected),windows}));
    }
    static void DelayedSecretComponents()
    {
        var old=new ServerProfile{Name="delayed secret",Host="delayed.invalid",Username="tester",SaveSecret=true};
        var pending=new PendingReadPersistence();var dialog=new ProfileDialog(pending,old);var entry=Logical<SecretEntry>(dialog).Single();
        dialog.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));Logical<PasswordBox>(dialog).Single().Password="new manual input  ";
        pending.Read.TrySetResult("old stored value");WaitFor(()=>pending.Returned,TimeSpan.FromSeconds(3));Pump(_app.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle).Task);
        Check(entry.Password=="new manual input  ","delayed secret load cannot overwrite manually edited actual profile control (unshown component)");dialog.Close();
        pending=new PendingReadPersistence();dialog=new ProfileDialog(pending,old);entry=Logical<SecretEntry>(dialog).Single();dialog.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        dialog.Model.AuthenticationIndex=1;pending.Read.TrySetResult("old password");WaitFor(()=>pending.Returned,TimeSpan.FromSeconds(3));Pump(_app.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle).Task);
        Check(entry.Password==""&&!dialog.Model.SecretExplanation.Contains("прежний защищённый",StringComparison.Ordinal),"late password load is discarded after authentication method changes (unshown component)");dialog.Close();
    }
    static void DialogScenarios()
    {
        var store=NewStore("dialog-save");SetStore(store);
        ServerProfile? saved=RunStaticDialog(dialog=>
        {
            Fill(dialog);Check(Logical<Expander>(dialog).Single(x=>Equals(x.Header,"Дополнительные параметры")).IsExpanded==false&&Logical<Expander>(dialog).Single(x=>Equals(x.Header,"Подробнее о сохранении секрета")).IsExpanded==false,"advanced connection fields and optional secret details start collapsed");
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
            if(phase==0)
            {
                Fill(dialog);Field(dialog,"Порт").Text="invalid";phase=1;
                FindButton(dialog,"Сохранить").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            else if(phase==1)
            {
                Check(dialog is ProfileDialog{Model.PortError.Length:>0}&&Field(dialog,"Название").Text=="STA сервер","actual modal validation preserves input without closing");
                Field(dialog,"Порт").Text="22";phase=2;
                FindButton(dialog,"Сохранить").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            // Phase 2 waits for the asynchronous commit and dialog close. A slow disk may
            // produce many timer ticks here; the already-corrected error must not be asserted again.
        },repeat:true);
        Check(saved is not null&&store.Profiles.Count==2,"same modal dialog saves corrected port without reopening");
        var failedStore=NewStore("dialog-failure");Directory.CreateDirectory(Path.Combine(failedStore.DirectoryPath,"settings.json"));SetStore(failedStore);phase=0;
        saved=RunStaticDialog(dialog=>
        {
            if(phase==0){Fill(dialog);phase=1;FindButton(dialog,"Сохранить").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}
            else if(phase==1&&dialog is ProfileDialog d&&!d.Model.IsSaving&&d.Model.Status.Contains("Не удалось",StringComparison.Ordinal))
            {
                Check(Field(dialog,"Название").Text=="STA сервер"&&failedStore.Profiles.Count==0,"actual modal persistence failure retains input without a partial profile");
                Directory.Delete(Path.Combine(failedStore.DirectoryPath,"settings.json"));phase=2;FindButton(dialog,"Сохранить").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(40)};var deadline=Stopwatch.StartNew();
            timer.Tick+=(_,_)=>
            {
                if(deadline.Elapsed>TimeSpan.FromSeconds(15))throw new TimeoutException("MainWindow AddProfile did not display dialog");
                if(_app.Windows.Cast<Window>().FirstOrDefault(x=>x is ProfileDialog)is not Window dialog)return;
                timer.Stop();Fill(dialog);Field(dialog,"Название").Text="Main profile "+iteration;FindButton(dialog,"Сохранить").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            var add=Logical<Button>(main).First(x=>AutomationProperties.GetName(x).Contains("Добавить сервер",StringComparison.Ordinal));
            timer.Start();add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));timer.Stop();
            var grid=Logical<DataGrid>(main).First();Check(store.Profiles.Count==iteration+1&&grid.Items.Count==iteration+1,"full MainWindow AddProfile refreshes server list, iteration "+(iteration+1));
            grid.SelectedIndex=0;
        }
        Check(new AppStore(store.DirectoryPath).Profiles.Count==2,"full MainWindow AddProfile persistence survives restart with current selection");CloseMainAndWait(main);
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
            string secret=File.ReadAllText(secretFile);
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
    static void OrdinaryPasswordScenario(string configurationFile)
    {
        using var config=System.Text.Json.JsonDocument.Parse(File.ReadAllText(configurationFile));var cfg=config.RootElement;
        string secret=File.ReadAllText(cfg.GetProperty("SecretFile").GetString()!);string fingerprint=cfg.GetProperty("ExpectedHostFingerprint").GetString()!;
        var store=NewStore("ordinary-password");SetStore(store);var main=new MainWindow{ShowActivated=false,ShowInTaskbar=false,WindowState=WindowState.Minimized};_app.MainWindow=main;main.Show();
        int profiles=0,trusts=0,credentials=0;Exception? failure=null;
        var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(30)};
        EventHandler tick=(_,_)=>
        {
            try
            {
                foreach(var window in _app.Windows.Cast<Window>().Where(w=>w!=main&&w!=_owner&&w.IsVisible).ToArray())
                {
                    if(window is ProfileDialog)
                    {
                        if(profiles++!=0)continue;Fill(window);Field(window,"Адрес сервера").Text=cfg.GetProperty("Host").GetString()!;Field(window,"Порт").Text=cfg.GetProperty("Ports").GetProperty("password").GetInt32().ToString();Field(window,"Пользователь Linux").Text=cfg.GetProperty("Username").GetString()!;
                        Logical<PasswordBox>(window).Single().Password=secret;Logical<CheckBox>(window).Single().IsChecked=true;
                        FindButton(window,"Сохранить и подключиться").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    else if(window.Title=="Проверка ключа сервера")
                    {
                        trusts++;Check(Field(window,"SHA256-отпечаток ключа сервера").Text==fingerprint,"ordinary Trust dialog displays independently generated fixture fingerprint");
                        Check(store.Profiles.Count==1&&Pump(store.ReadSecretAsync(store.Profiles.Single().Id))is null,"ordinary Save-and-connect defers opted-in secret before authentication");
                        FindButton(window,"Доверить и сохранить").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    else if(window.Title.StartsWith("Вход —",StringComparison.Ordinal)){credentials++;throw new Exception("Save-and-connect unexpectedly lost its entered password");}
                }
            }
            catch(Exception ex){failure=ex;timer.Stop();foreach(var window in _app.Windows.Cast<Window>().Where(w=>w!=main&&w!=_owner).ToArray())window.Close();}
        };
        timer.Tick+=tick;timer.Start();
        try
        {
            Logical<Button>(main).First(x=>AutomationProperties.GetName(x)=="Добавить сервер").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(()=>failure is not null||ReadyFiles(main),TimeSpan.FromSeconds(35));if(failure is not null)throw failure;
            Check(profiles>=1&&trusts==1&&credentials==0,"ordinary MainWindow Add button reaches actual profile and Trust controls without credential or trust overrides");
            var view=Logical<SessionView>(main).Single();Check(view.Session.State==SessionState.Connected&&view.Session.SftpAvailable,"ordinary control route authenticates exact Unicode whitespace password");
            WaitFor(()=>Pump(store.ReadSecretAsync(store.Profiles.Single().Id))==secret,TimeSpan.FromSeconds(10));
            Check(Pump(new AppStore(store.DirectoryPath).ReadSecretAsync(store.Profiles.Single().Id))==secret,"ordinary successful connection persists opted-in secret for restart");
            Check(store.Trust.Records.Single().Fingerprint==fingerprint,"ordinary Trust button persists exact fixture host key");
        }
        finally{timer.Stop();CloseMainAndWait(main);}
        var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        start.ArgumentList.Add("--saved-reuse");start.ArgumentList.Add(Path.GetFullPath(configurationFile));start.ArgumentList.Add(store.DirectoryPath);
        using var restarted=Process.Start(start)??throw new Exception("Cannot launch isolated STA restart check");var output=restarted.StandardOutput.ReadToEndAsync();var errors=restarted.StandardError.ReadToEndAsync();
        if(!restarted.WaitForExit(45000)){restarted.Kill(true);throw new TimeoutException("Ordinary saved-secret restart check timed out");}
        string summary=output.GetAwaiter().GetResult();if(restarted.ExitCode!=0)throw new Exception("Ordinary saved-secret restart failed: "+summary+errors.GetAwaiter().GetResult());
        Check(summary.Contains("ordinary saved-credential restart checks passed",StringComparison.Ordinal),"fresh test process reuses saved credential through ordinary Connect control without prompting");
    }
    static bool ReadyFiles(MainWindow main)
    {
        var files=Logical<FileBrowserView>(main).FirstOrDefault();if(files is null||files.CurrentPath!="/")return false;
        return Logical<DataGrid>(files).Any(g=>g.Items.Cast<object>().Any(item=>item.GetType().GetProperty("Name")?.GetValue(item)?.ToString()=="README.txt"));
    }
    static void SavedReuseScenario(string configurationFile,string storePath)
    {
        using var config=System.Text.Json.JsonDocument.Parse(File.ReadAllText(configurationFile));string fingerprint=config.RootElement.GetProperty("ExpectedHostFingerprint").GetString()!;
        var store=new AppStore(storePath);SetStore(store);var main=new MainWindow{ShowActivated=false,ShowInTaskbar=false,WindowState=WindowState.Minimized};_app.MainWindow=main;main.Show();
        Exception? unexpected=null;var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(25)};
        timer.Tick+=(_,_)=>{var stale=_app.Windows.Cast<Window>().FirstOrDefault(w=>w!=main&&w!=_owner&&w.IsVisible);if(stale is not null){unexpected=new Exception("Saved-secret ordinary reuse unexpectedly displayed "+stale.GetType().Name);stale.Close();}};timer.Start();
        try
        {
            var grid=Logical<DataGrid>(main).First();grid.SelectedIndex=0;
            Logical<Button>(main).First(b=>AutomationProperties.GetName(b)=="Подключиться").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(()=>unexpected is not null||ReadyFiles(main),TimeSpan.FromSeconds(30));if(unexpected is not null)throw unexpected;
            Check(Logical<SessionView>(main).Single().Session.State==SessionState.Connected&&store.Trust.Records.Single().Fingerprint==fingerprint,"fresh process ordinary Connect uses persisted DPAPI secret and host trust");
            Check(ReadyFiles(main),"fresh process ordinary Connect navigates actual SFTP fixture and displays README");
        }
        finally{timer.Stop();CloseMainAndWait(main);}
    }
    static void CloseMainAndWait(MainWindow main)
    {
        bool closed=false;EventHandler completed=(_,_)=>closed=true;
        main.Closed+=completed;
        DumpWindowState("before-main-close",main);
        try
        {
            main.Close();
            // Visibility can change before the asynchronous Closing path has finished.
            // Only Closed and collection removal prove this exact window is gone.
            WaitFor(()=>closed&&!_app.Windows.Cast<Window>().Contains(main),TimeSpan.FromSeconds(15));
        }
        finally{main.Closed-=completed;DumpWindowState("after-main-close",main);}
        _app.MainWindow=_owner;
    }
    static void WaitFor(Func<bool> completed,TimeSpan timeout)
    {
        var pending=new TaskCompletionSource();var watch=Stopwatch.StartNew();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
        timer.Tick+=(_,_)=>{try{if(completed()){timer.Stop();pending.TrySetResult();}else if(watch.Elapsed>timeout){timer.Stop();pending.TrySetException(new TimeoutException("STA operation did not reach expected state"));}}catch(Exception ex){timer.Stop();pending.TrySetException(ex);}};
        timer.Start();try{Pump(pending.Task);}finally{timer.Stop();}
    }
    static ServerProfile? RunStaticDialog(Action<Window> step,ServerProfile? old=null,bool repeat=false)
    {
        Exception? failure=null;var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(60)};var deadline=Stopwatch.StartNew();
        timer.Tick+=(_,_)=>
        {
            var dialog=_app.Windows.Cast<Window>().FirstOrDefault(x=>x is ProfileDialog);if(dialog is null)return;
            try{if(deadline.Elapsed>TimeSpan.FromSeconds(15))throw new TimeoutException("Modal profile scenario did not complete");if(!repeat)timer.Stop();step(dialog);}
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
    sealed class PendingReadPersistence:IProfilePersistence
    {
        public TaskCompletionSource<string?> Read{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);public bool Returned{get;private set;}
        public async Task<string?> ReadSecretAsync(Guid id,CancellationToken ct=default){var value=await Read.Task.WaitAsync(ct);Returned=true;return value;}
        public Task<ProfileCommitResult> SaveAsync(ServerProfile profile,string? secret,CancellationToken ct=default)=>throw new InvalidOperationException("No save in this unshown component scenario");
    }
}
