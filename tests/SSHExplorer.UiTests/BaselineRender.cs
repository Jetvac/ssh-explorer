using System.IO;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SSHExplorer.App;
using SSHExplorer.App.ViewModels;
using SSHExplorer.Core;
using SSHExplorer.Monitoring;

/// <summary>Compiled separately against the exact published beta.4 assemblies.
/// No later application API is referenced in this binary. All HWND creation is CI-only.</summary>
internal static class BaselineRender
{
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            CiVisualEvidence.RequireCi();
            if(!args.Contains("--render-beta4baseline",StringComparer.Ordinal))throw new ArgumentException("Explicit baseline render mode is required.");
            string output=Value(args,"--render-dir")??"artifacts/ui-beta5-before";
            var evidence=new CiVisualEvidence(output);
            var app=new App(startDesktop:false);app.InitializeComponent();app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
            CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("ru-RU");CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo("ru-RU");
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
            var store=new AppStore(Path.Combine(Environment.CurrentDirectory,".tools","beta4-render-store",Guid.NewGuid().ToString("N")));
            typeof(App).GetProperty(nameof(App.Store))!.SetValue(null,store);store.Settings.ShowHidden=true;
            Pump();
            if(!app.StartupCallbackObserved||app.DesktopStartupCount!=0||app.Windows.Count!=0||!ReferenceEquals(App.Store,store))throw new InvalidOperationException("Baseline startup was not safely suppressed before creating windows.");
            Console.WriteLine("SAFETY exact beta.4 App(false) consumed queued startup; desktop starts=0; no windows; isolated store identity unchanged");
            Theme.Apply("Light");
            var profile=new ServerProfile{Name="Linux · UI fixture",Host="127.0.0.1",Username="fixture"};
            store.Profiles.Add(profile);var session=new ServerSession(profile,store);var main=new MainWindow{Width=1320,Height=850,ShowActivated=false,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.Manual,Left=-10000,Top=-10000};
            using var files=new FileBrowserView(session,main);var monitorModel=new MonitorViewModel(()=>CompositionFixtures.Timestamp);var monitor=new MonitorView(monitorModel);
            var fileModel=(FileBrowserViewModel)files.DataContext;
            var entries=CompositionFixtures.Files();
            fileModel.CommitNavigation(fileModel.BeginNavigation(),"/srv/fixture",entries,true,[],0);fileModel.ApplyFilter(true,true);Invoke(files,"ApplyItems");Invoke(files,"UpdateBreadcrumbs");
            var tree=(TreeView)Field(files,"tree");tree.Items.Clear();
            var root=Node(files,"Корень /","/");var folder=Node(files,"fixture","/srv/fixture");root.Items.Clear();folder.Items.Clear();Ui.SetIsCurrentFolder(folder,true);root.Items.Add(folder);tree.Items.Add(root);root.IsExpanded=folder.IsExpanded=true;
            var now=CompositionFixtures.Timestamp;monitorModel.ApplySnapshot(CompositionFixtures.Snapshot(),CompositionFixtures.History());
            var view=new SessionView(session,main,files,monitor);((SessionViewModel)Field(view,"viewModel")).StatusText="Данные стенда · без подключения";view.SetActive(false);
            typeof(MainWindow).GetMethod("AddSessionTab",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(main,[profile,session,view]);
            main.Show();Pump();view.SetActive(false);view.ShowSection("processes");Layout(main);SetGraphTime(monitor);
            var table=(DataGrid)Field(monitor,"processes");table.SelectedItem=monitorModel.Processes[3];Layout(main);
            evidence.Capture((FrameworkElement)main.Content,"processes-wide",new{theme="Light",fixtureOnly=true,processCount=160,sampleTimestamp=now,selectedPid=103,verticalOffset=0,sort="fixture order",windowOuterDip=new{width=main.Width,height=main.Height}});
            main.Width=760;Layout(main);evidence.Capture((FrameworkElement)main.Content,"processes-narrow",new{theme="Light",fixtureOnly=true,windowOuterDip=new{width=main.Width,height=main.Height}});
            main.Width=1320;view.ShowSection("performance");Layout(main);SetGraphTime(monitor);evidence.Capture((FrameworkElement)main.Content,"performance-gap",new{theme="Light",fixtureOnly=true,actualHistoryGapsSeconds=new[]{1,59,1,59}});
            view.ShowSection("files");
            Layout(main);var fileTable=(DataGrid)Field(files,"table");fileTable.SelectedItem=fileModel.Entries[5];var fileScroll=Descendants<ScrollViewer>(fileTable).First();fileScroll.ScrollToVerticalOffset(0);Layout(main);
            evidence.Capture((FrameworkElement)main.Content,"files-tree",new{theme="Light",fixtureOnly=true,fileCount=180,selectedFile="document-005.txt",verticalOffset=fileScroll.VerticalOffset});
            evidence.CaptureRelatedScenes(main,files,store);
            bool closed=false;main.Closed+=(_,_)=>closed=true;main.Close();WaitFor(()=>closed&&!app.Windows.Cast<Window>().Contains(main));app.Shutdown();
            Console.WriteLine("RESULT: exact published beta.4 actual-WPF client renders completed; no SSH or process signals; CI-only HWNDs");return 0;
        }
        catch(Exception ex){Console.WriteLine(ex);return 1;}
    }
    static void SetGraphTime(MonitorView monitor){foreach(var graph in Descendants<MetricGraph>(monitor))graph.Now=CompositionFixtures.Timestamp;}
    static object Field(object target,string name)=>target.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(target)!;
    static TreeViewItem Node(FileBrowserView browser,string name,string path)=>(TreeViewItem)typeof(FileBrowserView).GetMethod("Node",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(browser,[name,path])!;
    static void Invoke(object target,string name)=>target.GetType().GetMethod(name,BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(target,null);
    static string? Value(string[] args,string name){int i=Array.IndexOf(args,name);return i<0?null:i+1<args.Length?args[i+1]:throw new ArgumentException(name+" requires a value");}
    static void Layout(Window window){window.UpdateLayout();Pump();window.UpdateLayout();}
    static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,()=>frame.Continue=false);Dispatcher.PushFrame(frame);}
    static void WaitFor(Func<bool> done){var watch=System.Diagnostics.Stopwatch.StartNew();while(!done()){if(watch.Elapsed>TimeSpan.FromSeconds(15))throw new TimeoutException("Baseline window failed to close");Pump();}}
    static IEnumerable<T> Descendants<T>(DependencyObject root)where T:DependencyObject{for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is T item)yield return item;foreach(var nested in Descendants<T>(child))yield return nested;}}
}
