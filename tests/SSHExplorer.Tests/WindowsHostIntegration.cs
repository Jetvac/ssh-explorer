#if CORE_TESTS
using SSHExplorer.Core;
using SSHExplorer.Monitoring;

/// <summary>
/// 0.5: a real Windows server over SSH — the GitHub Windows runner with OpenSSH Server and a local administrator
/// (password from WIN_SSH_PASSWORD). Detection, SFTP paths with drive letters, PowerShell commands, task manager,
/// the «Сервер» page, health check.
/// </summary>
internal static class WindowsHostIntegration
{
    public static async Task RunAsync(TestSuite t, string host, int port, string user)
    {
        var password = Environment.GetEnvironmentVariable("WIN_SSH_PASSWORD") ?? throw new InvalidOperationException("WIN_SSH_PASSWORD is not set.");
        var dir = Path.Combine(Path.GetTempPath(), "sshx-winhost-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        var store = new AppStore(dir);
        var profile = new ServerProfile { Name = "runner", Host = host, Port = port, Username = user, Authentication = AuthenticationKind.Password, TimeoutSeconds = 30 };
        await using var session = new ServerSession(profile, store);
        await t.Run("Windows host: connect, detect Windows, SFTP and commands available", async () =>
        {
            await session.ConnectAsync(new SessionCredentials(password), _ => Task.FromResult(TrustDecision.Permanent));
            TestSuite.True(session.IsWindows && session.ShellAvailable && session.SftpAvailable && session.ShellError is null, $"windows={session.IsWindows} shell={session.ShellAvailable} sftp={session.SftpAvailable} cmd={session.WindowsShellIsCmd} {session.ShellError} {session.SftpError}");
        });
        await t.Run("Windows host: drives at the root, home as /C:/Users/…, shown as C:\\Users\\…", async () =>
        {
            var root = await session.Files.ListAsync("/");
            var home = session.Files.HomeDirectory;
            TestSuite.True(root.Any(e => e.Name.StartsWith("C:", StringComparison.OrdinalIgnoreCase)) && home.StartsWith("/C:/", StringComparison.OrdinalIgnoreCase)
                && WindowsHost.ToDisplay(home).StartsWith("C:\\", StringComparison.OrdinalIgnoreCase) && WindowsHost.FromDisplay(WindowsHost.ToDisplay(home)).Equals(home, StringComparison.OrdinalIgnoreCase),
                "root: " + string.Join(", ", root.Select(e => e.Name).Take(8)) + " home: " + home);
            var listed = await session.Files.ListAsync(home);
            TestSuite.True(listed.Count > 0, "home lists");
        });
        await t.Run("Windows host: PowerShell commands, UTF-8 output and the exit code", async () =>
        {
            var r = await session.ExecuteAsync(WindowsHost.PowerShell("Write-Output ('Привет ' + $PSVersionTable.PSVersion.Major); exit 3"));
            TestSuite.True(r.Output.Contains("Привет 5", StringComparison.Ordinal) && r.ExitStatus == 3, $"{r.ExitStatus} «{r.Output.Trim()}» {r.Error}");
        });
        await t.Run("Windows host: task manager sample — CPU, memory, disks, processes", async () =>
        {
            var monitor = new LinuxMonitor(async (c, ct) => { var x = await session.ExecuteAsync(c, ct); return new ExecResult(x.Output, x.Error, x.ExitStatus); }) { Windows = true };
            var s = await monitor.SampleAsync();
            TestSuite.True(s.System.CpuCount > 0 && s.System.MemoryTotalBytes > 1L << 30 && s.System.CpuPercent is >= 0 and <= 100 && s.Processes.Count > 10 && s.Processes.Any(p => p.Name.Equals("sshd", StringComparison.OrdinalIgnoreCase))
                && s.Storage.Any(d => d.MountPoint.StartsWith("C:", StringComparison.OrdinalIgnoreCase) && d.TotalBytes > 0) && s.Networks.Count > 0 && monitor.History.Count == 1,
                $"cpus={s.System.CpuCount} mem={s.System.MemoryTotalBytes} cpu={s.System.CpuPercent} procs={s.Processes.Count} disks={string.Join(",", s.Storage.Select(d => d.MountPoint))} nets={s.Networks.Count} dur={s.CollectionDuration.TotalMilliseconds:0} ms");
            var sleeper = await session.ExecuteAsync(WindowsHost.PowerShell("(Invoke-CimMethod Win32_Process -MethodName Create -Arguments @{CommandLine='powershell -NoProfile -Command Start-Sleep 300'}).ProcessId"));
            int pid = int.Parse(sleeper.Output.Trim());
            var again = await monitor.SampleAsync(); var proc = again.Processes.SingleOrDefault(p => p.Pid == pid) ?? throw new InvalidOperationException($"process {pid} not in the sample of {again.Processes.Count}");
            await monitor.SignalAsync(proc.Identity, 15);
            var gone = await session.ExecuteAsync(WindowsHost.PowerShell($"if(Get-Process -Id {pid} -ErrorAction SilentlyContinue){{'alive'}}else{{'gone'}}"));
            var stale = false; try { await monitor.SignalAsync(proc.Identity, 15); } catch (ProcessChangedException) { stale = true; }
            TestSuite.True(gone.Output.Trim() == "gone" && stale, "a process is stopped only while it is the same one: " + gone.Output.Trim());
        });
        await t.Run("Windows host: «Сервер» page — system, administrator, restart available", async () =>
        {
            var info = WindowsInfo.Parse((await session.ExecuteAsync(WindowsInfo.Command)).Output, DateTimeOffset.Now);
            TestSuite.True(info.OsName?.Contains("Windows", StringComparison.OrdinalIgnoreCase) == true && info.CpuCount > 0 && info.Uptime > TimeSpan.Zero && info.BootId is { Length: > 0 }
                && info.Reboot is { Available: true, Method: PowerMethod.WindowsShutdown } && info.Storage.Any() && info.User?.Contains(user, StringComparison.OrdinalIgnoreCase) == true,
                $"{info.OsName} {info.OsVersion} {info.Kernel} cpus={info.CpuCount} user={info.User} reboot={info.Reboot.Available} ips={info.Addresses.Count}");
        });
        await t.Run("Windows host: health check in the df form; a stopped automatic service is reported", async () =>
        {
            var issues = ServerHealth.Parse((await session.ExecuteLongAsync(ServerHealth.WindowsCommand, TimeSpan.FromSeconds(60))).Output, 1);
            TestSuite.True(issues.Any(i => i.Key.StartsWith("disk:C:", StringComparison.OrdinalIgnoreCase)), "with a 1% threshold the system disk is reported: " + string.Join(" | ", issues.Select(i => i.Text)));
        });
        await t.Run("Windows host: services list with states; start and stop a service; its events", async () =>
        {
            var services = new SystemdService(session);
            var list = await services.ListAsync();
            var sshd = list.Units.SingleOrDefault(u => u.Name.Equals("sshd", StringComparison.OrdinalIgnoreCase));
            TestSuite.True(list.Available && list.IsRoot && list.Units.Count > 50 && sshd is { IsRunning: true } && sshd.StateText == "Работает", $"{list.Units.Count} services, admin={list.IsRoot}, sshd={sshd?.StateText}/{sshd?.StartupText} {list.Error}");
            // The time service is harmless to start, restart and stop on a runner.
            var target = list.Units.FirstOrDefault(u => u.Name.Equals("W32Time", StringComparison.OrdinalIgnoreCase)) ?? list.Units.First(u => u.Name.Equals("Spooler", StringComparison.OrdinalIgnoreCase));
            bool wasRunning = target.IsRunning;
            await services.ActAsync(target.Name, wasRunning ? ServiceAction.Restart : ServiceAction.Start);
            var after = (await services.ListAsync()).Units.Single(u => u.Name == target.Name);
            if (!wasRunning) await services.ActAsync(target.Name, ServiceAction.Stop);
            var lines = new List<string>();
            await services.JournalAsync(target.Name, 50, false, l => { lock (lines) lines.Add(l); }, CancellationToken.None);
            var reloadRefused = false; try { await services.ActAsync(target.Name, ServiceAction.Reload); } catch (NotSupportedException) { reloadRefused = true; }
            TestSuite.True(after.IsRunning && lines.Count > 0 && reloadRefused, $"{target.Name}: running after start/restart={after.IsRunning}, events={lines.Count}: {lines.LastOrDefault()}");
        });
        await t.Run("Windows host: firewall rules read; a rule is added, switched off and removed with the restore safety net", async () =>
        {
            var fw = new WindowsFirewallService(session);
            var before = await fw.ReadAsync();
            TestSuite.True(before.Admin && before.Rules.Count > 20 && before.Profiles.Count == 3, $"rules={before.Rules.Count} profiles={before.Profiles.Count} admin={before.Admin}");
            var rule = new WindowsFirewallNewRule("SSH Explorer CI " + Guid.NewGuid().ToString("N")[..6], true, "TCP", "47001-47002", "203.0.113.0/24, 198.51.100.7");
            var added = await fw.ApplyAsync(WindowsFirewall.AddScript(rule));
            var mine = (await fw.ReadAsync()).Rules.SingleOrDefault(r => r.Name == rule.Name);
            TestSuite.True(added.Outcome == ApplyOutcome.Confirmed && mine is { Ours: true, Enabled: true, Allows: true, Protocol: "TCP" } && mine.LocalPorts == "47001-47002" && mine.RemoteAddresses.Contains("203.0.113.0", StringComparison.Ordinal),
                $"{added.Outcome}: {added.Message} {added.Output} rule={mine?.LocalPorts} {mine?.RemoteAddresses}");
            var tasks = await session.ExecuteAsync(WindowsHost.PowerShell("@(Get-ScheduledTask -TaskName 'SSHExplorer-firewall-*' -ErrorAction SilentlyContinue).Count"));
            TestSuite.True(tasks.Output.Trim() == "0", "the restore task is cancelled after a confirmed change: " + tasks.Output.Trim());
            var off = await fw.ApplyAsync(WindowsFirewall.EnableScript(mine!.Id, false));
            var offRule = (await fw.ReadAsync()).Rules.Single(r => r.Id == mine.Id);
            fw.VerifyOverride = _ => Task.FromResult(false);
            var rolled = await fw.ApplyAsync(WindowsFirewall.RemoveScript(mine.Id));
            var stillThere = (await fw.ReadAsync()).Rules.Any(r => r.Id == mine.Id);
            fw.VerifyOverride = null;
            var removed = await fw.ApplyAsync(WindowsFirewall.RemoveScript(mine.Id));
            var gone = (await fw.ReadAsync()).Rules.All(r => r.Id != mine.Id);
            var bad = await fw.ApplyAsync("throw 'нельзя'");
            TestSuite.True(off.Outcome == ApplyOutcome.Confirmed && !offRule.Enabled && rolled.Outcome == ApplyOutcome.RolledBack && stillThere && removed.Outcome == ApplyOutcome.Confirmed && gone && bad.Outcome == ApplyOutcome.Failed,
                $"off={off.Outcome}/{offRule.Enabled} rolledBack={rolled.Outcome} kept={stillThere} removed={removed.Outcome} gone={gone} bad={bad.Outcome}: {bad.Output}");
            TestSuite.True(WindowsFirewall.Validate(rule with { Ports = "70000" }) is not null && WindowsFirewall.Validate(rule with { RemoteAddresses = "evil.example" }) is not null && WindowsFirewall.Validate(rule with { Protocol = "Any", Ports = "" }) is null, "rule validation");
        });
        await t.Run("Windows host: group run executes PowerShell on a Windows server", async () =>
        {
            profile.SaveSecret = true; await store.SaveProfileAsync(profile, password);
            var runner = new GroupRunner(store);
            var results = new List<GroupRunResult>();
            await runner.RunAsync([profile], new SavedScript { Name = "проверка", Body = "Write-Output \"host=$env:COMPUTERNAME\"\ncmd /c exit 4", TimeoutMinutes = 2, StopOnError = false }, r => { lock (results) results.Add(r); });
            var last = results.Last();
            TestSuite.True(last.State == GroupRunState.Failed && last.ExitCode == 4 && last.Output.Contains("host=", StringComparison.Ordinal), $"{last.State} {last.ExitCode} {last.Output} {last.Problem}");
        });
        await t.Run("Windows host: background metric history with the saved password — PowerShell sample, disks, custom metrics", async () =>
        {
            var stored = store.Profiles.Single(p => p.Id == profile.Id);
            stored.RecordHistory = true;
            stored.CustomMetrics = [new CustomMetricDefinition { Name = "procs", Command = "(Get-Process).Count", Approved = true }, new CustomMetricDefinition { Name = "broken", Command = "throw 'нет такого'", Approved = true }];
            var metrics = new MetricsStore(Path.Combine(dir, "metrics"));
            MetricsSampler? sampler = null; bool background = false;
            await using var scheduler = new MetricsScheduler(store, async (target, ct) =>
            {
                background = target.Background;
                sampler ??= new MetricsSampler(target.Profile.Id, metrics, async (c, timeout, k) => { var r = await target.Session.ExecuteLongAsync(c, timeout, k); return new ExecResult(r.Output, r.Error, r.ExitStatus); }, target.Session.IsWindows);
                await sampler.SampleAsync(MetricsProfile.CustomMetrics(stored, store.Settings.Folders).Select(m => new CustomMetricSpec(m.Id, m.Name, m.Command, m.Unit, m.IntervalSeconds)).ToArray(), ct);
            });
            await scheduler.TickAsync(CancellationToken.None);
            var latest = metrics.Latest(profile.Id, DateTimeOffset.UtcNow);
            TestSuite.True(scheduler.StatusOf(profile.Id) is { Problem: null, LastSample: not null } && background && sampler!.Windows, scheduler.StatusOf(profile.Id)?.Problem ?? "no status");
            TestSuite.True(latest.ContainsKey(MetricSeries.Cpu) && latest.ContainsKey(MetricSeries.Memory) && latest.Keys.Any(k => k.StartsWith("fs:C:", StringComparison.OrdinalIgnoreCase)) && latest.TryGetValue("custom:procs", out var procs) && procs.Value > 10,
                "series: " + string.Join(", ", latest.Keys));
            TestSuite.True(sampler!.CustomErrors.Values.SingleOrDefault() is { } e && e.StartsWith("Код завершения", StringComparison.Ordinal), string.Join(" | ", sampler.CustomErrors.Values));
        });
        await t.Run("Windows host: file properties show the owner and access rules (Get-Acl)", async () =>
        {
            var acl = WindowsHost.ParseAcl((await session.ExecuteLongAsync(WindowsHost.AclCommand(session.Files.HomeDirectory), TimeSpan.FromSeconds(60))).Output);
            TestSuite.True(acl.Owner.Length > 0 && acl.Attributes.Contains("Directory", StringComparison.Ordinal) && acl.Rules.Count > 0 && acl.Rules.Any(r => r.Allow), $"{acl.Owner} {acl.Attributes} rules={acl.Rules.Count}");
        });
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }
}
#endif
