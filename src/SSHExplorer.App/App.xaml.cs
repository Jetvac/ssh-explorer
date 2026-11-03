using System.Windows;
using SSHExplorer.Core;

namespace SSHExplorer.App;

public partial class App : Application
{
    public static AppStore Store { get; private set; } = null!;
    public static TransferQueue Transfers { get; } = new();
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        EventManager.RegisterClassHandler(typeof(Window), Window.LoadedEvent, new RoutedEventHandler((sender, _) => Theme.ApplyWindowFrame((Window)sender)));
        DispatcherUnhandledException += (_, args) => { UiTestDiagnostics.Record(args.Exception); _ = LocalDiagnostics.RecordAsync("ui.unhandled.failure", args.Exception); Ui.Error(ErrorMessages.Describe(args.Exception)); args.Handled = true; };
        try
        {
            Store = new AppStore(TestSessionHarness.TryGetStoreDirectory(e.Args));
            Theme.Apply(Store.Settings.Theme);
            SystemParameters.StaticPropertyChanged += (_, _) => Dispatcher.BeginInvoke(() => Theme.Apply(Store.Settings.Theme));
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, _) => Dispatcher.BeginInvoke(() => Theme.Apply(Store.Settings.Theme));
            Transfers.ResolveConflict = conflict => Dispatcher.InvokeAsync(() => Dialogs.Conflict(conflict)).Task;
            MainWindow = new MainWindow(); MainWindow.Show();
            await LocalDiagnostics.RecordAsync("app.started");
            await TestSessionHarness.StartAsync(this, e.Args);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "SSH Explorer — запуск", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1); }
    }
}
