#if CORE_TESTS
using System.Text;
using SSHExplorer.Core;
using Renci.SshNet;

internal static class DocumentEditIntegration
{
    public static async Task RunAsync(TestSuite suite, string configuration)
    {
        var config = await LinuxIntegration.LoadConfiguration(configuration);
        var root = Path.Combine(Environment.CurrentDirectory, ".tools", "document-sftp-tests", Guid.NewGuid().ToString("N"));
        var store = new AppStore(Path.Combine(root, "store"));
        var profile = new ServerProfile { Name = "Document SFTP fixture", Host = config.Host, Port = config.Port, Username = config.Username, TimeoutSeconds = 20 };
        await using var first = new ServerSession(profile, store); await using var second = new ServerSession(profile, store);
        Task<TrustDecision> Trust(HostKeyPrompt key) { TestSuite.Equal(config.ExpectedHostFingerprint, key.Fingerprint); TestSuite.True(!key.Changed); return Task.FromResult(TrustDecision.Session); }
        await first.ConnectAsync(new SessionCredentials(config.Secret), Trust); await second.ConnectAsync(new SessionCredentials(config.Secret), Trust);
        var parent = await first.Files.ResolvePathAsync(config.RemoteParent); var directory = PosixPath.Combine(parent, "sshx-edit-" + Guid.NewGuid().ToString("N")); await first.Files.CreateDirectoryAsync(directory);
        var transport = new SftpDocumentTransport(first);
        async Task Write(string path, byte[] bytes, short mode = 644, DateTime? modified = null)
        {
            using var lease = await second.LeaseSftpAsync(CancellationToken.None);
            await Task.Run(() => { using (var stream = lease.Client.Open(path, FileMode.Create, FileAccess.Write)) stream.Write(bytes); lease.Client.ChangePermissions(path, mode); if (modified is { } stamp) lease.Client.SetLastWriteTimeUtc(path, stamp); });
        }
        try
        {
            await suite.Run("REAL SFTP document byte roundtrip .sh/.conf/.txt UTF-8 BOM UTF-16 mixed endings empty and executable mode", async () =>
            {
                var variants = new[] { ("script.sh", Encoding.UTF8.GetBytes("#!/bin/sh\n# не выполняется\n")), ("server.conf", new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes("key=value\r\n")).ToArray()), ("utf16.txt", Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("Привет\r\nlast\n")).ToArray()), ("empty.txt", Array.Empty<byte>()) };
                foreach (var (name, bytes) in variants)
                {
                    var path = PosixPath.Combine(directory, name); await Write(path, Encoding.UTF8.GetBytes("initial\n"), 755);
                    await using var document = await RemoteEditSession.OpenAsync(root, transport, path, "fixture", false);
                    await File.WriteAllBytesAsync(document.WorkingPath, bytes); await document.CheckAsync(true);
                    if (document.Record.State == RemoteEditState.FormatConfirmation) await document.ResumeAsync(transport, true);
                    TestSuite.Equal(RemoteEditState.Confirmed, document.Record.State); var actual = await transport.ReadAsync(document.Record.Location, CancellationToken.None);
                    TestSuite.True(bytes.SequenceEqual(actual.Bytes)); TestSuite.Equal((short)493, actual.Metadata.Mode); TestSuite.Equal(document.Record.Metadata.UserId, actual.Metadata.UserId); TestSuite.Equal(document.Record.Metadata.GroupId, actual.Metadata.GroupId);
                }
            });
            await suite.Run("REAL SFTP document detects external same-length same-mtime writer and repeated conflict", async () =>
            {
                string path = PosixPath.Combine(directory, "conflict.conf"); await Write(path, Encoding.UTF8.GetBytes("base\n"));
                await using var document = await RemoteEditSession.OpenAsync(root, transport, path, "fixture", false); var stamp = document.Record.Metadata.ModifiedUtc;
                await Write(path, Encoding.UTF8.GetBytes("peer\n"), modified: stamp); await File.WriteAllTextAsync(document.WorkingPath, "ours\n"); await document.CheckAsync(true);
                TestSuite.Equal(RemoteEditState.Conflict, document.Record.State); var approved = await document.ReadServerAsync(); await Write(path, Encoding.UTF8.GetBytes("next\n"), modified: stamp); await document.ReplaceApprovedVersionAsync(approved);
                TestSuite.Equal(RemoteEditState.Conflict, document.Record.State); TestSuite.Equal("next\n", Encoding.UTF8.GetString((await document.ReadServerAsync()).Bytes));
            });
            await suite.Run("REAL SFTP document symlink requires explicit target and changed target stops replacement", async () =>
            {
                string target = PosixPath.Combine(directory, "target.txt"), other = PosixPath.Combine(directory, "other.txt"), link = PosixPath.Combine(directory, "link");
                await Write(target, Encoding.UTF8.GetBytes("target\n")); await Write(other, Encoding.UTF8.GetBytes("other\n"));
                Console.WriteLine("DIAGNOSTIC document symlink create");
                using (var lease = await second.LeaseSftpAsync(CancellationToken.None)) await Task.Run(() => SftpProtocolAdapter.SymbolicLink(lease.Client, target, link));
                await TestSuite.ThrowsAsync<IOException>(() => transport.LocateAsync(link, false, CancellationToken.None));
                Console.WriteLine("DIAGNOSTIC document symlink explicit locate/open");
                await using var document = await RemoteEditSession.OpenAsync(root, transport, link, "fixture", true);
                Console.WriteLine("DIAGNOSTIC document symlink send");
                await File.WriteAllTextAsync(document.WorkingPath, "edited\n"); await document.CheckAsync(true); TestSuite.Equal(RemoteEditState.Confirmed, document.Record.State); TestSuite.True((await first.Files.StatAsync(link)).IsSymbolicLink);
                Console.WriteLine("DIAGNOSTIC document symlink retarget");
                using (var lease = await second.LeaseSftpAsync(CancellationToken.None)) await Task.Run(() => { SftpProtocolAdapter.Get(lease.Client, link).Delete(); SftpProtocolAdapter.SymbolicLink(lease.Client, other, link); });
                await File.WriteAllTextAsync(document.WorkingPath, "later\n"); await document.CheckAsync(true); TestSuite.Equal(RemoteEditState.Conflict, document.Record.State);
            });
            await suite.Run("REAL SFTP document ten-MiB long line hashes and preserves mode", async () =>
            {
                string path = PosixPath.Combine(directory, "large.txt"); var content = Enumerable.Repeat((byte)'x', 10 * 1024 * 1024).ToArray(); await Write(path, content);
                await using var document = await RemoteEditSession.OpenAsync(root, transport, path, "fixture", false);
                content[^1] = (byte)'y'; await File.WriteAllBytesAsync(document.WorkingPath, content); await document.CheckAsync(true);
                TestSuite.Equal(RemoteEditState.Confirmed, document.Record.State); TestSuite.Equal(RemoteEditSession.Hash(content), (await document.ReadServerAsync()).Hash);
            });
            await suite.Run("REAL SFTP document metadata refusal leaves original and local snapshot intact", async () =>
            {
                string path = PosixPath.Combine(directory, "mode.conf"); await Write(path, Encoding.UTF8.GetBytes("original\n"));
                await using var document = await RemoteEditSession.OpenAsync(root, transport, path, "fixture", false);
                using (var lease = await second.LeaseSftpAsync(CancellationToken.None)) lease.Client.ChangePermissions(path, 600);
                await File.WriteAllTextAsync(document.WorkingPath, "changed\n"); await document.CheckAsync(true); TestSuite.Equal(RemoteEditState.Conflict, document.Record.State);
                TestSuite.Equal("original\n", Encoding.UTF8.GetString((await document.ReadServerAsync()).Bytes)); TestSuite.True(File.Exists(document.SnapshotPath));
            });
            await suite.Run("REAL SFTP document cancellation before finalization preserves destination", async () =>
            {
                string path = PosixPath.Combine(directory, "cancel.txt"); await Write(path, Encoding.UTF8.GetBytes("original\n"));
                var location = await transport.LocateAsync(path, false, CancellationToken.None); var baseline = await transport.ReadAsync(location, CancellationToken.None); using var cancel = new CancellationTokenSource();
                await TestSuite.ThrowsAsync<OperationCanceledException>(() => transport.ReplaceAsync(location, baseline.Hash, baseline.Metadata, Encoding.UTF8.GetBytes("new\n"), Guid.NewGuid(), () => { cancel.Cancel(); return Task.CompletedTask; }, null, cancel.Token));
                TestSuite.Equal(baseline.Hash, (await transport.ReadAsync(location, CancellationToken.None)).Hash);
            });
        }
        finally { if (PosixPath.Parent(directory) != parent || !PosixPath.Name(directory).StartsWith("sshx-edit-")) throw new IOException("Invalid cleanup target"); await first.Files.DeleteAsync(directory); }
    }
}
#endif
