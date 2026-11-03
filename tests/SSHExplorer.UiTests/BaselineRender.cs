using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SSHExplorer.App;
using SSHExplorer.App.ViewModels;
using SSHExplorer.Core;
using SSHExplorer.Monitoring;

/// <summary>Compiled separately against the exact published beta.2 assemblies.
/// No beta.3 API is referenced in this binary. All HWND creation is CI-only.</summary>
internal static class BaselineRender
{
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            CiVisualEvidence.RequireCi();
            if(!args.Contains("--render-beta2baseline",StringComparer.Ordinal))throw new ArgumentException("Explicit baseline render mode is required.");
            string output=Value(args,"--render-dir")??"artifacts/ui-beta3-before";
            var evidence=new CiVisualEvidence(output);
            var app=new App(startDesktop:false);app.InitializeComponent();app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
            var store=new AppStore(Path.Combine(Environment.CurrentDirectory,".tools","beta2-render-store",Guid.NewGuid().ToString("N")));
            typeof(App).GetProperty(nameof(App.Store))!.SetValue(null,store);
            Pump();
            if(!app.StartupCallbackObserved||app.DesktopStartupCount!=0||app.Windows.Count!=0||!ReferenceEquals(App.Store,store))throw new InvalidOperationException("Baseline startup was not safely suppressed before creating windows.");
            Console.WriteLine("SAFETY exact beta.2 App(false) consumed queued startup; desktop starts=0; no windows; isolated store identity unchanged");
            Theme.Apply("Light");
            var profile=new ServerProfile{Name="Linux · UI fixture",Host="127.0.0.1",Username="fixture"};
            store.Profiles.Add(profile);var session=new ServerSession(profile,store);var main=new MainWindow{Width=1320,Height=850,ShowActivated=false,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.Manual,Left=-10000,Top=-10000};
            using var files=new FileBrowserView(session,main);using var monitor=new MonitorView(session);
            var fileModel=(FileBrowserViewModel)files.DataContext;
            var entries=Enumerable.Range(0,180).Select(i=>new RemoteEntry{Name=$"document-{i:000}.txt",FullPath=$"/srv/fixture/document-{i:000}.txt",IsRegularFile=true,Length=4096+i}).ToArray();
            fileModel.CommitNavigation(fileModel.BeginNavigation(),"/srv/fixture",entries,true,[],0);fileModel.ApplyFilter(true,true);Invoke(files,"ApplyItems");Invoke(files,"UpdateBreadcrumbs");
            var tree=(TreeView)Field(files,"tree");tree.Items.Clear();
            var root=Node(files,"Корень /","/");var folder=Node(files,"fixture","/srv/fixture");root.Items.Clear();folder.Items.Clear();Ui.SetIsCurrentFolder(folder,true);root.Items.Add(folder);tree.Items.Add(root);root.IsExpanded=folder.IsExpanded=true;
            var monitorModel=(MonitorViewModel)Field(monitor,"viewModel");
            var now=new DateTimeOffset(2026,11,3,12,0,0,TimeSpan.Zero);
            var processes=Enumerable.Range(0,160).Select(i=>new ProcessMetric(new ProcessIdentity(100+i,(ulong)(8000+i),1000),i==3?"long-service-name-with-readable-context-and-worker-arguments":"service-"+i,"fixture","S",i%23,32L*1024*1024+i*1048576,i%4==0?null:2048+i*512,i%5==0?null:1024,"/usr/bin/service --fixture")).ToArray();
            var system=new SystemMetric(46,4,8L*1024*1024*1024,5L*1024*1024*1024,3L*1024*1024*1024,0,0,TimeSpan.FromDays(3),.7,.9,1.1,100,false);
            var sample=new MonitorSnapshot(now,system,processes,[new NetworkMetric("eth0",4096,8192,2048,1024)],[new DiskMetric("vda",4096,8192,1024,2048)],[new StorageMetric("ext4","/",16L<<30,4L<<30,12L<<30)],["Детерминированный UI fixture; I/O недоступен для части процессов."]);
            var history=new[]{new HistoryPoint(now.AddSeconds(-120),10,35,0,0),new HistoryPoint(now.AddSeconds(-119),30,36,0,0),new HistoryPoint(now.AddSeconds(-60),20,37,0,0),new HistoryPoint(now.AddSeconds(-59),50,38,0,0),new HistoryPoint(now,46,37.5,0,0)};
            monitorModel.ApplySnapshot(sample,history);
            var view=new SessionView(session,main,null);((SessionViewModel)Field(view,"viewModel")).StatusText="Данные стенда · без подключения";SetField(view,"files",files);SetField(view,"monitor",monitor);
            ((StackPanel)Field(view,"side")).Children.Add(files.Navigation);
            ((ContentControl)Field(view,"addressHost")).Content=files.AddressBar;((ContentControl)Field(view,"commandHost")).Content=files.CommandBar;((ContentControl)Field(view,"fileStatusHost")).Content=files.StatusBar;
            typeof(MainWindow).GetMethod("AddSessionTab",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(main,[profile,session,view]);
            main.Show();Pump();ShowMonitor(view,monitor);Layout(main);
            var table=(DataGrid)Field(monitor,"processes");table.SelectedItem=monitorModel.Processes[3];Layout(main);
            evidence.Capture((FrameworkElement)main.Content,"processes-wide",new{theme="Light",fixtureOnly=true,processCount=160,sampleTimestamp=now,selectedPid=103,verticalOffset=0,sort="fixture order",windowOuterDip=new{width=main.Width,height=main.Height}});
            main.Width=760;Layout(main);evidence.Capture((FrameworkElement)main.Content,"processes-narrow",new{theme="Light",fixtureOnly=true,windowOuterDip=new{width=main.Width,height=main.Height}});
            main.Width=1320;var innerTabs=Descendants<TabControl>(monitor).Single();innerTabs.SelectedIndex=1;Layout(main);evidence.Capture((FrameworkElement)main.Content,"performance-gap",new{theme="Light",fixtureOnly=true,actualHistoryGapsSeconds=new[]{1,59,1,59}});
            innerTabs.SelectedIndex=0;((ContentControl)Field(view,"screen")).Content=files;SetField(view,"section","files");SetNavigation(view,"files");
            foreach(string name in new[]{"addressHost","commandHost","fileStatusHost"})((ContentControl)Field(view,name)).Visibility=Visibility.Visible;
            Layout(main);var fileTable=(DataGrid)Field(files,"table");fileTable.SelectedItem=fileModel.Entries[5];var fileScroll=Descendants<ScrollViewer>(fileTable).First();fileScroll.ScrollToVerticalOffset(0);Layout(main);
            evidence.Capture((FrameworkElement)main.Content,"files-tree",new{theme="Light",fixtureOnly=true,fileCount=180,selectedFile="document-005.txt",verticalOffset=fileScroll.VerticalOffset});
            evidence.CaptureRelatedScenes(main,files,store);
            bool closed=false;main.Closed+=(_,_)=>closed=true;main.Close();WaitFor(()=>closed&&!app.Windows.Cast<Window>().Contains(main));app.Shutdown();
            Console.WriteLine("RESULT: exact published beta.2 actual-WPF client renders completed; no SSH or process signals; CI-only HWNDs");return 0;
        }
        catch(Exception ex){Console.WriteLine(ex);return 1;}
    }
    static void ShowMonitor(SessionView view,MonitorView monitor)
    {
        view.SetActive(false);SetField(view,"section","monitor");((ContentControl)Field(view,"screen")).Content=monitor;
        foreach(string name in new[]{"addressHost","commandHost","fileStatusHost"})((ContentControl)Field(view,name)).Visibility=Visibility.Collapsed;
        SetNavigation(view,"monitor");
    }
    static void SetNavigation(SessionView view,string key)
    {
        foreach(var pair in (Dictionary<string,Button>)Field(view,"navigationButtons"))
        {pair.Value.SetResourceReference(Control.BackgroundProperty,pair.Key==key?"Selected":"NavigationSurface");pair.Value.SetResourceReference(Control.BorderBrushProperty,"Accent");pair.Value.BorderThickness=pair.Key==key?new Thickness(2,0,0,0):new Thickness(0);}
    }
    static object Field(object target,string name)=>target.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(target)!;
    static TreeViewItem Node(FileBrowserView browser,string name,string path)=>(TreeViewItem)typeof(FileBrowserView).GetMethod("Node",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(browser,[name,path])!;
    static void SetField(object target,string name,object value)
    {
        if(target.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)is { } field)field.SetValue(target,value);
        else target.GetType().GetProperty(name,BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(target,value);
    }
    static void Invoke(object target,string name)=>target.GetType().GetMethod(name,BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(target,null);
    static string? Value(string[] args,string name){int i=Array.IndexOf(args,name);return i<0?null:i+1<args.Length?args[i+1]:throw new ArgumentException(name+" requires a value");}
    static void Layout(Window window){window.UpdateLayout();Pump();window.UpdateLayout();}
    static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,()=>frame.Continue=false);Dispatcher.PushFrame(frame);}
    static void WaitFor(Func<bool> done){var watch=System.Diagnostics.Stopwatch.StartNew();while(!done()){if(watch.Elapsed>TimeSpan.FromSeconds(15))throw new TimeoutException("Baseline window failed to close");Pump();}}
    static IEnumerable<T> Descendants<T>(DependencyObject root)where T:DependencyObject{for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is T item)yield return item;foreach(var nested in Descendants<T>(child))yield return nested;}}
}
