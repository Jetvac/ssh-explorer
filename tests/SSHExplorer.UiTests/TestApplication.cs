using System.Windows;
using System.Windows.Threading;
using SSHExplorer.App;

internal static class TestApplication
{
    public static App Create()
    {
        var app = new App(startDesktop: false); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        // Exercise the startup callback deliberately, while the constructor-level
        // suppression is already in effect. The real App.OnStartup must never run.
        var frame = new DispatcherFrame();
        app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
        if (!app.StartupCallbackObserved || app.DesktopStartupCount != 0 || app.Windows.Count != 0 || App.Store is not null)
            throw new InvalidOperationException("Resource runner started a desktop or initialized ordinary settings.");
        Console.WriteLine("SAFETY queued WPF startup consumed; desktop starts=0, application windows=0, ordinary settings unopened");
        return app;
    }
}
