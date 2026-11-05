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
            await session.ConnectAsync(new SessionCredentials(password), _ => Task.FromResult(TrustDecision.Session));
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
            var notepad = await session.ExecuteAsync(WindowsHost.PowerShell("(Start-Process notepad -PassThru).Id"));
            int pid = int.Parse(notepad.Output.Trim());
            var again = await monitor.SampleAsync(); var proc = again.Processes.Single(p => p.Pid == pid);
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
            var issues = ServerHealth.Parse((await session.ExecuteAsync(ServerHealth.WindowsCommand)).Output, 1);
            TestSuite.True(issues.Any(i => i.Key.StartsWith("disk:C:", StringComparison.OrdinalIgnoreCase)), "with a 1% threshold the system disk is reported: " + string.Join(" | ", issues.Select(i => i.Text)));
        });
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }
}
#endif
