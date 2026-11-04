using System.Text;
using SSHExplorer.Monitoring;

var suite = new TestSuite();
#if CORE_TESTS
if (args.Length == 2 && args[0] == "--document-edit-integration")
{
    await DocumentEditIntegration.RunAsync(suite, args[1]);
    Console.WriteLine($"RESULT: {suite.Passed} real SFTP document checks passed, {suite.Failed} failed");
    return suite.Failed == 0 ? 0 : 1;
}
if (args.Length == 6 && args[0] == "--ports-integration")
{
    await PortsIntegration.RunAsync(suite, args[1], int.Parse(args[2]), args[3], int.Parse(args[4]), int.Parse(args[5]));
    Console.WriteLine($"RESULT: {suite.Passed} real ports checks passed, {suite.Failed} failed");
    return suite.Failed == 0 ? 0 : 1;
}
if (args.Length == 4 && args[0] == "--resume-integration")
{
    await ResumeIntegration.RunAsync(suite, args[1], int.Parse(args[2]), args[3]);
    Console.WriteLine($"RESULT: {suite.Passed} real resume checks passed, {suite.Failed} failed");
    return suite.Failed == 0 ? 0 : 1;
}
if (args.Length == 6 && args[0] == "--jump-integration")
{
    await JumpIntegration.RunAsync(suite, args[1], args[2], int.Parse(args[3]), int.Parse(args[4]), int.Parse(args[5]));
    Console.WriteLine($"RESULT: {suite.Passed} real jump-host checks passed, {suite.Failed} failed");
    return suite.Failed == 0 ? 0 : 1;
}
if (args.Length is 4 or 5 && args[0] == "--agent-integration")
{
    await AgentIntegration.RunAsync(suite, args[1], int.Parse(args[2]), args[3], args.Length == 5 ? args[4] : null);
    Console.WriteLine($"RESULT: {suite.Passed} real agent checks passed, {suite.Failed} failed");
    return suite.Failed == 0 ? 0 : 1;
}
if (args.Length == 1 && args[0] == "--document-edit")
{
    await DocumentEditTests.RunAsync(suite);
    Console.WriteLine($"RESULT: {suite.Passed} document edit checks passed, {suite.Failed} failed");
    return suite.Failed == 0 ? 0 : 1;
}
if (args.Length == 2 && args[0] == "--files-inline-integration")
{
    await FileCreationIntegration.RunAsync(suite, args[1]);
    Console.WriteLine($"RESULT: {suite.Passed} passed, {suite.Failed} failed");
    return suite.Failed == 0 ? 0 : 1;
}
if (args.Length == 2 && args[0] == "--collector-encoding-check")
    return await CollectorEncodingChecks.RunAsync(args[1]);
if (args.Length is 2 or 3 && args[0] == "--collector-benchmark")
    return await CollectorBenchmark.RunAsync(args[1], args.Length == 3 ? args[2] : null);
if (args.Length is 2 or 3 && args[0] == "--monitor-benchmark")
    return await MonitorBenchmark.RunAsync(args[1],args.Length==3?args[2]:null);
if (args.Length == 2 && args[0] == "--linux-capabilities")
{
    await LinuxIntegration.RunCapabilitiesOnlyAsync(suite, args[1]);
    Console.WriteLine($"RESULT: {suite.Passed} passed, {suite.Failed} failed");
    return suite.Failed == 0 ? 0 : 1;
}
if (args.Length == 2 && args[0] == "--network-failure")
{
    await NetworkIntegration.RunAsync(suite, args[1]);
    Console.WriteLine($"RESULT: {suite.Passed} passed, {suite.Failed} failed");
    return suite.Failed == 0 ? 0 : 1;
}
#endif
await MonitorTests.RunAsync(suite);
await MetricStateTests.RunAsync(suite);
await ServerPanelTests.RunAsync(suite);
#if CORE_TESTS
await CoreTests.RunAsync(suite);
await SshConfigTests.RunAsync(suite);
await AgentTests.RunAsync(suite);
await ProxyJumpTests.RunAsync(suite);
await ExternalImportTests.RunAsync(suite);
await TransferResumeTests.RunAsync(suite);
await PortsTests.RunAsync(suite);
await DocumentEditTests.RunAsync(suite);
if (args.Length == 2 && args[0] == "--linux-integration")
    await LinuxIntegration.RunAsync(suite, args[1]);
