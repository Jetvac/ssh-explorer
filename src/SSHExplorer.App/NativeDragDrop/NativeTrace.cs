using System.IO;

namespace SSHExplorer.App.NativeDragDrop;

internal static class NativeTrace
{
#if DEBUG
    private static readonly object gate = new();
    private static int lines;
#endif
    [System.Diagnostics.Conditional("DEBUG")]
    internal static void Write(string message)
    {
#if DEBUG
        var path = Environment.GetEnvironmentVariable("SSHX_NATIVE_TRACE");
        if (string.IsNullOrEmpty(path) || Interlocked.Increment(ref lines) > 2000) return;
        lock (gate) try { File.AppendAllText(path, $"{DateTime.UtcNow:HH:mm:ss.fff} P{Environment.ProcessId} T{Environment.CurrentManagedThreadId} {message}\n"); } catch { }
#endif
    }
}
