#if CORE_TESTS
using SSHExplorer.Core;

internal static class TransferResumeTests
{
    public static async Task RunAsync(TestSuite t)
    {
        await t.Run("resume: part names are stable per transfer pair, distinct per direction/source/destination, and hidden", () =>
        {
            var a = TransferResume.PartName("upload", @"C:\data\a.bin", "/srv/a.bin");
            TestSuite.True(a == TransferResume.PartName("upload", @"C:\data\a.bin", "/srv/a.bin") && a.StartsWith(".sshexplorer-r-", StringComparison.Ordinal) && a.EndsWith(".part", StringComparison.Ordinal), a);
            TestSuite.True(new[] { TransferResume.PartName("download", @"C:\data\a.bin", "/srv/a.bin"), TransferResume.PartName("upload", @"C:\data\b.bin", "/srv/a.bin"), TransferResume.PartName("upload", @"C:\data\a.bin", "/srv/b.bin") }.All(x => x != a), "distinct pairs get distinct parts");
        });
        await t.Run("resume: interruptions keep parts; changed source, denied writes and full disk do not", () =>
        {
            TestSuite.True(TransferResume.ShouldRetain(new OperationCanceledException()) && TransferResume.ShouldRetain(new Renci.SshNet.Common.SshConnectionException("lost")) && TransferResume.ShouldRetain(new TimeoutException()), "interruptions");
            TestSuite.True(!TransferResume.ShouldRetain(new IOException("Источник изменён во время загрузки")) && !TransferResume.ShouldRetain(new Renci.SshNet.Common.SftpPermissionDeniedException("denied")) && !TransferResume.ShouldRetain(new IOException("Недостаточно свободного места.")), "non-resumable");
        });
        await t.Run("limits: the parallel gate queues, starts waiters when raised and drops cancelled waiters", async () =>
        {
            var gate = new ParallelGate(1);
            await gate.WaitAsync(CancellationToken.None);
            var second = gate.WaitAsync(CancellationToken.None); using var cancel = new CancellationTokenSource(); var third = gate.WaitAsync(cancel.Token);
            await Task.Delay(50); TestSuite.True(!second.IsCompleted && !third.IsCompleted, "capacity 1 holds the others");
            cancel.Cancel(); await TestSuite.ThrowsAsync<TaskCanceledException>(() => third);
            gate.Capacity = 2; await second.WaitAsync(TimeSpan.FromSeconds(2));
            TestSuite.Equal(2, gate.Running); gate.Release(); gate.Release(); TestSuite.Equal(0, gate.Running);
            gate.Capacity = 50; TestSuite.Equal(8, gate.Capacity);
        });
        await t.Run("limits: 2 MiB through a 1 MiB/s limiter takes about 1.75 s; unlimited is immediate", async () =>
        {
            var limiter = new RateLimiter { BytesPerSecond = 1024 * 1024 };
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (int sent = 0; sent < 2 * 1024 * 1024; sent += 128 * 1024) await limiter.WaitAsync(128 * 1024, CancellationToken.None);
            var limited = watch.Elapsed.TotalSeconds;
            TestSuite.True(limited is > 1.4 and < 3.0, $"limited transfer took {limited:0.00} s");
            limiter.BytesPerSecond = 0; watch.Restart();
            for (int i = 0; i < 100; i++) await limiter.WaitAsync(1024 * 1024, CancellationToken.None);
            TestSuite.True(watch.ElapsedMilliseconds < 200, $"unlimited took {watch.ElapsedMilliseconds} ms");
        });
        await Task.CompletedTask;
    }
}
#endif