else if (args.Length != 0)
    throw new ArgumentException("Usage: SSHExplorer.Tests [--linux-integration|--linux-capabilities|--network-failure|--monitor-benchmark isolated-config.json [report.json]]");
#endif
Console.WriteLine($"RESULT: {suite.Passed} passed, {suite.Failed} failed");
return suite.Failed == 0 ? 0 : 1;

internal sealed class TestSuite
{
    public int Passed { get; private set; }
    public int Failed { get; private set; }
    public async Task Run(string name, Func<Task> test)
    {
        try { await test(); Passed++; Console.WriteLine($"PASS {name}"); }
        catch (Exception e) { Failed++; Console.WriteLine($"FAIL {name}: {e.GetType().Name}: {e.Message}"); }
    }
    public Task Run(string name, Action test) => Run(name, () => { test(); return Task.CompletedTask; });
    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; actual {actual}");
    }
    public static void True(bool condition, string message = "Assertion failed")
    { if (!condition) throw new Exception(message); }
    public static void Near(double expected, double? actual, double tolerance = 0.001)
    { if (actual is null || Math.Abs(expected - actual.Value) > tolerance) throw new Exception($"Expected {expected}; actual {actual}"); }
    public static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}");
    }
    public static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}");
    }
}

internal static class MonitorTests
{
    public static async Task RunAsync(TestSuite t)
    {
        await t.Run("proc/stat excludes guest double counting and counts CPUs", () =>
        {
            var cpu = ProcParser.ParseCpu("cpu 100 2 20 800 5 3 4 6 70 1\ncpu0 1 2 3 4\ncpu1 1 2 3 4\n")!;
            TestSuite.Equal(940UL, cpu.Total);
            TestSuite.Equal(805UL, cpu.Idle);
            TestSuite.Equal(2, cpu.CpuCount);
        });
        await t.Run("process stat preserves parentheses, newline, spaces and shell punctuation", () =>
        {
            var name = "worker ) name\n$;' )";
            var stat = ProcParser.ParseProcessStat(Stat(42, name, 12, 3, 777, 25))!;
            TestSuite.Equal(name, stat.Name);
            TestSuite.Equal(42, stat.Pid);
            TestSuite.Equal(15UL, stat.CpuTicks);
            TestSuite.Equal(777UL, stat.StartTimeTicks);
            TestSuite.Equal(25L, stat.ResidentPages);
        });
        await t.Run("malformed and overflowing counters stay unavailable", () =>
        {
            TestSuite.Equal<CpuCounters?>(null, ProcParser.ParseCpu("cpu 18446744073709551615 1 1 1"));
            TestSuite.Equal<ProcessStat?>(null, ProcParser.ParseProcessStat("42 (truncated) S 1 2"));
            TestSuite.Equal<double?>(null, MonitorCalculator.Rate(10, 20, 1));
            TestSuite.Equal<double?>(null, MonitorCalculator.Rate(10, 5, 0));
            TestSuite.Near(0, MonitorCalculator.Rate(10, 10, 1));
        });
        await t.Run("memory uses MemAvailable and KiB units", () =>
        {
            var m = ProcParser.ParseMemory("MemTotal: 1000 kB\nMemAvailable: 600 kB\nMemFree: 1 kB\nSwapTotal: 200 kB\nSwapFree: 50 kB\n");
            TestSuite.Equal(1024000L, m.TotalBytes);
            TestSuite.Equal(409600L, m.UsedBytes);
            TestSuite.Equal(153600L, m.SwapUsedBytes);
            TestSuite.True(!m.IsEstimate);
            var unavailable = ProcParser.ParseMemory(null);
            TestSuite.Equal<long?>(null, unavailable.UsedBytes);
        });
        await t.Run("memory fallback is explicitly estimated", () =>
        {
            var m = ProcParser.ParseMemory("MemTotal: 1000 kB\nMemFree: 100 kB\nBuffers: 100 kB\nCached: 200 kB\n");
            TestSuite.Equal(409600L, m.AvailableBytes);
            TestSuite.True(m.IsEstimate);
        });
        await t.Run("base64 framing preserves NUL and newline without record injection", () =>
        {
            var text = "a\0b\nEND\np/9/stat\t!\n";
            var frames = ProcParser.DecodeFrames(Protocol(new() { ["p/42/cmdline"] = text }));
            TestSuite.Equal(text, frames["p/42/cmdline"]);
        });
        await t.Run("truncated, duplicate and oversized protocol rejected", () =>
        {
            TestSuite.Throws<MonitoringUnavailableException>(() => ProcParser.DecodeFrames("SSHXMON1\nsys/stat\tYQ==\n"));
            TestSuite.Throws<MonitoringUnavailableException>(() => ProcParser.DecodeFrames("SSHXMON1\nsys/stat\tYQ==\nsys/stat\tYg==\nEND\n"));
            TestSuite.Throws<MonitoringUnavailableException>(() => ProcParser.DecodeFrames(new string('a', ProcParser.MaxProtocolCharacters + 1)));
        });
        await t.Run("real counter formulas: first sample unknown, second CPU50%, PID5%, net1KiB/s", () =>
        {
            var calc = new MonitorCalculator();
            var first = calc.Calculate(Sample(0), DateTimeOffset.UnixEpoch);
            TestSuite.Equal<double?>(null, first.System.CpuPercent);
            TestSuite.Equal<double?>(null, first.Processes[0].CpuPercent);
            var second = calc.Calculate(Sample(1), DateTimeOffset.UnixEpoch.AddSeconds(2));
            TestSuite.Near(50, second.System.CpuPercent);
            TestSuite.Near(5, second.Processes[0].CpuPercent);
            TestSuite.Near(1000, second.Networks.Single().ReceiveBytesPerSecond);
            TestSuite.Near(500, second.Networks.Single().SendBytesPerSecond);
            TestSuite.Near(1000, second.Processes[0].ReadBytesPerSecond);
            TestSuite.Near(512, second.Disks.Single().ReadBytesPerSecond);
            TestSuite.Equal("alice", second.Processes[0].User);
            TestSuite.Equal("prog 'argument with spaces'", second.Processes[0].CommandLine);
        });
        await t.Run("PID reuse does not inherit old CPU or I/O", () =>
        {
            var calc = new MonitorCalculator();
            calc.Calculate(Sample(0), DateTimeOffset.UnixEpoch);
            var next = calc.Calculate(Sample(1, start: 999), DateTimeOffset.UnixEpoch.AddSeconds(2));
            TestSuite.Equal<double?>(null, next.Processes.Single().CpuPercent);
            TestSuite.Equal<double?>(null, next.Processes.Single().ReadBytesPerSecond);
        });
        await t.Run("process disappearing midway is excluded rather than mixed", () =>
        {
            var f = SampleFrames(0);
            f["p/42/stat_end"] = Stat(42, "replacement", 20, 0, 999, 20);
            var snapshot = new MonitorCalculator().Calculate(Protocol(f), DateTimeOffset.UnixEpoch);
            TestSuite.Equal(0, snapshot.Processes.Count);
            TestSuite.True(snapshot.Limitations.Any(x => x.Contains("1 процессов", StringComparison.Ordinal)));
        });
        await t.Run("reboot resets CPU, network and process baselines", () =>
        {
            var calc = new MonitorCalculator();
            calc.Calculate(Sample(0), DateTimeOffset.UnixEpoch);
            var f = SampleFrames(1); f["sys/boot_id"] = "another-boot";
            var snapshot = calc.Calculate(Protocol(f), DateTimeOffset.UnixEpoch.AddSeconds(2));
            TestSuite.Equal<double?>(null, snapshot.System.CpuPercent);
            TestSuite.Equal<double?>(null, snapshot.Networks[0].ReceiveBytesPerSecond);
        });
        await t.Run("counter reset is not negative or fabricated zero", () =>
        {
            var calc = new MonitorCalculator();
            calc.Calculate(Sample(1), DateTimeOffset.UnixEpoch);
            var f = SampleFrames(0); f["sys/uptime"] = "104 0";
            var snapshot = calc.Calculate(Protocol(f), DateTimeOffset.UnixEpoch.AddSeconds(2));
            TestSuite.Equal<double?>(null, snapshot.System.CpuPercent);
            TestSuite.Equal<double?>(null, snapshot.Networks[0].ReceiveBytesPerSecond);
        });
        await t.Run("missing UID disables process signal; missing metrics do not become zeros", () =>
        {
            var f = SampleFrames(0); f["p/42/status"] = null; f["p/42/io"] = null; f["meta/pages"] = null;
            var p = new MonitorCalculator().Calculate(Protocol(f), DateTimeOffset.UnixEpoch).Processes.Single();
            TestSuite.True(!p.CanSignal);
            TestSuite.Equal<long?>(null, p.MemoryBytes);
            TestSuite.Equal<double?>(null, p.ReadBytesPerSecond);
        });
        await t.Run("df parser retains mount paths with spaces and 64-bit sizes", () =>
        {
            var storage = ProcParser.ParseStorage("Filesystem 1024-blocks Used Available Capacity Mounted on\n/dev/sda1 9000000000 1000000000 8000000000 12% /mount with spaces\n").Single();
            TestSuite.Equal(9216000000000L, storage.TotalBytes);
            TestSuite.Equal("/mount with spaces", storage.MountPoint);
        });
        await t.Run("monitor serializes requests and bounds history", async () =>
        {
            int active = 0, maxActive = 0, counter = 0;
            var monitor = new LinuxMonitor(async (_, ct) =>
            {
                var n = Interlocked.Increment(ref active);
                maxActive = Math.Max(maxActive, n);
                await Task.Delay(5, ct);
                var output = Sample(Interlocked.Increment(ref counter));
                Interlocked.Decrement(ref active);
                return new ExecResult(output, "", 0);
            }, historyCapacity: 2);
            await Task.WhenAll(monitor.SampleAsync(), monitor.SampleAsync(), monitor.SampleAsync());
            TestSuite.Equal(1, maxActive);
            TestSuite.Equal(2, monitor.History.Count);
        });
        await t.Run("signal protects PID1, identity and allowed signals", async () =>
        {
            int calls = 0;
            var monitor = new LinuxMonitor((_, _) => { calls++; return Task.FromResult(new ExecResult("", "", 0)); });
            await TestSuite.ThrowsAsync<InvalidOperationException>(() => monitor.SignalAsync(new(1, 100, 1000), 15));
            await TestSuite.ThrowsAsync<InvalidOperationException>(() => monitor.SignalAsync(new(42, 100, uint.MaxValue), 15));
            await TestSuite.ThrowsAsync<ArgumentOutOfRangeException>(() => monitor.SignalAsync(new(42, 100, 1000), 2));
            TestSuite.Equal(0, calls);
        });
        await t.Run("signal rejects changed identity and leaves server rights unchanged", async () =>
        {
            string command = "";
            var monitor = new LinuxMonitor((cmd, _) => { command = cmd; return Task.FromResult(new ExecResult("", "", 74)); });
            await TestSuite.ThrowsAsync<ProcessChangedException>(() => monitor.SignalAsync(new(42, 777, 1000), 15));
            TestSuite.True(command.Contains("'777'", StringComparison.Ordinal));
            TestSuite.True(command.Contains("'1000'", StringComparison.Ordinal));
            TestSuite.True(command.Contains("kill -15", StringComparison.Ordinal));
            TestSuite.True(!command.Contains("sudo", StringComparison.Ordinal));
        });
        await t.Run("missing existing utility yields precise limitation", async () =>
        {
            var monitor = new LinuxMonitor((_, _) => Task.FromResult(new ExecResult("SSHXERROR:missing:base64\n", "", 72)));
            try { await monitor.SampleAsync(); throw new Exception("Expected unavailable"); }
            catch (MonitoringUnavailableException e) { TestSuite.True(e.Message.Contains("base64", StringComparison.Ordinal)); }
        });
        await t.Run("collector rejects unbounded PID limits and no server setup commands", () =>
        {
            TestSuite.Throws<ArgumentOutOfRangeException>(() => CollectorCommand.Create(0));
            TestSuite.Throws<ArgumentOutOfRangeException>(() => CollectorCommand.Create(4097));
            var command = CollectorCommand.Create(123);
            TestSuite.True(command.Contains("-ge 123", StringComparison.Ordinal));
            foreach (var prohibited in new[] { "apt ", "yum ", "pip ", "systemctl ", "sudo ", "curl ", "wget " })
                TestSuite.True(!command.Contains(prohibited, StringComparison.Ordinal));
            TestSuite.True(!command.Contains("/proc/$p/net/dev", StringComparison.Ordinal));
        });
    }

