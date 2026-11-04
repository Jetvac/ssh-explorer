using System.IO;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
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
    internal static bool SoftwarePointerUsed{get;set;}
    public static void RequireCi()
    {
        if(!string.Equals(Environment.GetEnvironmentVariable("CI"),"true",StringComparison.OrdinalIgnoreCase)||
           !string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"),"true",StringComparison.OrdinalIgnoreCase)||
           !long.TryParse(Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),out _))
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
        // Observe the settled static state; do not alter or fast-forward product clocks.
        // This CI-only observation wait is unrelated to command execution latency.
        var settleFrame=new System.Windows.Threading.DispatcherFrame();var settleTimer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(300)};
        settleTimer.Tick+=(_,_)=>{settleTimer.Stop();settleFrame.Continue=false;};settleTimer.Start();
        try{System.Windows.Threading.Dispatcher.PushFrame(settleFrame);}finally{settleTimer.Stop();}
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
            scope=window is null?"actual WPF popup visual":"actual WPF client visual",settleMilliseconds=300,
            clientDip=new{width=visual.ActualWidth,height=visual.ActualHeight},pixels=new{width,height},
            windowGeometry=window is null?null:new{actualWidth=Finite(window.ActualWidth),actualHeight=Finite(window.ActualHeight),requestedWidth=Finite(window.Width),requestedHeight=Finite(window.Height),sizeToContent=window.SizeToContent.ToString()},
            culture=CultureInfo.CurrentCulture.Name,uiCulture=CultureInfo.CurrentUICulture.Name,
            windowActive=window?.IsActive,keyboardFocusType=Keyboard.FocusedElement?.GetType().Name,keyboardFocusElement=(Keyboard.FocusedElement as FrameworkElement)?.Name,
            dpi=new{x=96*dpi.DpiScaleX,y=96*dpi.DpiScaleY},systemHighContrast=SystemParameters.HighContrast,
            systemClientAreaAnimation=SystemParameters.ClientAreaAnimation,
            softwarePointerInjected=SoftwarePointerUsed,physicalMouseTested=false,
            uiFontSize=visual.TryFindResource("UIFontSize"),measurements
        });
        File.WriteAllText(Path.Combine(directory,"visual-evidence.json"),JsonSerializer.Serialize(new
        {
            utc=DateTimeOffset.UtcNow,build=BuildInfo.Version,commit=BuildInfo.Commit,windows=Environment.OSVersion.VersionString,
            appAssembly=new
            {
                file=Path.GetFileName(typeof(App).Assembly.Location),
                informationalVersion=typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                fileVersion=System.Diagnostics.FileVersionInfo.GetVersionInfo(typeof(App).Assembly.Location).FileVersion,
                sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(App).Assembly.Location))).ToLowerInvariant()
            },
            renderer="RenderTargetBitmap of actual WPF client/popup visual after CI HWND Show; native caption excluded",nativeCaptionExcluded=true,
            primaryScreenDip=new{width=SystemParameters.PrimaryScreenWidth,height=SystemParameters.PrimaryScreenHeight},
            fixture="Deterministic offline component data; no SSH, no real process signals, no user settings",
            physicalKeyboardTested=false,physicalMouseTested=false,softwarePointerInjected=SoftwarePointerUsed,liveMotionEvaluated=false,captures
        },new JsonSerializerOptions{WriteIndented=true}));
    }
    public void CaptureRelatedScenes(MainWindow main,FileBrowserView files,AppStore store,Action<bool,string>? assert=null,bool includeNarrowValidation=false)
    {
        RequireCi();
        var button=Descendants<Button>(files.CommandBar).Single(b=>AutomationProperties.GetName(b)=="Просмотреть");button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
        var menu=PresentationSource.CurrentSources.Cast<PresentationSource>().SelectMany(source=>source.RootVisual is { } root?Descendants<ContextMenu>(root):[]).Single(m=>m.IsOpen);
        assert?.Invoke(ReferenceEquals(menu.PlacementTarget,button),"actual file view flyout remains anchored to the invoking command");
        Capture(menu,"files-view-menu",new{theme="Light",fixtureOnly=true,placement=menu.Placement.ToString()});menu.IsOpen=false;Pump();
        var tabs=(TabControl)typeof(MainWindow).GetField("tabs",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(main)!;tabs.SelectedIndex=0;main.UpdateLayout();Pump();Capture((FrameworkElement)main.Content,"home",new{theme="Light",fixtureOnly=true});
        var dialog=new ProfileDialog(new ProfilePersistence(store),owner:main,allowConnect:true){Width=540};dialog.Show();Pump();
        try
        {
            Descendants<Button>(dialog).Single(b=>Equals(b.Content,"Сохранить и подключиться")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();dialog.UpdateLayout();
            assert?.Invoke(dialog.Model.NameError.Length>0&&dialog.Model.HostError.Length>0,"actual profile validation keeps the window open with inline errors");
            Capture((FrameworkElement)dialog.Content,"profile-validation",new{theme="Light",fixtureOnly=true});
            var authentication=Descendants<ComboBox>(dialog).Single(combo=>AutomationProperties.GetName(combo)=="Способ входа");
            authentication.Focus();SendKey(authentication,Key.F4);Pump();dialog.UpdateLayout();
            var popup=authentication.Template.FindName("PART_Popup",authentication) as Popup??throw new InvalidOperationException("The applied authentication ComboBox has no PART_Popup.");
            if(!authentication.IsDropDownOpen||popup.Child is not FrameworkElement popupVisual||!popupVisual.IsVisible)throw new InvalidOperationException("F4 did not open the actual authentication popup.");
            var items=Descendants<ComboBoxItem>(popupVisual).Where(item=>item.IsVisible).ToArray();
            assert?.Invoke(items.Length==4&&popupVisual.ActualWidth+1>=authentication.ActualWidth&&items.All(item=>item.ActualHeight>20),"applied authentication popup exposes all four real selectable rows (password, key, keyboard-interactive, SSH agent) at the field width");
            Capture(popupVisual,"profile-auth-popup",new{theme="Light",fixtureOnly=true,openedBy="routed F4 KeyDown",physicalKeyboardInput=false,selectedIndex=authentication.SelectedIndex,fieldWidthDip=authentication.ActualWidth,realizedItems=items.Length});
            SendKey(authentication,Key.Escape);Pump();
            assert?.Invoke(!authentication.IsDropDownOpen&&dialog.IsVisible,"Escape closes the actual ComboBox popup without cancelling its owner dialog");
            var name=Descendants<TextBox>(dialog).Single(input=>AutomationProperties.GetName(input)=="Название");
            var host=Descendants<TextBox>(dialog).Single(input=>AutomationProperties.GetName(input)=="Адрес сервера");
            var user=Descendants<TextBox>(dialog).Single(input=>AutomationProperties.GetName(input)=="Пользователь Linux");
            name.Text="Лаборатория · тестовый сервер";host.Text="lab.example.invalid";user.Text="fixture";name.Focus();Pump();dialog.UpdateLayout();
            Capture((FrameworkElement)dialog.Content,"profile-filled",new{theme="Light",fixtureOnly=true,syntheticFields=true,authentication="Password",secretProvided=false});
            assert?.Invoke(dialog.Model.FirstInvalidField is null&&dialog.Model.Status.Length==0,"correcting all ordinary profile fields clears the obsolete validation summary without submitting or saving");
            if(includeNarrowValidation)
            {
                name.Clear();host.Clear();user.Clear();
                Descendants<Button>(dialog).Single(button=>Equals(button.Content,"Сохранить и подключиться")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
                dialog.Width=430;dialog.UpdateLayout();Pump();var content=(FrameworkElement)dialog.Content;
                var actions=Descendants<Button>(dialog).Where(button=>button.IsVisible&&button.Content is string name&&name is "Отмена" or "Сохранить" or "Сохранить и подключиться").ToArray();
                var bounds=actions.Select(button=>button.TransformToAncestor(content).TransformBounds(new Rect(button.RenderSize))).ToArray();bool overlap=false;
                for(int i=0;i<bounds.Length;i++)for(int j=i+1;j<bounds.Length;j++){var shared=Rect.Intersect(bounds[i],bounds[j]);if(!shared.IsEmpty&&shared.Width>.5&&shared.Height>.5)overlap=true;}
                assert?.Invoke(actions.Length==3&&!overlap&&bounds.All(rect=>rect.Left>=-.5&&rect.Right<=content.ActualWidth+.5&&rect.Bottom<=content.ActualHeight+.5),"narrow ordinary profile footer wraps all three real actions without overlap or clipping");
                Capture(content,"profile-validation-narrow",new{theme="Light",fixtureOnly=true,widthRoleDip=430});
            }
        }
        finally{dialog.Close();Pump();}
    }
    static void SendKey(UIElement target,Key key)
    {
        var source=PresentationSource.FromVisual(target)??throw new InvalidOperationException("Key handler evidence requires a CI presentation source.");
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,source,Environment.TickCount,key){RoutedEvent=Keyboard.KeyDownEvent});
    }
    static void Pump(){var frame=new System.Windows.Threading.DispatcherFrame();System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,()=>frame.Continue=false);System.Windows.Threading.Dispatcher.PushFrame(frame);}
    static double? Finite(double value)=>double.IsFinite(value)?value:null;
    static IEnumerable<T> Descendants<T>(DependencyObject root)where T:DependencyObject{for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is T item)yield return item;foreach(var nested in Descendants<T>(child))yield return nested;}}
}
