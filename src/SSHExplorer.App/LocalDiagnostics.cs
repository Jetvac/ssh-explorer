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
                var line = $"{DateTimeOffset.Now:O}\t{safeCategory}\t{error?.GetType().FullName ?? "ok"}\t{error?.HResult.ToString("X8") ?? "0"}\n";
                await File.AppendAllTextAsync(path, line, Encoding.UTF8);
            }
            finally { Gate.Release(); }
        }
        catch { /* Logging must never interfere with a user's operation. */ }
    }
}
