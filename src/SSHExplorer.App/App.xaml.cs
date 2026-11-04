using System.Windows;
using SSHExplorer.Core;

namespace SSHExplorer.App;

public partial class App : Application
{
    readonly bool startDesktop = true;
    public App() { }
    // WPF queues Application.OnStartup from its constructor, even when a component
    // runner only pumps Dispatcher and never calls Application.Run. Test resources
    // must not implicitly start the desktop or read the ordinary settings directory.
    internal App(bool startDesktop) => this.startDesktop = startDesktop;
    internal bool StartupCallbackObserved { get; private set; }
    internal int DesktopStartupCount { get; private set; }
    public static AppStore Store { get; private set; } = null!;
    public static TransferQueue Transfers { get; } = new();
    protected override async void OnStartup(StartupEventArgs e)
    {
        StartupCallbackObserved = true;
        if (!startDesktop) return;
        DesktopStartupCount++;
        base.OnStartup(e);
        EventManager.RegisterClassHandler(typeof(Window), Window.LoadedEvent, new RoutedEventHandler((sender, _) => { AppIcon.Apply((Window)sender); Theme.ApplyWindowFrame((Window)sender); }));
        DispatcherUnhandledException += (_, args) =>
        {
            UiTestDiagnostics.Record(args.Exception);
            LocalDiagnostics.RecordCritical("ui.unhandled.failure", args.Exception);
            // An unknown dispatcher failure may leave partial UI state. Preserve the normal
            // fatal policy instead of silently continuing in a damaged session.
            args.Handled = false;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LocalDiagnostics.RecordCritical("runtime.unhandled.failure", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => LocalDiagnostics.RecordSynchronous("task.unobserved.failure", "observed", args.Exception);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => LocalDiagnostics.RecordSynchronous("process.exit", "observed");
        try
        {
            Store = new AppStore(TestSessionHarness.TryGetStoreDirectory(e.Args));
            Theme.Apply(Store.Settings.Theme);
            SystemParameters.StaticPropertyChanged += (_, _) => Dispatcher.BeginInvoke(() => Theme.Apply(Store.Settings.Theme));
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, _) => Dispatcher.BeginInvoke(() => { SystemFileIconProvider.Invalidate(); Theme.Apply(Store.Settings.Theme); });
            Transfers.ResolveConflict = conflict => Dispatcher.InvokeAsync(() => Dialogs.Conflict(conflict)).Task;
            MainWindow = new MainWindow(); MainWindow.Show();
            await LocalDiagnostics.RecordAsync("app.started");
            await TestSessionHarness.StartAsync(this, e.Args);
        }
        catch (Exception ex) { LocalDiagnostics.RecordCritical("app.startup.failed", ex); MessageBox.Show(ErrorMessages.Describe(ex), "SSH Explorer — запуск", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1); }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        if (startDesktop) LocalDiagnostics.RecordSynchronous("app.exit", e.ApplicationExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        base.OnExit(e);
    }
}
