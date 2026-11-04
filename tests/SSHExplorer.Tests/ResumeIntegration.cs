#if CORE_TESTS
using System.Security.Cryptography;
using SSHExplorer.Core;

/// <summary>Real SFTP resume: interrupt by cancellation mid-file, retry, compare SHA-256. Arguments: host port user (agent auth).</summary>
internal static class ResumeIntegration
{
    const int Size = 96 * 1024 * 1024;

    public static async Task RunAsync(TestSuite t, string host, int port, string user)
    {
        var directory = Path.Combine(Path.GetTempPath(), "sshx-resume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var store = new AppStore(Path.Combine(directory, "store"));
        await using var session = new ServerSession(new ServerProfile { Name = "Докачка · стенд", Host = host, Port = port, Username = user, Authentication = AuthenticationKind.Agent }, store);
        await session.ConnectAsync(new SessionCredentials(""), _ => Task.FromResult(TrustDecision.Session));
        var remote = "/tmp/sshx-resume-" + Guid.NewGuid().ToString("N")[..8];
        await session.ExecuteAsync("mkdir -p " + PosixPath.Quote(remote));
        var queue = new TransferQueue();
        var local = Path.Combine(directory, "payload.bin");
        await File.WriteAllBytesAsync(local, RandomNumberGenerator.GetBytes(Size));

        async Task<TransferJob> Interrupted(Func<TransferJob> start)
        {
            var job = start();
            void OnProgress(object? s, System.ComponentModel.PropertyChangedEventArgs e) { if (job.BytesTransferred > 2 * 1024 * 1024 && job.BytesTransferred < Size) job.Cancel(); }
            job.PropertyChanged += OnProgress;
            await queue.WaitAsync(job); job.PropertyChanged -= OnProgress;
            return job;
        }
        async Task<string> RemoteParts() => (await session.ExecuteAsync($"ls -A {PosixPath.Quote(remote)} | grep -c '^.sshexplorer-r-' || true")).Output.Trim();
        async Task<string> RemoteHash(string path) => (await session.ExecuteAsync("sha256sum " + PosixPath.Quote(path))).Output.Split(' ')[0];
        static string LocalHash(string path) { using var s = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant(); }

        await t.Run("resume integration: an interrupted 96 MiB upload continues from its verified part", async () =>
        {
            var job = await Interrupted(() => queue.Upload(session, [local], remote));
            TestSuite.True(job.State == TransferState.Cancelled && job.BytesTransferred < Size, $"interrupted at {job.BytesTransferred}");
            TestSuite.Equal("1", await RemoteParts());
            await job.RetryAsync(); await queue.WaitAsync(job);
            TestSuite.True(job.State == TransferState.Completed && job.ResumedBytes > 0 && job.Summary.Contains("продолжено", StringComparison.Ordinal), $"{job.State} resumed {job.ResumedBytes}: {job.Error}");
            TestSuite.Equal(LocalHash(local), await RemoteHash(remote + "/payload.bin"));
            TestSuite.Equal("0", await RemoteParts());
        });
        var downloads = Path.Combine(directory, "downloads"); Directory.CreateDirectory(downloads);
        await t.Run("resume integration: an interrupted download continues from its verified local part", async () =>
        {
            var job = await Interrupted(() => queue.Download(session, [remote + "/payload.bin"], downloads));
            TestSuite.True(job.State == TransferState.Cancelled && Directory.GetFiles(downloads, ".sshexplorer-r-*.part").Length == 1, "local part kept");
            await job.RetryAsync(); await queue.WaitAsync(job);
            TestSuite.True(job.State == TransferState.Completed && job.ResumedBytes > 0, $"{job.State} resumed {job.ResumedBytes}: {job.Error}");
            TestSuite.Equal(LocalHash(local), LocalHash(Path.Combine(downloads, "payload.bin")));
            TestSuite.Equal(0, Directory.GetFiles(downloads, ".sshexplorer-r-*.part").Length);
        });
        await t.Run("resume integration: a changed source restarts from zero and still produces the new content", async () =>
        {
            await session.ExecuteAsync("rm -f " + PosixPath.Quote(remote + "/payload.bin"));
            var job = await Interrupted(() => queue.Upload(session, [local], remote));
            using (var f = new FileStream(local, FileMode.Open, FileAccess.Write)) { f.Seek(0, SeekOrigin.Begin); f.Write(RandomNumberGenerator.GetBytes(4096)); }
            File.SetLastWriteTimeUtc(local, DateTime.UtcNow.AddMinutes(1));
            await job.RetryAsync(); await queue.WaitAsync(job);
            TestSuite.True(job.State == TransferState.Completed && job.ResumedBytes == 0, $"{job.State} resumed {job.ResumedBytes}: {job.Error}");
            TestSuite.Equal(LocalHash(local), await RemoteHash(remote + "/payload.bin"));
            TestSuite.Equal("0", await RemoteParts());
        });
        await t.Run("resume integration: removing a cancelled job from the queue deletes its kept part", async () =>
        {
            await session.ExecuteAsync("rm -f " + PosixPath.Quote(remote + "/payload.bin"));
            var job = await Interrupted(() => queue.Upload(session, [local], remote));
            TestSuite.Equal("1", await RemoteParts());
            queue.RemoveFinished();
            for (int i = 0; i < 50 && await RemoteParts() != "0"; i++) await Task.Delay(100);
            TestSuite.Equal("0", await RemoteParts());
        });
        await session.ExecuteAsync("rm -rf " + PosixPath.Quote(remote));
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }
}
#endif
