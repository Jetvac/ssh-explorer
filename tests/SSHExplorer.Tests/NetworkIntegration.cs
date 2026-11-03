#if CORE_TESTS
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SSHExplorer.Core;

internal sealed class NetworkTestConfiguration
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 22222;
    public string Username { get; set; } = "sshx-test";
    public AuthenticationKind Authentication { get; set; }
    public string PrivateKeyPath { get; set; } = "";
    public string Secret { get; set; } = "";
    public string? SecretFile { get; set; }
    public string ExpectedHostFingerprint { get; set; } = "";
    public int ProxyPort { get; set; } = 22226;
    public int DelayMilliseconds { get; set; } = 50;
}

internal static class NetworkIntegration
{
    public static async Task RunAsync(TestSuite t, string file)
    {
        var config = JsonSerializer.Deserialize<NetworkTestConfiguration>(await File.ReadAllTextAsync(file),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } })
            ?? throw new InvalidDataException("Empty isolated proxy fixture configuration");
        if (config.Host is not ("127.0.0.1" or "localhost" or "::1"))
            throw new InvalidDataException("Network fault fixture only permits an explicitly isolated loopback server");
        if (config.ExpectedHostFingerprint.Length == 0) throw new InvalidDataException("Independent host fingerprint is required");
        if (config.SecretFile is not null) config.Secret = (await File.ReadAllTextAsync(config.SecretFile)).TrimEnd('\r', '\n');
        var run = Guid.NewGuid().ToString("N");
        var local = Path.Combine(Environment.CurrentDirectory, ".tools", "network-tests", run);
        Directory.CreateDirectory(local);
        var directStore = new AppStore(Path.Combine(local, "direct-store"));
        var proxyStore = new AppStore(Path.Combine(local, "proxy-store"));
        ServerProfile Profile(int port) => new() { Name = "Изолированный сетевой тест", Host = port == config.ProxyPort ? "127.0.0.1" : config.Host,
            Port = port, Username = config.Username, Authentication = config.Authentication,
            PrivateKeyPath = config.PrivateKeyPath, TimeoutSeconds = 20 };
        Task<TrustDecision> Trust(HostKeyPrompt prompt)
        {
            TestSuite.Equal(config.ExpectedHostFingerprint, prompt.Fingerprint);
            return Task.FromResult(TrustDecision.Permanent);
        }
        await using var direct = new ServerSession(Profile(config.Port), directStore);
        await direct.ConnectAsync(new SessionCredentials(config.Secret), Trust);
        var remote = PosixPath.Combine(direct.Files.HomeDirectory, "sshx-network-" + run);
        await direct.Files.CreateDirectoryAsync(remote);
        var source = Path.Combine(local, "preserve-original.bin");
        var original = RandomNumberGenerator.GetBytes(256 * 1024);
        await File.WriteAllBytesAsync(source, original);
        var remoteFile = PosixPath.Combine(remote, Path.GetFileName(source));
        var queue = new TransferQueue { ResolveConflict = _ => Task.FromResult(ConflictDecision.Replace) };
        var seed = queue.Upload(direct, [source], remote); await queue.WaitAsync(seed);
        TestSuite.Equal(TransferState.Completed, seed.State);
        var replacement = RandomNumberGenerator.GetBytes(4 * 1024 * 1024);
        await File.WriteAllBytesAsync(source, replacement);
        await using var proxy = new TcpFaultProxy(config.ProxyPort, config.Host, config.Port,
            TimeSpan.FromMilliseconds(config.DelayMilliseconds));
        proxy.Start();
        Console.WriteLine($"FAULT FIXTURE: loopback {config.ProxyPort} -> {config.Port}, {config.DelayMilliseconds}ms delay per forwarded chunk; primary server untouched");
        ServerSession? broken = null, fresh = null;
        try
        {
            broken = new ServerSession(Profile(config.ProxyPort), proxyStore);
            await broken.ConnectAsync(new SessionCredentials(config.Secret), Trust);
            var failed = queue.Upload(broken, [source], remote);
            var cut = 0;
            failed.PropertyChanged += (_, _) =>
            {
                if (failed.BytesTransferred >= 512 * 1024 && Interlocked.Exchange(ref cut, 1) == 0)
                    proxy.CutAll();
            };
            await t.Run("REAL NETWORK: mid-upload transport cut preserves correct destination and local source", async () =>
            {
                await queue.WaitAsync(failed).WaitAsync(TimeSpan.FromSeconds(20));
                TestSuite.Equal(1, cut);
                TestSuite.True(failed.State is TransferState.Failed or TransferState.Cancelled, "Cut transfer was incorrectly reported completed");
                TestSuite.True(failed.BytesTransferred > 0 && failed.BytesTransferred < replacement.Length);
                TestSuite.Equal(Hash(original), Hash(await direct.Files.ReadBytesAsync(remoteFile, original.Length + 1)));
                TestSuite.Equal(Hash(replacement), Hash(await File.ReadAllBytesAsync(source)));
                Console.WriteLine($"NETWORK CUT: after {failed.BytesTransferred} bytes of {replacement.Length}; state {failed.State}; original SHA256 unchanged");
            });
            await t.Run("REAL NETWORK: restoring proxy does not replay failed upload or overwrite", async () =>
            {
                var bytes = failed.BytesTransferred; var accepted = proxy.AcceptedConnections;
                proxy.Restore(); await Task.Delay(600);
                TestSuite.Equal(accepted, proxy.AcceptedConnections);
                TestSuite.Equal(bytes, failed.BytesTransferred);
                TestSuite.True(failed.State is TransferState.Failed or TransferState.Cancelled);
                TestSuite.True(!queue.HasActive(broken.Id));
                TestSuite.Equal(Hash(original), Hash(await direct.Files.ReadBytesAsync(remoteFile, original.Length + 1)));
                await broken.DisposeAsync();
            });
            await t.Run("REAL NETWORK: explicit retry in fresh trusted session asks conflict and completes", async () =>
            {
                fresh = new ServerSession(Profile(config.ProxyPort), proxyStore);
                await fresh.ConnectAsync(new SessionCredentials(config.Secret), _ => throw new Exception("Stored trusted host key unexpectedly requested first trust"));
                var conflicts = 0;
                queue.ResolveConflict = _ => { Interlocked.Increment(ref conflicts); return Task.FromResult(ConflictDecision.Replace); };
                var retry = queue.Upload(fresh, [source], remote);
                await queue.WaitAsync(retry).WaitAsync(TimeSpan.FromSeconds(30));
                if (retry.State != TransferState.Completed) throw new Exception(retry.Error ?? "Explicit retry failed");
                TestSuite.True(conflicts >= 1);
                TestSuite.Equal(Hash(replacement), Hash(await direct.Files.ReadBytesAsync(remoteFile, replacement.Length + 1)));
                TestSuite.True(failed.State is TransferState.Failed or TransferState.Cancelled);
                Console.WriteLine($"EXPLICIT RETRY: {retry.BytesTransferred} bytes, {conflicts} confirmed conflict; old job remained {failed.State}");
            });
            if (fresh is not null)
            {
                await t.Run("REAL NETWORK: delayed exec respects cancellation without replay", async () =>
                {
                    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
                    var elapsed = Stopwatch.StartNew();
                    await TestSuite.ThrowsAsync<OperationCanceledException>(() => fresh.ExecuteAsync("sleep 5; printf should-not-complete", cancellation.Token).WaitAsync(TimeSpan.FromSeconds(2)));
                    elapsed.Stop(); TestSuite.True(elapsed.Elapsed < TimeSpan.FromSeconds(2));
                    Console.WriteLine($"CANCEL: delayed exec stopped locally in {elapsed.Elapsed.TotalMilliseconds:0}ms");
                });
                await t.Run("REAL NETWORK: close cancels outstanding request and releases all owned transports", async () =>
                {
                    var pending = fresh.ExecuteAsync("sleep 5; printf late");
                    await Task.Delay(50);
                    TestSuite.True(!pending.IsCompleted, "Close test requires a genuinely outstanding command");
                    var elapsed = Stopwatch.StartNew();
                    await fresh.DisposeAsync();
                    var rejected = false;
                    try { await pending.WaitAsync(TimeSpan.FromSeconds(2)); }
                    catch (Exception e) when (e is not TimeoutException) { rejected = true; }
                    TestSuite.True(rejected, "An outstanding command succeeded after session close");
                    TestSuite.True(fresh.Lifetime.IsCancellationRequested);
                    TestSuite.Equal(SessionState.Closed, fresh.State);
                    while (proxy.ActiveConnections != 0 && elapsed.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(20);
                    TestSuite.Equal(0, proxy.ActiveConnections);
                    TestSuite.True(elapsed.Elapsed < TimeSpan.FromSeconds(2));
                    Console.WriteLine($"CLOSE: pending request cancelled, {proxy.ActiveConnections} proxy connections, {elapsed.Elapsed.TotalMilliseconds:0}ms");
                });
            }
        }
        finally
        {
            if (broken is not null) await broken.DisposeAsync();
            if (fresh is not null) await fresh.DisposeAsync();
            await t.Run("REAL NETWORK: dedicated test-tree cleanup; independent direct server remains usable", async () =>
            {
                await direct.Files.DeleteAsync(remote);
                TestSuite.Equal(0, (await direct.ExecuteAsync("printf independent-server-alive")).ExitStatus);
            });
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
#endif
