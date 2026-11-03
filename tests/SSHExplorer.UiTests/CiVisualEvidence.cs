using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SSHExplorer.App;
using SSHExplorer.Core;

/// <summary>Captures actual laid-out WPF visuals in an explicitly authorized CI HWND.
/// This is a client-visual render, not a desktop screenshot or a native caption capture.</summary>
internal sealed class CiVisualEvidence
{
    readonly string directory;
    readonly List<object> captures=[];
    public static void RequireCi()
    {
        if(!string.Equals(Environment.GetEnvironmentVariable("CI"),"true",StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Actual-window visual checks are CI-only; no host windows are permitted.");
    }
    public CiVisualEvidence(string outputDirectory)
    {
        RequireCi();directory=Path.GetFullPath(outputDirectory);
        string workspace=Path.GetFullPath(Environment.CurrentDirectory)+Path.DirectorySeparatorChar;
        if(!directory.StartsWith(workspace,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Visual evidence output must stay inside the workspace.");
        Directory.CreateDirectory(directory);
    }
    public void Capture(FrameworkElement visual,string state,object? measurements=null)
    {
        RequireCi();visual.Dispatcher.VerifyAccess();
        var window=Window.GetWindow(visual);var source=PresentationSource.FromVisual(visual) as HwndSource;
        if(!visual.IsVisible||source is null||source.Handle==IntPtr.Zero||window is not null&&!window.IsVisible)
            throw new InvalidOperationException("Visual evidence requires the actual shown CI HWND.");
        if(state.Length==0||state.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c!='-'))throw new ArgumentException("Use a fixed safe state name.");
        visual.UpdateLayout();var dpi=VisualTreeHelper.GetDpi(visual);
        if(visual.ActualWidth<=0||visual.ActualHeight<=0)throw new InvalidOperationException("Visual evidence has no measured client area.");
        int width=(int)Math.Ceiling(visual.ActualWidth*dpi.DpiScaleX),height=(int)Math.Ceiling(visual.ActualHeight*dpi.DpiScaleY);
        var bitmap=new RenderTargetBitmap(width,height,96*dpi.DpiScaleX,96*dpi.DpiScaleY,PixelFormats.Pbgra32);bitmap.Render(visual);
        string path=Path.Combine(directory,state+".png");
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(path))encoder.Save(stream);
        captures.Add(new
        {
            state,file=Path.GetFileName(path),sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
            scope=window is null?"actual WPF popup visual":"actual WPF client visual",
            clientDip=new{width=visual.ActualWidth,height=visual.ActualHeight},pixels=new{width,height},
            dpi=new{x=96*dpi.DpiScaleX,y=96*dpi.DpiScaleY},systemHighContrast=SystemParameters.HighContrast,
            systemClientAreaAnimation=SystemParameters.ClientAreaAnimation,
            uiFontSize=visual.TryFindResource("UIFontSize"),measurements
        });
        File.WriteAllText(Path.Combine(directory,"visual-evidence.json"),JsonSerializer.Serialize(new
        {
            utc=DateTimeOffset.UtcNow,build=BuildInfo.Version,windows=Environment.OSVersion.VersionString,
            renderer="RenderTargetBitmap of actual WPF client/popup visual after CI HWND Show; native caption excluded",
            fixture="Deterministic offline component data; no SSH, no real process signals, no user settings",
            physicalKeyboardTested=false,liveMotionEvaluated=false,captures
        },new JsonSerializerOptions{WriteIndented=true}));
    }
    public void CaptureRelatedScenes(MainWindow main,FileBrowserView files,AppStore store,Action<bool,string>? assert=null)
    {
        RequireCi();
        var button=Descendants<Button>(files.CommandBar).Single(b=>AutomationProperties.GetName(b)=="Просмотреть");button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
        var menu=PresentationSource.CurrentSources.Cast<PresentationSource>().SelectMany(source=>source.RootVisual is { } root?Descendants<ContextMenu>(root):[]).Single(m=>m.IsOpen);
        assert?.Invoke(ReferenceEquals(menu.PlacementTarget,button),"actual file view flyout remains anchored to the invoking command");
        Capture(menu,"files-view-menu",new{theme="Light",fixtureOnly=true,placement=menu.Placement.ToString()});menu.IsOpen=false;Pump();
        var tabs=(TabControl)typeof(MainWindow).GetField("tabs",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(main)!;tabs.SelectedIndex=0;main.UpdateLayout();Pump();Capture((FrameworkElement)main.Content,"home",new{theme="Light",fixtureOnly=true});
        var dialog=new ProfileDialog(new ProfilePersistence(store),owner:main){Width=540};dialog.Show();Pump();
        try
        {
            Descendants<Button>(dialog).Single(b=>Equals(b.Content,"Сохранить")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();dialog.UpdateLayout();
            assert?.Invoke(dialog.Model.NameError.Length>0&&dialog.Model.HostError.Length>0,"actual profile validation keeps the window open with inline errors");
            Capture((FrameworkElement)dialog.Content,"profile-validation",new{theme="Light",fixtureOnly=true});
        }
        finally{dialog.Close();Pump();}
    }
    static void Pump(){var frame=new System.Windows.Threading.DispatcherFrame();System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,()=>frame.Continue=false);System.Windows.Threading.Dispatcher.PushFrame(frame);}
    static IEnumerable<T> Descendants<T>(DependencyObject root)where T:DependencyObject{for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is T item)yield return item;foreach(var nested in Descendants<T>(child))yield return nested;}}
}
