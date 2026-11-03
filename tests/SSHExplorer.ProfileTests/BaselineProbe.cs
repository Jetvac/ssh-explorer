using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using SSHExplorer.App;
using SSHExplorer.Core;

internal static class BaselineProbe
{
    [STAThread]
    static int Main(string[] args)
    {
        var directory=Path.Combine(Environment.CurrentDirectory,".tools","profile-regression",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var app=new App();app.InitializeComponent();app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            typeof(App).GetProperty(nameof(App.Store),BindingFlags.Public|BindingFlags.Static)!.SetValue(null,new AppStore(directory));
            Theme.Apply("Light");
            var windowStyle=new Style(typeof(Window),(Style)app.Resources[typeof(Window)]);
            windowStyle.Setters.Add(new Setter(Window.ShowActivatedProperty,false));
            windowStyle.Setters.Add(new Setter(Window.WindowStateProperty,WindowState.Minimized));
            app.Resources[typeof(Window)]=windowStyle;
            if(args.Contains("--selection-only",StringComparer.Ordinal))
            {
                App.Store.Profiles.Add(new ServerProfile{Name="Baseline saved profile",Host="offline.example.invalid",Username="tester"});
                App.Store.SaveAsync().GetAwaiter().GetResult();
                Console.WriteLine("BASELINE_DLL="+typeof(App).Assembly.Location);
                Console.WriteLine("BASELINE_DLL_SHA256="+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(App).Assembly.Location))));
                var unseen=new MainWindow();app.MainWindow=unseen;
                var content=(FrameworkElement)unseen.Content;content.Measure(new Size(1320,800));content.Arrange(new Rect(0,0,1320,800));content.UpdateLayout();
                var savedGrid=Logical<DataGrid>(unseen).First();Console.WriteLine("BASELINE_ROWS="+savedGrid.Items.Count);
                Console.WriteLine("BASELINE selecting saved row without showing any window");
                savedGrid.SelectedIndex=0;
                Console.WriteLine("PASS baseline unshown row selection survived");return 0;
            }
            var owner=new Window{Title="Isolated STA profile regression",ShowInTaskbar=false,ShowActivated=false,WindowState=WindowState.Minimized};
            app.MainWindow=owner;owner.Show();
            Console.WriteLine("BASELINE direct Dialogs.Profile construction/save starts");
            int ticks=0;
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(25)};
            timer.Tick+=(_,_)=>
            {
                if(++ticks>100){timer.Stop();throw new TimeoutException("Profile dialog did not become available");}
                var dialog=app.Windows.Cast<Window>().FirstOrDefault(x=>x!=owner&&x.Title.Contains("сервер",StringComparison.OrdinalIgnoreCase));
                if(dialog is null)return;
                timer.Stop();
                foreach(var field in Logical<TextBox>(dialog))
                {
                    string name=AutomationProperties.GetName(field);
                    if(name=="Название")field.Text="Проверка STA";
                    if(name=="DNS-имя, IPv4 или IPv6")field.Text="offline.example.invalid";
                    if(name=="Пользователь Linux")field.Text="tester";
                }
                var save=Logical<Button>(dialog).First(x=>x.Content?.ToString()=="Сохранить");
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            timer.Start();var profile=Dialogs.Profile();timer.Stop();
            if(profile is null||profile.Name!="Проверка STA"||profile.Host!="offline.example.invalid")throw new Exception("Direct Profile dialog save did not return entered profile");
            Console.WriteLine("PASS direct Dialogs.Profile opens and saves offline DNS without losing input");
            Console.WriteLine("BASELINE full MainWindow AddProfile -> persisted row -> row selection starts");
            var main=new MainWindow{ShowActivated=false,ShowInTaskbar=false,WindowState=WindowState.Minimized};app.MainWindow=main;main.Show();
            var dialogTimer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(30)};
            dialogTimer.Tick+=(_,_)=>
            {
                var dialog=app.Windows.Cast<Window>().FirstOrDefault(x=>x!=owner&&x!=main&&x.Title.Contains("сервер",StringComparison.OrdinalIgnoreCase));if(dialog is null)return;
                dialogTimer.Stop();
                foreach(var field in Logical<TextBox>(dialog))
                {
                    string name=AutomationProperties.GetName(field);
                    if(name=="Название")field.Text="Baseline complete flow";
                    if(name=="DNS-имя, IPv4 или IPv6")field.Text="offline.example.invalid";
                    if(name=="Пользователь Linux")field.Text="tester";
                }
                Logical<Button>(dialog).First(x=>x.Content?.ToString()=="Сохранить").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            var add=Logical<Button>(main).First(x=>AutomationProperties.GetName(x).Contains("Добавить сервер",StringComparison.Ordinal));
            dialogTimer.Start();add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));dialogTimer.Stop();
            var grid=Logical<DataGrid>(main).First();
            var wait=new DispatcherFrame();var waitTimer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(30)};int waitTicks=0;
            waitTimer.Tick+=(_,_)=>{if(grid.Items.Count>0||++waitTicks>100)wait.Continue=false;};waitTimer.Start();Dispatcher.PushFrame(wait);waitTimer.Stop();
            if(grid.Items.Count!=1)throw new Exception("Baseline full AddProfile did not populate grid");
            Console.WriteLine("BASELINE row persisted; selecting the added row now");
            grid.SelectedIndex=0;
            Console.WriteLine("PASS baseline row selection survived");
            main.Close();owner.Close();app.Shutdown();return 0;
        }
        catch(Exception ex)
        {
            Console.WriteLine(ex.ToString());return 1;
        }
    }
    static IEnumerable<T> Logical<T>(DependencyObject parent)where T:DependencyObject
    {
        foreach(var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if(child is T typed)yield return typed;
            foreach(var nested in Logical<T>(child))yield return nested;
        }
    }
}
