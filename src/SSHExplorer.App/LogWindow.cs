using System.Windows;
using SSHExplorer.Core;

namespace SSHExplorer.App;

/// <summary>A server file followed like <c>tail -F</c> (survives rotation), with the same filter as the services journal.</summary>
public sealed class LogWindow : Window
{
    internal LogView View { get; } = new(showTitle: false);

    public LogWindow(string title, LogView.LogSource source)
    {
        Title = "Журнал: " + title; Width = 1000; Height = 680; MinWidth = 560; MinHeight = 320; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = true; Style = (Style)FindResource(typeof(Window));
        View.SetResourceReference(BackgroundProperty, "Paper");
        Content = View;
        Loaded += (_, _) => View.Open(title, source);
        Closed += (_, _) => View.Dispose();
    }

    /// <summary>tail of a server file; follows with -F until the window is closed.</summary>
    public static LogView.LogSource Tail(ServerSession session, string path) =>
        (lines, follow, onLine, ct) => session.IsWindows ? session.StreamLinesAsync(WindowsServices.TailCommand(path, lines, follow), onLine, ct) : session.StreamLinesAsync(follow ? LogFollow.Wrap(Systemd.TailCommand(path, lines, true)) : Systemd.TailCommand(path, lines, false), onLine, ct);
}