    public static string Stat(int pid, string name, ulong userTicks, ulong systemTicks, ulong start, long rss)
    {
        var fields = Enumerable.Repeat("0", 50).ToArray();
        fields[0] = "S"; fields[11] = userTicks.ToString(); fields[12] = systemTicks.ToString();
        fields[19] = start.ToString(); fields[21] = rss.ToString();
        return $"{pid} ({name}) {string.Join(' ', fields)}\n";
    }

    public static string Protocol(Dictionary<string, string?> frames)
    {
        var text = new StringBuilder("SSHXMON1\n");
        foreach (var (key, value) in frames)
            text.Append(key).Append('\t').Append(value is null ? "!" : Convert.ToBase64String(Encoding.UTF8.GetBytes(value))).Append('\n');
        return text.Append("END\n").ToString();
    }

    public static string Sample(int n, ulong start = 777) => Protocol(SampleFrames(n, start));
    public static Dictionary<string, string?> SampleFrames(int n, ulong start = 777)
    {
        var stat = Stat(42, "worker ) name", (ulong)(20 + 10 * n), 0, start, 20);
        return new Dictionary<string, string?>
        {
            ["sys/stat"] = $"cpu {100 + 50 * n} 0 {100 + 50 * n} {800 + 100 * n} 0 0 0 0\ncpu0 1 0 1 1\ncpu1 1 0 1 1\n",
            ["sys/meminfo"] = "MemTotal: 1000 kB\nMemAvailable: 600 kB\nSwapTotal: 200 kB\nSwapFree: 50 kB\n",
            ["sys/uptime"] = $"{100 + n * 2} 0\n", ["sys/loadavg"] = "0.1 0.2 0.3 1/100 42\n",
            ["sys/boot_id"] = "boot-id\n", ["sys/passwd"] = "alice:x:1000:1000::/home/alice:/bin/sh\n",
            ["meta/ticks"] = "100", ["meta/pages"] = "4096", ["meta/count"] = "1", ["meta/limited"] = "0",
            ["sys/net"] = $"eth0: {1000 + 2000 * n} 0 0 0 0 0 0 0 {2000 + 1000 * n} 0 0 0 0 0 0 0\n",
            ["sys/disks"] = $"8 0 sda 1 0 {10 + 2 * n} 0 1 0 {20 + 4 * n} 0 0 0 0\n",
            ["sys/df"] = "/dev/sda1 1000 400 600 40% /\n",
            ["p/42/stat"] = stat, ["p/42/stat_end"] = stat,
            ["p/42/status"] = "Name:\tworker\nUid:\t1000\t1000\t1000\t1000\nVmRSS:\t80 kB\n",
            ["p/42/cmdline"] = "prog\0argument with spaces\0", ["p/42/io"] = $"read_bytes: {100 + 2000 * n}\nwrite_bytes: {200 + 4000 * n}\n"
        };
    }
}
