#if CORE_TESTS
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;
using System.Text.RegularExpressions;
using SSHExplorer.Core;
using SSHExplorer.Monitoring;

internal sealed class LinuxTestConfiguration
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 22222;
    public string Username { get; set; } = "sshx-test";
    public AuthenticationKind Authentication { get; set; }
    public string PrivateKeyPath { get; set; } = "";
    public string Secret { get; set; } = "";
    public string? SecretFile { get; set; }
    public string ExpectedHostFingerprint { get; set; } = "";
    public string RemoteParent { get; set; } = "~";
    public long LargeFileBytes { get; set; }
    public bool RunMonitoring { get; set; } = true;
    public bool RunDirectoryLoad { get; set; }
}

internal static class LinuxIntegration
{
    public static async Task RunAsync(TestSuite t, string configurationFile)
    {
        var config = await LoadConfiguration(configurationFile);
        var run = Guid.NewGuid().ToString("N");
        var local = Path.Combine(Environment.CurrentDirectory, ".tools", "integration-data", run);
        Directory.CreateDirectory(local);
        var store = new AppStore(Path.Combine(local, "store"));
        var profile = new ServerProfile { Name = "Изолированный Linux-стенд", Host = config.Host, Port = config.Port,
            Username = config.Username, Authentication = config.Authentication, PrivateKeyPath = config.PrivateKeyPath, TimeoutSeconds = 20 };
        await using var session = new ServerSession(profile, store);
        bool connected = false;
        var trustPrompts = 0;
        await t.Run("REAL SSH: independent host fingerprint and fresh account authentication", async () =>
        {
            await session.ConnectAsync(new SessionCredentials(config.Secret), prompt =>
            {
                trustPrompts++;
                TestSuite.Equal(config.ExpectedHostFingerprint, prompt.Fingerprint);
                TestSuite.True(!prompt.Changed);
                return Task.FromResult(TrustDecision.Permanent);
            });
            TestSuite.Equal(SessionState.Connected, session.State);
            TestSuite.True(session.SftpAvailable, session.SftpError ?? "SFTP unavailable");
            TestSuite.True(session.ShellAvailable, session.ShellError ?? "exec unavailable");
            TestSuite.Equal(1, trustPrompts);
            connected = true;
        });
        if (!connected) return;
        var parent = await session.Files.ResolvePathAsync(config.RemoteParent);
        var remote = PosixPath.Combine(parent, "sshx-integration-" + run);
        await session.Files.CreateDirectoryAsync(remote);
        var queue = new TransferQueue { ResolveConflict = _ => Task.FromResult(ConflictDecision.Replace) };
        var payload = Path.Combine(local, "payload.txt");
        var data = RandomNumberGenerator.GetBytes(1024 * 1024 + 17);
        await File.WriteAllBytesAsync(payload, data);
        string remotePayload = PosixPath.Combine(remote, Path.GetFileName(payload));
        try
        {
            await t.Run("REAL SFTP: CRUD and hostile POSIX names do not execute shell syntax", async () =>
            {
                foreach (var name in new[] { "кириллица", "a b", "single'quote", "-leading", "semi;colon", "$dollar", "line\nbreak", "back\\slash" })
                {
                    var path = PosixPath.Combine(remote, name);
                    await session.Files.CreateFileAsync(path);
                    var entry = await session.Files.StatAsync(path);
                    TestSuite.Equal(name, entry.Name); TestSuite.True(entry.IsRegularFile);
                }
                var names = (await session.Files.ListAsync(remote)).Select(x => x.Name).ToArray();
                TestSuite.True(names.Contains("semi;colon", StringComparer.Ordinal));
                var folder = PosixPath.Combine(remote, "папка");
                await session.Files.CreateDirectoryAsync(folder);
                TestSuite.Equal("—", (await session.Files.StatAsync(folder)).SizeText);
                await session.Files.DeleteAsync(folder);
                await TestSuite.ThrowsAsync<Exception>(async () => await session.Files.StatAsync(folder));
            });
            await t.Run("REAL SFTP: streaming upload/download roundtrip and measured byte progress", async () =>
            {
                var upload = queue.Upload(session, [payload], remote); await Wait(queue, upload);
                TestSuite.Equal((long)data.Length, upload.BytesTransferred);
                TestSuite.Equal((long)data.Length, upload.TotalBytes);
                var output = Path.Combine(local, "download");
                var download = queue.Download(session, [remotePayload], output); await Wait(queue, download);
                TestSuite.Equal(Convert.ToHexString(SHA256.HashData(data)),
                    Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(output, "payload.txt")))));
                TestSuite.True(File.Exists(payload), "Upload removed local original");
                TestSuite.True((await session.Files.StatAsync(remotePayload)).IsRegularFile, "Download removed remote original");
            });
            await t.Run("REAL SFTP: rename safe undo; external change refuses undo", async () =>
            {
                var initial = PosixPath.Combine(remote, "undo.txt"); var renamed = PosixPath.Combine(remote, "renamed.txt");
                await session.Files.CreateFileAsync(initial); await session.Files.RenameAsync(initial, renamed);
                TestSuite.True(session.Files.CanUndo); await session.Files.UndoAsync();
                TestSuite.True((await session.Files.StatAsync(initial)).IsRegularFile);
                await session.Files.RenameAsync(initial, renamed);
                var write = await session.ExecuteAsync("printf 'external-change' > " + PosixPath.Quote(renamed));
                TestSuite.Equal(0, write.ExitStatus);
                await TestSuite.ThrowsAsync<IOException>(() => session.Files.UndoAsync());
                TestSuite.Equal(15L, (await session.Files.StatAsync(renamed)).Length);
            });
            await t.Run("REAL SFTP: same-size external modification with preserved mtime refuses undo", async () =>
            {
                var original = PosixPath.Combine(remote, "undo-content.txt"); var renamed = PosixPath.Combine(remote, "undo-content-renamed.txt");
                var write = await session.ExecuteAsync("printf 'AAAA' > " + PosixPath.Quote(original));
                TestSuite.Equal(0, write.ExitStatus);
                await session.Files.RenameAsync(original, renamed);
                var modified = await session.ExecuteAsync("reference=" + PosixPath.Quote(PosixPath.Combine(remote, "undo-reference")) +
                    "; touch -r " + PosixPath.Quote(renamed) + " \"$reference\" && printf 'BBBB' > " + PosixPath.Quote(renamed) +
                    " && touch -r \"$reference\" " + PosixPath.Quote(renamed));
                TestSuite.Equal(0, modified.ExitStatus);
                await TestSuite.ThrowsAsync<IOException>(() => session.Files.UndoAsync());
                TestSuite.Equal("BBBB", Encoding.UTF8.GetString(await session.Files.ReadBytesAsync(renamed, 10)));
            });
            await t.Run("REAL SFTP: same-server copy, move and descendant protection", async () =>
            {
                var copies = PosixPath.Combine(remote, "copies"); var moved = PosixPath.Combine(remote, "moved");
                await session.Files.CreateDirectoryAsync(copies); await session.Files.CreateDirectoryAsync(moved);
                await Wait(queue, queue.Copy(session, [remotePayload], session, copies));
                var copy = PosixPath.Combine(copies, "payload.txt");
                TestSuite.Equal(Convert.ToHexString(SHA256.HashData(data)),
                    Convert.ToHexString(SHA256.HashData(await session.Files.ReadBytesAsync(copy, data.Length + 1))));
                await Wait(queue, queue.Copy(session, [copy], session, moved, move: true));
                await TestSuite.ThrowsAsync<Exception>(async () => await session.Files.StatAsync(copy));
                TestSuite.Equal((long)data.Length, (await session.Files.StatAsync(PosixPath.Combine(moved, "payload.txt"))).Length);
                var invalid = queue.Copy(session, [copies], session, copies); await queue.WaitAsync(invalid);
                TestSuite.Equal(TransferState.Failed, invalid.State);
            });
            await t.Run("REAL SFTP: conflicts skip, keep both and replace preserve correct original", async () =>
            {
                queue.ResolveConflict = _ => Task.FromResult(ConflictDecision.Skip);
                var skip = queue.Upload(session, [payload], remote); await Wait(queue, skip);
                TestSuite.Equal(1, skip.SkippedItems); TestSuite.Equal(0L, skip.BytesTransferred);
                queue.ResolveConflict = _ => Task.FromResult(ConflictDecision.KeepBoth);
                await Wait(queue, queue.Upload(session, [payload], remote));
                TestSuite.True((await session.Files.ListAsync(remote)).Any(x => x.Name == "payload.txt (2)"));
                queue.ResolveConflict = _ => Task.FromResult(ConflictDecision.Replace);
                await Wait(queue, queue.Upload(session, [payload], remote));
                TestSuite.Equal(Convert.ToHexString(SHA256.HashData(data)),
                    Convert.ToHexString(SHA256.HashData(await session.Files.ReadBytesAsync(remotePayload, data.Length + 1))));
            });
            await t.Run("REAL SFTP: recursive upload merges directories without deleting unrelated files", async () =>
            {
                var tree = Path.Combine(local, "tree"); Directory.CreateDirectory(Path.Combine(tree, "nested"));
                await File.WriteAllTextAsync(Path.Combine(tree, "nested", "new.txt"), "new-content");
                var remoteTree = PosixPath.Combine(remote, "tree"); await session.Files.CreateDirectoryAsync(remoteTree);
                await session.Files.CreateFileAsync(PosixPath.Combine(remoteTree, "sentinel.txt"));
                await Wait(queue, queue.Upload(session, [tree], remote));
                TestSuite.True((await session.Files.StatAsync(PosixPath.Combine(remoteTree, "sentinel.txt"))).IsRegularFile);
                TestSuite.Equal("new-content", Encoding.UTF8.GetString(await session.Files.ReadBytesAsync(PosixPath.Combine(remoteTree, "nested/new.txt"), 100)));
                TestSuite.Equal(11L, await session.Files.CalculateSizeAsync(remoteTree));
            });
            await t.Run("REAL SFTP: cancelled replacement preserves remote and local original", async () =>
            {
                await using (var stream = new FileStream(payload, FileMode.Open, FileAccess.Write)) stream.SetLength(64L * 1024 * 1024);
                var cancelled = queue.Upload(session, [payload], remote);
                cancelled.PropertyChanged += (_, _) => { if (cancelled.BytesTransferred >= 128 * 1024) cancelled.Cancel(); };
                await queue.WaitAsync(cancelled);
                TestSuite.Equal(TransferState.Cancelled, cancelled.State);
                TestSuite.Equal(Convert.ToHexString(SHA256.HashData(data)),
                    Convert.ToHexString(SHA256.HashData(await session.Files.ReadBytesAsync(remotePayload, data.Length + 1))));
                TestSuite.True(File.Exists(payload));
                TestSuite.True(!(await session.Files.ListAsync(remote)).Any(x => x.Name.StartsWith(".sshexplorer-", StringComparison.Ordinal) && x.Name.EndsWith(".part", StringComparison.Ordinal)));
                await File.WriteAllBytesAsync(payload, data);
            });
            await t.Run("REAL SFTP: POSIX permissions and owner/group readback", async () =>
            {
                await session.Files.SetPermissionsAsync(remotePayload, 640);
                var e = await session.Files.StatAsync(remotePayload);
                TestSuite.Equal("0640", e.PermissionsText);
                await session.Files.SetOwnerAsync(remotePayload, e.UserId, e.GroupId);
                var changed = await session.Files.StatAsync(remotePayload);
                TestSuite.Equal(e.UserId, changed.UserId); TestSuite.Equal(e.GroupId, changed.GroupId);
            });
            await t.Run("REAL SFTP: symlink, broken link and cycle deletion never follows target", async () =>
            {
                var target = PosixPath.Combine(remote, "link-target"); var container = PosixPath.Combine(remote, "link-container");
                await session.Files.CreateDirectoryAsync(target); await session.Files.CreateFileAsync(PosixPath.Combine(target, "sentinel"));
                await session.Files.CreateDirectoryAsync(container);
                var link = PosixPath.Combine(container, "link"); var broken = PosixPath.Combine(container, "broken"); var cycle = PosixPath.Combine(container, "cycle");
                var r = await session.ExecuteAsync("ln -s " + PosixPath.Quote(target) + " " + PosixPath.Quote(link) +
                    " && ln -s " + PosixPath.Quote(PosixPath.Combine(remote, "does-not-exist")) + " " + PosixPath.Quote(broken) +
                    " && ln -s '.' " + PosixPath.Quote(cycle));
                TestSuite.Equal(0, r.ExitStatus);
                TestSuite.True((await session.Files.StatAsync(link)).IsSymbolicLink);
                TestSuite.True((await session.Files.StatAsync(broken)).IsSymbolicLink);
                await session.Files.DeleteAsync(link);
                TestSuite.True((await session.Files.StatAsync(PosixPath.Combine(target, "sentinel"))).IsRegularFile);
                await session.Files.DeleteAsync(container);
                TestSuite.True((await session.Files.StatAsync(PosixPath.Combine(target, "sentinel"))).IsRegularFile);
                await TestSuite.ThrowsAsync<IOException>(() => session.Files.DeleteAsync("/"));
            });
            await t.Run("REAL Windows download: reserved names and case collisions have mapping", async () =>
            {
                var naming = PosixPath.Combine(remote, "windows-names"); await session.Files.CreateDirectoryAsync(naming);
                foreach (var name in new[] { "CON", "A.txt", "a.txt", "trailing.", "colon:name" }) await session.Files.CreateFileAsync(PosixPath.Combine(naming, name));
                var download = Path.Combine(local, "names");
                queue.ResolveConflict = _ => Task.FromResult(ConflictDecision.KeepBoth);
                await Wait(queue, queue.Download(session, [naming], download));
                var directory = Path.Combine(download, "windows-names");
                TestSuite.True(File.Exists(Path.Combine(directory, "_CON")));
                TestSuite.True(File.Exists(Path.Combine(directory, ".sshexplorer-name-map.jsonl")));
                TestSuite.Equal(6, Directory.EnumerateFiles(directory).Count());
                queue.ResolveConflict = _ => Task.FromResult(ConflictDecision.Replace);
            });
            await t.Run("REAL SSH: second session independent; close stops owned connections", async () =>
            {
                var second = new ServerSession(profile, store);
                await second.ConnectAsync(new SessionCredentials(config.Secret), _ => throw new Exception("Trusted key was requested again"));
                TestSuite.True(second.Id != session.Id);
                await second.DisposeAsync();
                TestSuite.True(second.Lifetime.IsCancellationRequested);
                TestSuite.Equal(0, (await session.ExecuteAsync("printf alive")).ExitStatus);
                TestSuite.True(session.SftpAvailable);
            });
            if (config.RunMonitoring)
            {
                await t.Run("REAL Linux: /proc monitor without Python/htop and independent memory comparison", async () =>
                {
                    var monitor = new LinuxMonitor(async (cmd, ct) =>
                    { var r = await session.ExecuteAsync(cmd, ct); return new ExecResult(r.Output, r.Error, r.ExitStatus); });
                    var first = await monitor.SampleAsync(); await Task.Delay(1100); var second = await monitor.SampleAsync();
                    TestSuite.True(second.System.CpuPercent is >= 0 and <= 100);
                    TestSuite.True(second.System.MemoryTotalBytes is > 0);
                    TestSuite.True(second.Processes.Count > 0);
                    TestSuite.True(second.Processes.All(p => p.CpuPercent is null or (>= 0 and <= 100)));
                    var independent = await session.ExecuteAsync("cat /proc/meminfo");
                    var memory = ProcParser.ParseMemory(independent.Output);
                    TestSuite.Equal(memory.TotalBytes, second.System.MemoryTotalBytes);
                    TestSuite.True(memory.AvailableBytes is not null && second.System.MemoryAvailableBytes is not null);
                    var memoryDelta = Math.Abs(memory.AvailableBytes!.Value - second.System.MemoryAvailableBytes!.Value);
                    TestSuite.True(memoryDelta < second.System.MemoryTotalBytes!.Value / 3,
                        "Independent memory measurement differs by more than one third of total memory");
                    TestSuite.True(second.System.Uptime >= first.System.Uptime);
                });
                await t.Run("REAL Linux: CPU matches an independent simultaneous SSH counter interval", async () =>
                {
                    using var independentClient = await session.CreateSshClientAsync(default);
                    using var independent = independentClient.CreateCommand("head -n1 /proc/stat; sleep 3; head -n1 /proc/stat");
                    independent.CommandTimeout = TimeSpan.FromSeconds(10);
                    var monitor = new LinuxMonitor(async (cmd, ct) =>
                    { var r = await session.ExecuteAsync(cmd, ct); return new ExecResult(r.Output, r.Error, r.ExitStatus); });
                    var elapsed = Stopwatch.StartNew();
                    var reading = independent.ExecuteAsync(default);
                    await monitor.SampleAsync();
                    var remaining = TimeSpan.FromSeconds(3) - elapsed.Elapsed;
                    if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
                    var measured = await monitor.SampleAsync();
                    await reading;
                    TestSuite.Equal(0, independent.ExitStatus);
                    var rows = independent.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    TestSuite.Equal(2, rows.Length);
                    static (ulong Total, ulong Idle) Count(string row)
                    {
                        var fields = row.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                        TestSuite.Equal("cpu", fields[0]);
                        ulong total = 0; for (var i = 1; i <= 8; i++) total = checked(total + ulong.Parse(fields[i]));
                        return (total, ulong.Parse(fields[4]) + ulong.Parse(fields[5]));
                    }
                    var before = Count(rows[0]); var after = Count(rows[1]);
                    TestSuite.True(after.Total > before.Total && after.Idle >= before.Idle);
                    var independentPercent = 100d * ((after.Total - before.Total) - (after.Idle - before.Idle)) / (after.Total - before.Total);
                    TestSuite.True(measured.System.CpuPercent is not null);
                    Console.WriteLine($"CPU comparison: monitor {measured.System.CpuPercent:0.00}%, independent {independentPercent:0.00}%, ~3s intervals; scale 0–100% all visible CPUs");
                    TestSuite.True(Math.Abs(measured.System.CpuPercent!.Value - independentPercent) < 20,
                        "CPU differs by >=20 percentage points despite aligned 3-second windows");
                });
                await t.Run("REAL Linux: PID identity mismatch blocks SIGTERM", async () =>
                {
                    var monitor = new LinuxMonitor(async (cmd, ct) =>
                    { var r = await session.ExecuteAsync(cmd, ct); return new ExecResult(r.Output, r.Error, r.ExitStatus); });
                    var snapshot = await monitor.SampleAsync();
                    var owned = snapshot.Processes.First(p => p.Pid > 1 && p.CanSignal);
                    await TestSuite.ThrowsAsync<ProcessChangedException>(() => monitor.SignalAsync(owned.Identity with { StartTimeTicks = owned.Identity.StartTimeTicks + 1 }, 15));
                });
                await t.Run("REAL Linux: SIGTERM and separate SIGKILL terminate only dedicated PTY test processes", async () =>
                {
                    foreach (var signal in new[] { 15, 9 })
                    {
                        using var shell = await session.CreateShellAsync();
                        var bytes = Encoding.ASCII.GetBytes("stty -echo; printf '\\nSSHXTESTPID:%s\\n' \"$$\"; exec sleep 60\r");
                        shell.Write(bytes, 0, bytes.Length); shell.Flush();
                        var response = new StringBuilder(); var deadline = Stopwatch.StartNew(); int pid = 0;
                        while (deadline.Elapsed < TimeSpan.FromSeconds(8))
                        {
                            if (shell.DataAvailable) response.Append(shell.Read());
                            var match = Regex.Match(response.ToString(), "SSHXTESTPID:([0-9]+)", RegexOptions.CultureInvariant);
                            if (match.Success) { pid = int.Parse(match.Groups[1].Value); break; }
                            await Task.Delay(20);
                        }
                        TestSuite.True(pid > 1, "PTY did not return its dedicated test PID");
                        var monitor = new LinuxMonitor(async (cmd, ct) =>
                        { var r = await session.ExecuteAsync(cmd, ct); return new ExecResult(r.Output, r.Error, r.ExitStatus); });
                        var snapshot = await monitor.SampleAsync();
                        var target = snapshot.Processes.Single(p => p.Pid == pid);
                        TestSuite.Equal("sleep", target.Name);
                        await monitor.SignalAsync(target.Identity, signal);
                        var gone = false;
                        for (var attempt = 0; attempt < 20; attempt++)
                        {
                            var probe = await session.ExecuteAsync("test -d /proc/" + pid);
                            if (probe.ExitStatus != 0) { gone = true; break; }
                            await Task.Delay(50);
                        }
                        TestSuite.True(gone, "Dedicated test process was not reaped after signal");
                        TestSuite.True(session.SftpAvailable);
                    }
                });
            }
            if (config.LargeFileBytes > 0)
            {
                await t.Run("REAL SFTP: configured >2GiB streaming upload/download SHA256", async () =>
                {
                    TestSuite.True(config.LargeFileBytes > 2L * 1024 * 1024 * 1024, "LargeFileBytes must exceed 2 GiB");
                    var large = Path.Combine(local, "large.bin");
                    await using (var output = new FileStream(large, FileMode.CreateNew, FileAccess.Write)) output.SetLength(config.LargeFileBytes);
                    await Wait(queue, queue.Upload(session, [large], remote));
                    var destination = Path.Combine(local, "large-download");
                    await Wait(queue, queue.Download(session, [PosixPath.Combine(remote, "large.bin")], destination));
                    using var source = File.OpenRead(large); using var result = File.OpenRead(Path.Combine(destination, "large.bin"));
                    TestSuite.Equal(Convert.ToHexString(await SHA256.HashDataAsync(source)), Convert.ToHexString(await SHA256.HashDataAsync(result)));
                    TestSuite.Equal(config.LargeFileBytes, result.Length);
                });
            }
            if (config.RunDirectoryLoad)
            {
                await t.Run("REAL SFTP: directory with 10000 entries enumerates exact names and count", async () =>
                {
                    var many = PosixPath.Combine(remote, "ten-thousand");
                    await session.Files.CreateDirectoryAsync(many);
                    for (var batch = 0; batch < 10; batch++)
                    {
                        var result = await session.ExecuteAsync("d=" + PosixPath.Quote(many) + "; i=" + batch * 1000 +
                            "; end=" + (batch + 1) * 1000 + "; while [ \"$i\" -lt \"$end\" ]; do : > \"$d/file-$i.txt\" || exit 1; i=$((i+1)); done");
                        TestSuite.Equal(0, result.ExitStatus);
                    }
                    var stopwatch = Stopwatch.StartNew();
                    var entries = await session.Files.ListAsync(many);
                    stopwatch.Stop();
                    TestSuite.Equal(10000, entries.Count);
                    TestSuite.Equal(10000, entries.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count());
                    TestSuite.True(entries.Any(x => x.Name == "file-0.txt") && entries.Any(x => x.Name == "file-9999.txt"));
                    Console.WriteLine($"DIRECTORY 10000 entries enumerated in {stopwatch.Elapsed.TotalSeconds:0.00}s, RSS {Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024} MiB");
                });
            }
        }
        finally
        {
            await t.Run("REAL SFTP: cleanup only this run's dedicated test directory", () => session.Files.DeleteAsync(remote));
        }
    }

    public static async Task RunCapabilitiesOnlyAsync(TestSuite t, string configurationFile)
    {
        var config = await LoadConfiguration(configurationFile);
        var store = new AppStore(Path.Combine(Environment.CurrentDirectory, ".tools", "integration-capabilities", Guid.NewGuid().ToString("N")));
        var profile = new ServerProfile { Name = "Изолированный Linux-стенд", Host = config.Host, Port = config.Port,
            Username = config.Username, Authentication = config.Authentication, PrivateKeyPath = config.PrivateKeyPath, TimeoutSeconds = 20 };
        await using var session = new ServerSession(profile, store);
        var connected = false;
        await t.Run("REAL SSH: capability-only connection uses independent host fingerprint", async () =>
        {
            await session.ConnectAsync(new SessionCredentials(config.Secret), prompt =>
            {
                TestSuite.Equal(config.ExpectedHostFingerprint, prompt.Fingerprint);
                return Task.FromResult(TrustDecision.Session);
            });
            connected = true;
        });
        if (!connected) return;
        await t.Run("REAL Linux: Python and htop absent; installed top detected", async () =>
        {
            var r = await session.ExecuteAsync("for x in python python2 python3 htop; do if command -v \"$x\" >/dev/null 2>&1; then printf '%s=found\\n' \"$x\"; else printf '%s=missing\\n' \"$x\"; fi; done");
            TestSuite.Equal(0, r.ExitStatus);
            foreach (var name in new[] { "python", "python2", "python3", "htop" })
                TestSuite.True(r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Contains(name + "=missing", StringComparer.Ordinal));
            var monitor = new LinuxMonitor(async (cmd, ct) =>
            { var result = await session.ExecuteAsync(cmd, ct); return new ExecResult(result.Output, result.Error, result.ExitStatus); });
            var caps = await monitor.GetCapabilitiesAsync();
            TestSuite.True(!caps.HasHtop); TestSuite.True(caps.HasTop);
            Console.WriteLine("CAPABILITIES: python/python2/python3/htop missing; existing top available; no setup commands executed");
        });
    }

    internal static async Task<LinuxTestConfiguration> LoadConfiguration(string file)
    {
        var config = JsonSerializer.Deserialize<LinuxTestConfiguration>(await File.ReadAllTextAsync(file),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } })
            ?? throw new InvalidDataException("Empty test configuration");
        if (config.ExpectedHostFingerprint.Length == 0)
            throw new InvalidDataException("Integration fixture must provide ExpectedHostFingerprint independently");
        if (config.SecretFile is not null) config.Secret = (await File.ReadAllTextAsync(config.SecretFile)).TrimEnd('\r', '\n');
        return config;
    }

    private static async Task Wait(TransferQueue queue, TransferJob job)
    {
        var completion = queue.WaitAsync(job);
        while (!completion.IsCompleted)
        {
            var elapsed = await Task.WhenAny(completion, Task.Delay(TimeSpan.FromSeconds(10)));
            if (elapsed != completion)
                Console.WriteLine($"TRANSFER {job.Name}: {job.BytesTransferred}/{job.TotalBytes?.ToString() ?? "unknown"} bytes, {job.BytesPerSecond / 1024 / 1024:0.0} MiB/s, RSS {Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024} MiB");
        }
        await completion;
        if (job.State != TransferState.Completed) throw new Exception(job.Error ?? "Transfer did not complete");
        TestSuite.Equal(TransferState.Completed, job.State);
    }
}
#endif
