#if CORE_TESTS
using System.Text;
using SSHExplorer.Core;

internal static class FileCreationIntegration
{
    internal static async Task RunAsync(TestSuite suite, string configuration)
    {
        var config = await LinuxIntegration.LoadConfiguration(configuration);
        var store = new AppStore(Path.Combine(Environment.CurrentDirectory, ".tools", "file-create-tests", Guid.NewGuid().ToString("N")));
        var profile = new ServerProfile { Name = "Изолированный файловый стенд", Host = config.Host, Port = config.Port, Username = config.Username, Authentication = config.Authentication, PrivateKeyPath = config.PrivateKeyPath };
        await using var first = new ServerSession(profile, store);
        await using var second = new ServerSession(profile, store);
        Task<TrustDecision> Trust(HostKeyPrompt prompt)
        {
            TestSuite.Equal(config.ExpectedHostFingerprint, prompt.Fingerprint);
            TestSuite.True(!prompt.Changed); return Task.FromResult(TrustDecision.Session);
        }
        await first.ConnectAsync(new SessionCredentials(config.Secret), Trust);
        await second.ConnectAsync(new SessionCredentials(config.Secret), Trust);
        var parent = await first.Files.ResolvePathAsync(config.RemoteParent);
        var directory = PosixPath.Combine(parent, "sshx-inline-" + Guid.NewGuid().ToString("N"));
        await first.Files.CreateDirectoryAsync(directory);
        try
        {
            await suite.Run("REAL SFTP unique file creation preserves occupied name and contents", async () =>
            {
                var occupied = PosixPath.Combine(directory, "Новый файл.txt");
                var write = await first.ExecuteAsync("printf 'sentinel' > " + PosixPath.Quote(occupied));
                TestSuite.Equal(0, write.ExitStatus);
                var created = await first.Files.CreateUniqueAsync(directory, false);
                TestSuite.Equal("Новый файл (2).txt", PosixPath.Name(created));
                TestSuite.Equal(0L, (await first.Files.StatAsync(created)).Length);
                TestSuite.Equal("sentinel", Encoding.UTF8.GetString(await first.Files.ReadBytesAsync(occupied, 100)));
            });
            await suite.Run("REAL SFTP concurrent exclusive file creates use independent clients without overwriting", async () =>
            {
                var paths = await Task.WhenAll(first.Files.CreateUniqueAsync(directory, false), second.Files.CreateUniqueAsync(directory, false));
                TestSuite.Equal(2, paths.Distinct(StringComparer.Ordinal).Count());
                foreach (var path in paths) TestSuite.True((await first.Files.StatAsync(path)).IsRegularFile);
            });
            await suite.Run("REAL SFTP concurrent MKDIR creates return distinct confirmed directories", async () =>
            {
                var paths = await Task.WhenAll(first.Files.CreateUniqueAsync(directory, true), second.Files.CreateUniqueAsync(directory, true));
                TestSuite.Equal(2, paths.Distinct(StringComparer.Ordinal).Count());
                foreach (var path in paths) TestSuite.True((await first.Files.StatAsync(path)).IsDirectory);
            });
            await suite.Run("REAL SFTP POSIX rename conflict retains original and allows corrected Unicode punctuation", async () =>
            {
                var source = await first.Files.CreateUniqueAsync(directory, false);
                await TestSuite.ThrowsAsync<IOException>(() => first.Files.RenameAsync(source, PosixPath.Combine(directory, "Новый файл.txt")));
                TestSuite.True((await first.Files.StatAsync(source)).IsRegularFile);
                var target = PosixPath.Combine(directory, ".имя ' кавычки : ? \\ .txt");
                await first.Files.RenameAsync(source, target);
                TestSuite.Equal(PosixPath.Name(target), (await first.Files.StatAsync(target)).Name);
                TestSuite.True(first.Files.CanUndo && first.Files.UndoDescription?.Contains(PosixPath.Name(target), StringComparison.Ordinal) == true);
                await first.Files.UndoAsync(); TestSuite.True((await first.Files.StatAsync(source)).IsRegularFile);
            });
            await suite.Run("REAL SFTP cancelled create starts no object", async () =>
            {
                var before = (await first.Files.ListAsync(directory)).Count;
                using var cancel = new CancellationTokenSource(); cancel.Cancel();
                await TestSuite.ThrowsAsync<OperationCanceledException>(() => first.Files.CreateUniqueAsync(directory, true, cancel.Token));
                TestSuite.Equal(before, (await first.Files.ListAsync(directory)).Count);
            });
        }
        finally
        {
            if (PosixPath.Parent(directory) != parent || !PosixPath.Name(directory).StartsWith("sshx-inline-", StringComparison.Ordinal)) throw new InvalidOperationException("Dedicated test cleanup target mismatch.");
            await first.Files.DeleteAsync(directory);
        }
    }
}
#endif
