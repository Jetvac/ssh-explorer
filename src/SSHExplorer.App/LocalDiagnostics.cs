using System.IO;
using System.Text;
using System.Text.Json;
using SSHExplorer.Core;

namespace SSHExplorer.App;

/// <summary>Bounded local diagnostics. Never stores exception messages, shell output, commands or credentials.</summary>
internal static class LocalDiagnostics
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    static string DirectoryPath => Path.Combine(App.Store?.DirectoryPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SSHExplorer"), "Logs");
    public static Task RecordAsync(string category, Exception? error = null) => AppendAsync(Format(Guid.Empty, category, "event", error));
    public static Task RecordOperationAsync(Guid operation, string stage, string outcome, Exception? error = null)
        => AppendAsync(Format(operation, stage, outcome, error));
    public static Task RecordStageAsync(ConnectionStageEvent value)
        => AppendAsync(JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, build = BuildInfo.Version, environment = BuildInfo.EnvironmentDescription,
            process = Environment.ProcessId, operation = value.OperationId, parent = value.ParentOperationId, session = value.SessionId,
            stage = value.Stage.ToString(), outcome = value.Outcome.ToString(), exception = value.ExceptionType, hresult = value.HResult?.ToString("X8"), stack = value.Stack is { Length: > 16000 } trace ? trace[..16000] : value.Stack }) + "\n");
    static string Format(Guid operation, string stage, string outcome, Exception? error)
    {
        var failures = new List<object>();
        for (var current = error; current is not null && failures.Count < 8; current = current.InnerException)
        {
            var frames = new System.Diagnostics.StackTrace(current, false).GetFrames() ?? [];
            failures.Add(new { type = current.GetType().FullName, hresult = current.HResult.ToString("X8"), methods = frames.Take(80).Select(f => f.GetMethod() is { } m ? $"{m.DeclaringType?.FullName}.{m.Name}" : "unknown").ToArray() });
        }
        static string Safe(string value) => new(value.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-').Take(80).ToArray());
        return JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, build = BuildInfo.Version, environment = BuildInfo.EnvironmentDescription,
            process = Environment.ProcessId, operation, stage = Safe(stage), outcome = Safe(outcome), failures }) + "\n";
    }
    static async Task AppendAsync(string text)
    {
        try
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try { await Task.Run(() => Append(text)).ConfigureAwait(false); }
            finally { Gate.Release(); }
        }
        catch { /* Diagnostics must not interfere with an operation. */ }
    }
    static void Append(string text)
    {
        Directory.CreateDirectory(DirectoryPath);
        var path = Path.Combine(DirectoryPath, "diagnostics.log");
        if (File.Exists(path) && new FileInfo(path).Length >= 1024 * 1024) File.Move(path, path + ".1", true);
        // A flushed record is used for stage boundaries and fatal handlers, never a fire-and-forget promise of crash capture.
        using var output = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        var bytes = Encoding.UTF8.GetBytes(text); output.Write(bytes); output.Flush(true);
    }
    public static void RecordCritical(string category, Exception? error = null)
        => RecordSynchronous(category, "fatal", error);
    public static void RecordSynchronous(string category, string outcome, Exception? error = null)
    {
        bool entered = false;
        try
        {
            entered = Gate.Wait(TimeSpan.FromMilliseconds(750));
            var text = Format(Guid.Empty, category, outcome, error);
            if (entered) Append(text);
            else
            {
                Directory.CreateDirectory(DirectoryPath);
                File.WriteAllText(Path.Combine(DirectoryPath, "fatal-emergency.log"), text, Encoding.UTF8);
            }
        }
        catch { /* Native crashes/StackOverflow may bypass every managed handler. */ }
        finally { if (entered) Gate.Release(); }
    }
}
