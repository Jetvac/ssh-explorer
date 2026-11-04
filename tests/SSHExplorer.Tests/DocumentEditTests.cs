#if CORE_TESTS
using System.Text;
using SSHExplorer.Core;

internal static class DocumentEditTests
{
    public static async Task RunAsync(TestSuite suite)
    {
        await suite.Run("document strict detection covers BOM UTF-16 no-BOM UTF-8 and mixed endings without rewriting bytes", () =>
        {
            foreach (var encoding in new Encoding[] { new UTF8Encoding(false, true), new UTF8Encoding(true, true), new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true), new UnicodeEncoding(false, false, true) })
            { var content = encoding.GetPreamble().Concat(encoding.GetBytes("hello\r\nмир\nlast")).ToArray(); var decoded = DocumentText.Decode(content); TestSuite.Equal("hello\r\nмир\nlast", decoded.Text); TestSuite.Equal("LF+CRLF", decoded.Format.Newlines); TestSuite.True(!decoded.Format.FinalNewline); }
            try { DocumentText.Decode([0xff, 0x81, 0x80]); throw new Exception("Invalid UTF-8 accepted"); } catch (IOException) { }
            try { DocumentText.Decode([0, 1, 2, 3, 0, 0, 0, 0]); throw new Exception("Binary accepted"); } catch (IOException) { }
        });
        await Case(suite, "document empty save is confirmed and a duplicate signal does not upload again", async (d, t) =>
        {
            await File.WriteAllBytesAsync(d.WorkingPath, []); await d.CheckAsync(true); await d.CheckAsync(true);
            TestSuite.Equal(1, t.Writes); TestSuite.Equal(0, t.Bytes.Length); TestSuite.Equal(RemoteEditState.Confirmed, d.Record.State);
        });
        await Case(suite, "document disappearance preserves server bytes and last snapshot", async (d, t) =>
        {
            var original = t.Bytes.ToArray(); File.Delete(d.WorkingPath); await d.CheckAsync(true);
            TestSuite.True(original.SequenceEqual(t.Bytes)); TestSuite.Equal(0, t.Writes); TestSuite.Equal(RemoteEditState.Error, d.Record.State);
            var retained = await File.ReadAllBytesAsync(d.SnapshotPath); TestSuite.True(original.SequenceEqual(retained));
        });
        await Case(suite, "document detects external same-length same-mtime content and rechecks a replaced approved version", async (d, t) =>
        {
            await File.WriteAllTextAsync(d.WorkingPath, "ours\n"); t.Bytes = Encoding.UTF8.GetBytes("peer\n"); await d.CheckAsync(true);
            TestSuite.Equal(RemoteEditState.Conflict, d.Record.State); TestSuite.Equal(0, t.Writes);
            var approved = await d.ReadServerAsync(); t.Bytes = Encoding.UTF8.GetBytes("next\n"); await d.ReplaceApprovedVersionAsync(approved);
            TestSuite.Equal(RemoteEditState.Conflict, d.Record.State); TestSuite.Equal("next\n", Encoding.UTF8.GetString(t.Bytes));
        });
        await Case(suite, "document format conversion requires approval bound to the actual snapshot hash", async (d, t) =>
        {
            await File.WriteAllTextAsync(d.WorkingPath, "hello\r\n"); await d.CheckAsync(true);
            TestSuite.Equal(RemoteEditState.FormatConfirmation, d.Record.State); TestSuite.Equal(0, t.Writes);
            await d.ResumeAsync(t, true); TestSuite.Equal("hello\r\n", Encoding.UTF8.GetString(t.Bytes)); TestSuite.Equal(1, t.Writes);
        });
        await Case(suite, "document immutable snapshots serialize rapid writes and a later version wins", async (d, t) =>
        {
            t.Delay = 250; await File.WriteAllTextAsync(d.WorkingPath, "first\n"); var sending = d.CheckAsync(true);
            await Task.Delay(300); await File.WriteAllTextAsync(d.WorkingPath, "newest\n"); await sending; await d.CheckAsync(true);
            TestSuite.Equal("newest\n", Encoding.UTF8.GetString(t.Bytes)); TestSuite.Equal(1, t.MaxActive); TestSuite.Equal(RemoteEditState.Confirmed, d.Record.State);
        });
        await Case(suite, "document failed upload never replaces baseline and retains snapshot", async (d, t) =>
        {
            t.FailBeforeCommit = true; await File.WriteAllTextAsync(d.WorkingPath, "pending\n"); await d.CheckAsync(true);
            TestSuite.Equal(RemoteEditState.Error, d.Record.State); TestSuite.Equal("original\n", Encoding.UTF8.GetString(t.Bytes)); TestSuite.True(File.Exists(d.SnapshotPath)); TestSuite.True(d.Record.Operation is null);
        });
        await Case(suite, "document post-commit loss recovers by hash and preserves a newer local snapshot", async (d, t) =>
        {
            t.FailAfterCommit = true; await File.WriteAllTextAsync(d.WorkingPath, "sent\n"); await d.CheckAsync(true); TestSuite.Equal(RemoteEditState.Unknown, d.Record.State);
            await File.WriteAllTextAsync(d.WorkingPath, "new-local\n"); await d.CheckAsync();
            var restored = RemoteEditSession.Restore(d.DirectoryPath);
            try { t.FailAfterCommit = false; await restored.ResumeAsync(t); TestSuite.Equal("new-local\n", Encoding.UTF8.GetString(t.Bytes)); TestSuite.Equal(RemoteEditState.Confirmed, restored.Record.State); TestSuite.Equal(2, t.Writes); }
            finally { await restored.DisposeAsync(); }
        });
        await Case(suite, "document detach leaves files and restored journals paused without startup upload", async (d, t) =>
        {
            await d.DetachAsync(); await File.WriteAllTextAsync(d.WorkingPath, "offline\n"); await d.CheckAsync(true); TestSuite.Equal(0, t.Writes);
            var restored = RemoteEditSession.Restore(d.DirectoryPath); try { await restored.CheckAsync(true); TestSuite.Equal(0, t.Writes); TestSuite.Equal(RemoteEditState.Paused, restored.Record.State); } finally { await restored.DisposeAsync(); }
        });
        await Case(suite, "document cannot rebind to a changed host key or profile target", async (d, t) =>
        {
            var wrong = new Fake { Identity = t.Identity with { HostKey = "different" } };
            try { await d.ResumeAsync(wrong); throw new Exception("Changed host key rebound"); } catch (IOException) { }
            TestSuite.Equal(0, wrong.Writes);
        });
        await Case(suite, "document accepts a ten-MiB long line and uploads its exact bytes", async (d, t) =>
        {
            var content = Enumerable.Repeat((byte)'a', 10 * 1024 * 1024).ToArray(); await File.WriteAllBytesAsync(d.WorkingPath, content); await d.CheckAsync(true);
            TestSuite.True(content.SequenceEqual(t.Bytes)); TestSuite.Equal(RemoteEditState.Confirmed, d.Record.State);
        });
        await Case(suite, "document watcher detects editor atomic rename without a manual signal and avoids duplicate upload", async (d, t) =>
        {
            var temporary = d.WorkingPath + ".editor-tmp"; await File.WriteAllTextAsync(temporary, "atomic-save\n"); File.Move(temporary, d.WorkingPath, true);
            for (int i = 0; i < 100 && t.Writes == 0; i++) await Task.Delay(40);
            await d.CheckAsync(true); TestSuite.Equal(1, t.Writes); TestSuite.Equal("atomic-save\n", Encoding.UTF8.GetString(t.Bytes));
        });
        await Case(suite, "document cancellation during upload pauses without losing snapshot and explicit retry succeeds", async (d, t) =>
        {
            t.Delay = 1500; await File.WriteAllTextAsync(d.WorkingPath, "cancel-retry\n"); var send = d.CheckAsync(true); await Task.Delay(300); var detach = d.DetachAsync(); await send; await detach;
            TestSuite.Equal(0, t.Writes); TestSuite.True(d.HasUnconfirmed && File.Exists(d.SnapshotPath)); t.Delay = 0; await d.ResumeAsync(t); TestSuite.Equal("cancel-retry\n", Encoding.UTF8.GetString(t.Bytes));
        });
        await suite.Run("document explicit legacy encoding detects UTF-8 conversion and retains the confirmed new format", async () =>
        {
            var root = Path.Combine(Environment.CurrentDirectory, ".tools", "sshx-document-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            var transport = new Fake { Bytes = Encoding.GetEncoding(1251).GetBytes("Привет\n") };
            await using var d = await RemoteEditSession.OpenAsync(root, transport, "/CON:*.conf", "fixture", false, "windows-1251");
            TestSuite.True(Path.GetFileName(d.WorkingPath).EndsWith(".conf") && !Path.GetFileName(d.WorkingPath).Contains(':') && !Path.GetFileName(d.WorkingPath).Contains('*')); await File.WriteAllTextAsync(d.WorkingPath, "Привет, UTF-8\n"); await d.CheckAsync(true); TestSuite.Equal(RemoteEditState.FormatConfirmation, d.Record.State);
            await d.ResumeAsync(transport, true); TestSuite.Equal("utf-8", d.Record.Format.Encoding); await File.WriteAllTextAsync(d.WorkingPath, "ascii\n"); await d.CheckAsync(true); TestSuite.Equal(RemoteEditState.Confirmed, d.Record.State); TestSuite.Equal("utf-8", d.Record.Format.Encoding);
        });
        await suite.Run("workspace deduplicates the same remote target while two server identities remain independent", async () =>
        {
            string root = Path.Combine(Environment.CurrentDirectory, ".tools", "sshx-document-tests-" + Guid.NewGuid().ToString("N"));
            await using var workspace = new RemoteEditWorkspace(root); var a = new Fake(); var b = new Fake { Identity = a.Identity with { Host = "other.invalid" } };
            var first = await workspace.OpenAsync(a, "/same.txt", "A", false); var again = await workspace.OpenAsync(a, "/same.txt", "A", false); var second = await workspace.OpenAsync(b, "/same.txt", "B", false);
            TestSuite.True(ReferenceEquals(first, again)); TestSuite.True(first.WorkingPath != second.WorkingPath); TestSuite.Equal(2, workspace.Documents.Count);
        });
    }
    static async Task Case(TestSuite suite, string name, Func<RemoteEditSession, Fake, Task> run) => await suite.Run(name, async () =>
    {
        var root = Path.Combine(Environment.CurrentDirectory, ".tools", "sshx-document-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); var transport = new Fake();
        await using var document = await RemoteEditSession.OpenAsync(root, transport, "/document.sh", "fixture", false); await run(document, transport);
    });
    internal sealed class Fake : IRemoteDocumentTransport
    {
        public RemoteDocumentIdentity Identity { get; set; } = new(Guid.NewGuid(), "fixture.invalid", 22, "user", "fixture-key");
        public Guid SessionId { get; } = Guid.NewGuid(); public CancellationToken Lifetime => CancellationToken.None;
        public byte[] Bytes = Encoding.UTF8.GetBytes("original\n"); public int Writes, MaxActive, Delay; int active;
        public bool FailBeforeCommit, FailAfterCommit;
        readonly DocumentMetadata metadata = new(9, DateTime.UnixEpoch, 493, 1000, 1000);
        public Task<DocumentLocation> LocateAsync(string path, bool followLink, CancellationToken ct) => Task.FromResult(new DocumentLocation(path, path));
        public Task<RemoteDocumentVersion> ReadAsync(DocumentLocation location, CancellationToken ct) => Task.FromResult(new RemoteDocumentVersion(Bytes.ToArray(), metadata with { Length = Bytes.Length }));
        public async Task<RemoteDocumentVersion> ReplaceAsync(DocumentLocation location, string expectedHash, DocumentMetadata original, byte[] snapshot, Guid operation, Func<Task> beforeCommit, IProgress<long>? progress, CancellationToken ct)
        {
            MaxActive = Math.Max(MaxActive, Interlocked.Increment(ref active));
            try
            {
                if (FailBeforeCommit) throw new IOException("fixture permission/ENOSPC");
                if (Delay != 0) await Task.Delay(Delay, ct);
                if (RemoteEditSession.Hash(Bytes) != expectedHash) throw new DocumentConflictException("fixture external writer");
                await beforeCommit(); ct.ThrowIfCancellationRequested(); Bytes = snapshot.ToArray(); Writes++;
                if (FailAfterCommit) throw new IOException("fixture connection lost after rename");
                return await ReadAsync(location, ct);
            }
            finally { Interlocked.Decrement(ref active); }
        }
        public Task SaveCopyAsync(string path, byte[] bytes, short mode, CancellationToken ct) => Task.CompletedTask;
    }
}
#endif
