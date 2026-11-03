using System.Collections.Specialized;
using System.IO;
using System.Reflection;
using System.Text.Json;
using SSHExplorer.App;
using SSHExplorer.App.ViewModels;
using SSHExplorer.Core;

internal static class ConnectionTests
{
    public static int Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sshx-connection-tests-" + Guid.NewGuid().ToString("N"));
        App? app = null;
        try
        {
            app = TestApplication.Create();
            typeof(App).GetProperty(nameof(App.Store))!.SetValue(null, new AppStore(directory));
            int count = 0;
            void Check(bool ok, string text) { if (!ok) throw new Exception(text); ++count; Console.WriteLine("PASS " + text); }
            using var owner = new CancellationTokenSource(); var registry = new ConnectionAttemptRegistry(owner.Token); var profile = Guid.NewGuid();
            using var first = registry.TryBegin(profile)!;
            Check(registry.TryBegin(profile) is null, "duplicate profile command is rejected before credentials/trust can queue");
            using var independent = registry.TryBegin(profile, independent: true)!;
            Check(first.Id != independent.Id && !independent.Token.IsCancellationRequested, "explicit independent connection owns a separate operation and cancellation");
            first.Cancel(); Check(first.Token.IsCancellationRequested && !independent.Token.IsCancellationRequested, "cancelling one operation leaves the independent attempt alive");
            first.Dispose(); using var retry = registry.TryBegin(profile)!;
            Check(retry is not null && retry.Id != first.Id, "completed attempt releases its registry slot for a retry");
            owner.Cancel(); Check(retry!.Token.IsCancellationRequested && independent.Token.IsCancellationRequested, "owner shutdown cancels all remaining operations");
            var marker = "SECRET-RESPONSE-" + Guid.NewGuid().ToString("N");
            Exception failure; try { ThrowSensitive(marker); throw new Exception(); } catch (IOException ex) { failure = ex; }
            first.RecordAsync("test.safe.failure", "failed", failure).GetAwaiter().GetResult(); first.FlushAsync().GetAwaiter().GetResult();
            LocalDiagnostics.RecordCritical("test.fatal.failure", failure);
            LocalDiagnostics.RecordSynchronous("test.exit", "7");
            var log = File.ReadAllText(Path.Combine(directory, "Logs", "diagnostics.log"));
            var records = log.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => JsonDocument.Parse(s)).ToArray();
            Check(records.Length == 3 && records[0].RootElement.GetProperty("operation").GetGuid() == first.Id, "operation, fatal and termination records are durable before returning");
            Check(!log.Contains(marker, StringComparison.Ordinal) && log.Contains(nameof(ThrowSensitive), StringComparison.Ordinal) && log.Contains("System.IO.IOException", StringComparison.Ordinal), "diagnostics retain exception type and method stack while excluding message, inner message and Data secrets");
            Check(records.All(r => r.RootElement.GetProperty("build").GetString()!.Length > 0 && r.RootElement.GetProperty("environment").GetString()!.Length > 0 && r.RootElement.GetProperty("process").GetInt32() == Environment.ProcessId), "safe diagnostic records identify build, runtime environment and process");
            foreach (var record in records) record.Dispose();
            Check(!new SessionCredentials(marker).ToString().Contains(marker, StringComparison.Ordinal), "credential record string cannot expose its secret");
            var list = new TransferListViewModel(); var one = new TransferJob(); var two = new TransferJob(); list.Refresh([one, two]); var retained = list.Rows[1];
            int reset = 0, changes = 0, properties = 0;
            list.Rows.CollectionChanged += (_, e) => { changes++; if (e.Action == NotifyCollectionChangedAction.Reset) reset++; };
            retained.PropertyChanged += (_, _) => properties++;
            list.Refresh([one, two]); Check(changes == 0 && properties == 0 && ReferenceEquals(retained, list.Rows[1]), "unchanged transfer refresh preserves row identity without collection reset or property churn");
            typeof(TransferJob).GetProperty(nameof(TransferJob.State))!.SetValue(two, TransferState.Completed);
            list.Refresh([one, two]); Check(properties > 0 && reset == 0 && retained.State == "Завершено" && retained.Progress.Contains("100"), "transfer completion updates displayed properties on the existing row");
            list.Refresh([two, new TransferJob()]); Check(reset == 0 && ReferenceEquals(retained, list.Rows[0]) && list.Rows.Count == 2, "transfer pruning and additions preserve surviving rows and selection identity");
            Check(app.Windows.Count == 0, "connection and diagnostics checks create no Window");
            app.Shutdown(); Console.WriteLine($"RESULT: {count} headless operation/diagnostics/transfer checks passed"); return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); app?.Shutdown(); return 1; }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    static void ThrowSensitive(string secret) => throw new IOException(secret, new InvalidOperationException(secret)) { Data = { ["server-response"] = secret } };
}
