using System.IO;
using System.Text;

namespace SSHExplorer.App;

/// <summary>Bounded local diagnostics. Never stores exception messages, shell output, commands or credentials.</summary>
internal static class LocalDiagnostics
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public static async Task RecordAsync(string category, Exception? error = null)
    {
        try
        {
            await Gate.WaitAsync();
            try
            {
                var directory = Path.Combine(App.Store.DirectoryPath, "Logs");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "diagnostics.log");
                if (File.Exists(path) && new FileInfo(path).Length >= 1024 * 1024) File.Move(path, path + ".1", true);
                var safeCategory = new string(category.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-').Take(80).ToArray());
                var line = $"{DateTimeOffset.Now:O}\t{safeCategory}\t{BuildInfo.EnvironmentDescription}\n";
                // Messages can include hostnames, remote output or credentials. Record types,
                // HRESULTs and method names only, including inner exceptions; no file paths.
                for (var current = error; current is not null; current = current.InnerException)
                {
                    line += $"{current.GetType().FullName}\t{current.HResult:X8}\n";
                    var frames = new System.Diagnostics.StackTrace(current, false).GetFrames() ?? [];
                    foreach (var frame in frames.Take(100))
                    {
                        var method = frame.GetMethod();
                        line += $"  at {method?.DeclaringType?.FullName}.{method?.Name}\n";
                    }
                }
                await File.AppendAllTextAsync(path, line, Encoding.UTF8);
            }
            finally { Gate.Release(); }
        }
        catch { /* Logging must never interfere with a user's operation. */ }
    }
}
