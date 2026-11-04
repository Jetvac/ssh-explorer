using System.Text;
using SSHExplorer.Monitoring;

/// <summary>Server panel facts and the power-operation state machine on a controlled executor.
/// No real reboot or power-off is ever sent by these tests.</summary>
internal static class ServerPanelTests
{
    static string Frame(string key, string? value) => key + "\t" + (value is null ? "!" : Convert.ToBase64String(Encoding.UTF8.GetBytes(value))) + "\n";
    public static string Info(string uid = "1000", string user = "alice", string? canReboot = null, string sudoReboot = "0", string tools = " systemctl busctl shutdown sudo ip df", string systemd = "1", string? bootId = "b1\n", string uptime = "3600.5 7000\n")
    {
        var text = new StringBuilder("SSHXINFO1\n");
        text.Append(Frame("os", "NAME=\"Ubuntu\"\nVERSION=\"24.04 LTS (Noble Numbat)\"\nID=ubuntu\nPRETTY_NAME=\"Ubuntu 24.04 LTS\"\n# comment\nBAD LINE\nHOME_URL=\"https://x/$(reboot)\"\n"));
        text.Append(Frame("hostname", "srv-01\n")).Append(Frame("kernel", "6.8.0-45-generic\n"));
        text.Append(Frame("cpuinfo", "processor\t: 0\nmodel name\t: AMD EPYC 7B13\nprocessor\t: 1\nmodel name\t: AMD EPYC 7B13\n"));
        text.Append(Frame("meminfo", "MemTotal: 4000000 kB\nMemAvailable: 1000000 kB\nSwapTotal: 0 kB\nSwapFree: 0 kB\n"));
        text.Append(Frame("uptime", uptime)).Append(Frame("loadavg", "0.50 0.25 0.10 1/200 999\n")).Append(Frame("boot_id", bootId));
        text.Append(Frame("arch", "x86_64")).Append(Frame("user", user)).Append(Frame("uid", uid)).Append(Frame("systemd", systemd)).Append(Frame("tools", tools));
        text.Append(Frame("addr", "1: lo    inet 127.0.0.1/8 scope host lo\n2: eth0    inet 10.0.0.5/24 brd 10.0.0.255 scope global eth0\n2: eth0    inet6 fe80::1/64 scope link\n"));
        text.Append(Frame("df", "Filesystem 1024-blocks Used Available Capacity Mounted on\n/dev/sda1 1000 400 600 40% /\n"));
        if (canReboot is not null) { text.Append(Frame("can_reboot", canReboot)); text.Append(Frame("can_poweroff", canReboot)); }
        text.Append(Frame("sudo_reboot", sudoReboot)).Append(Frame("sudo_poweroff", sudoReboot));
        return text.Append("END\n").ToString();
    }

    public static async Task RunAsync(TestSuite t)
    {
        await t.Run("server info parses os-release as data, CPU, memory, load, addresses and storage", () =>
        {
            var info = ServerInfoCollector.Parse(Info(), DateTimeOffset.UnixEpoch);
            TestSuite.Equal("Ubuntu 24.04 LTS", info.OsName); TestSuite.Equal("srv-01", info.Hostname); TestSuite.Equal("6.8.0-45-generic", info.Kernel);
            TestSuite.Equal(2, info.CpuCount ?? 0); TestSuite.Equal("AMD EPYC 7B13", info.CpuModel); TestSuite.Near(0.5, info.Load1);
            TestSuite.True(info.Addresses.Count == 2 && info.Addresses.All(a => a.Interface == "eth0"));
            TestSuite.Equal(1, info.Storage.Count); TestSuite.Equal(TimeSpan.FromSeconds(3600.5), info.Uptime);
            TestSuite.True(ServerInfoCollector.ParseOsRelease("HOME_URL=\"https://x/$(reboot)\"")["HOME_URL"] == "https://x/$(reboot)");
            foreach (var forbidden in new[] { "apt ", "yum ", "dnf ", "visudo", "sudoers", "--force", "kill " })
                TestSuite.True(!ServerInfoCollector.Command.Contains(forbidden, StringComparison.Ordinal), "info command must stay read-only: " + forbidden);
            // Power words appear only as capability checks: the tool list, logind Can* queries and "sudo -n -l" (list, not run).
            foreach (var line in ServerInfoCollector.Command.Split('\n').Where(l => l.Contains("reboot", StringComparison.Ordinal) || l.Contains("poweroff", StringComparison.Ordinal) || l.Contains("-h now", StringComparison.Ordinal)))
                TestSuite.True(line.Contains("for t in", StringComparison.Ordinal) || line.Contains("CanReboot", StringComparison.Ordinal) || line.Contains("CanPowerOff", StringComparison.Ordinal) || line.Contains("sudo -n -l", StringComparison.Ordinal), "power word outside a read-only check: " + line.Trim());
        });
        await t.Run("power capability picks exactly one permitted method and never treats the SSH password as sudo", () =>
        {
            var root = ServerInfoCollector.Parse(Info(uid: "0", user: "root"), DateTimeOffset.UnixEpoch);
            TestSuite.True(root.Reboot.Method == PowerMethod.SystemctlAsRoot && root.Reboot.Command == "systemctl reboot" && root.PowerOff.Command == "systemctl poweroff");
            var sysv = ServerInfoCollector.Parse(Info(uid: "0", user: "root", tools: " shutdown", systemd: "0"), DateTimeOffset.UnixEpoch);
            TestSuite.True(sysv.Reboot.Method == PowerMethod.SysVAsRoot && sysv.Reboot.Command == "shutdown -r now");
            var logind = ServerInfoCollector.Parse(Info(canReboot: "s \"yes\""), DateTimeOffset.UnixEpoch);
            TestSuite.True(logind.Reboot.Method == PowerMethod.SystemctlViaLogind && !logind.Reboot.Command.Contains("sudo", StringComparison.Ordinal));
            var sudo = ServerInfoCollector.Parse(Info(canReboot: "s \"challenge\"", sudoReboot: "1"), DateTimeOffset.UnixEpoch);
            TestSuite.True(sudo.Reboot.Method == PowerMethod.SystemctlViaSudo && sudo.Reboot.Command == "sudo -n systemctl reboot");
            var denied = ServerInfoCollector.Parse(Info(canReboot: "s \"challenge\""), DateTimeOffset.UnixEpoch);
            TestSuite.True(!denied.Reboot.Available && denied.Reboot.Explanation.Contains("Пароль SSH не используется", StringComparison.Ordinal));
            TestSuite.True(!denied.Reboot.Command.Contains("--force", StringComparison.Ordinal) && !root.Reboot.Command.Contains("-f", StringComparison.Ordinal));
        });
        var reboot = new PowerCapability(PowerAction.Reboot, PowerMethod.SystemctlAsRoot, "systemctl reboot", "root");
        var poweroff = new PowerCapability(PowerAction.PowerOff, PowerMethod.SystemctlAsRoot, "systemctl poweroff", "root");
        var fast = new PowerTimings(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(400));
        var before = new BootMarker("b1", TimeSpan.FromHours(1), DateTimeOffset.UnixEpoch);
        await t.Run("accepted reboot waits for disconnect then service, sends one command and verifies boot_id", async () =>
        {
            int sends = 0, alive = 3, reachable = 0;
            var op = new PowerOperation(reboot, before, (cmd, _) => { sends++; return Task.FromResult(new ExecResult("", "", 0)); }, _ => Task.FromResult(--alive > 0), _ => Task.FromResult(++reachable > 2), fast);
            var stage = await op.RunAsync(CancellationToken.None);
            TestSuite.Equal(PowerStage.ServiceReachable, stage); TestSuite.Equal(1, sends); TestSuite.Equal(1, op.CommandsSent);
            TestSuite.True(op.Log.Select(l => l.Stage).SequenceEqual([PowerStage.Sending, PowerStage.Accepted, PowerStage.WaitingForDisconnect, PowerStage.Disconnected, PowerStage.WaitingForService, PowerStage.ServiceReachable]));
            TestSuite.Equal(PowerStage.Confirmed, PowerOperation.Verify(before, new BootMarker("b2", TimeSpan.FromSeconds(40), DateTimeOffset.UnixEpoch.AddMinutes(2))).Stage);
            TestSuite.Equal(PowerStage.NotConfirmed, PowerOperation.Verify(before, new BootMarker("b1", TimeSpan.FromHours(1.05), DateTimeOffset.UnixEpoch.AddMinutes(3))).Stage);
            TestSuite.Equal(PowerStage.Confirmed, PowerOperation.Verify(before with { BootId = null }, new BootMarker(null, TimeSpan.FromSeconds(30), DateTimeOffset.UnixEpoch.AddMinutes(2))).Stage);
            await TestSuite.ThrowsAsync<InvalidOperationException>(() => op.RunAsync(CancellationToken.None));
            TestSuite.Equal(1, sends);
        });
        await t.Run("refusal is reported with its code and no alternative command is tried", async () =>
        {
            var commands = new List<string>();
            var op = new PowerOperation(reboot, before, (cmd, _) => { commands.Add(cmd); return Task.FromResult(new ExecResult("", "Interactive authentication required.", 1)); }, _ => Task.FromResult(true), _ => Task.FromResult(true), fast);
            TestSuite.Equal(PowerStage.Rejected, await op.RunAsync(CancellationToken.None));
            TestSuite.True(commands.Count == 1 && op.Log[^1].Message.Contains("код 1", StringComparison.Ordinal) && op.Log[^1].Message.Contains("Interactive", StringComparison.Ordinal));
        });
        await t.Run("timeout before exit status is an unknown result and the command is not repeated", async () =>
        {
            int sends = 0;
            var op = new PowerOperation(poweroff, before, async (_, ct) => { sends++; await Task.Delay(Timeout.Infinite, ct); return new ExecResult("", "", 0); }, _ => Task.FromResult(false), _ => Task.FromResult(false), fast);
            TestSuite.Equal(PowerStage.ResultUnknown, await op.RunAsync(CancellationToken.None)); TestSuite.Equal(1, sends);
        });
        await t.Run("power-off without an external channel ends at disconnected, never at powered-off", async () =>
        {
            int alive = 2;
            var op = new PowerOperation(poweroff, before, (_, _) => Task.FromResult(new ExecResult("", "", 0)), _ => Task.FromResult(--alive > 0), _ => throw new InvalidOperationException("must not probe"), fast);
            TestSuite.Equal(PowerStage.Disconnected, await op.RunAsync(CancellationToken.None));
            TestSuite.True(op.Log[^1].Message.Contains("подтвердить нельзя", StringComparison.Ordinal));
        });
        await t.Run("accepted command with a server that stays up reports StillReachable without resending", async () =>
        {
            int sends = 0;
            var op = new PowerOperation(reboot, before, (_, _) => { sends++; return Task.FromResult(new ExecResult("", "", 0)); }, _ => Task.FromResult(true), _ => Task.FromResult(true), fast);
            TestSuite.Equal(PowerStage.StillReachable, await op.RunAsync(CancellationToken.None)); TestSuite.Equal(1, sends);
        });
        await t.Run("stopping the wait after acceptance is Abandoned and states the command is not cancelled", async () =>
        {
            using var stop = new CancellationTokenSource();
            var op = new PowerOperation(reboot, before, (_, _) => Task.FromResult(new ExecResult("", "", 0)), async ct => { stop.Cancel(); await Task.Yield(); return true; }, _ => Task.FromResult(false), fast);
            TestSuite.Equal(PowerStage.Abandoned, await op.RunAsync(stop.Token));
            TestSuite.True(op.Log.Any(l => l.Stage == PowerStage.Accepted) && op.Log[^1].Message.Contains("не отменяется", StringComparison.Ordinal));
        });
        await t.Run("service that never returns ends Unreachable; verification registry is consumed once", async () =>
        {
            var op = new PowerOperation(reboot, before, (_, _) => Task.FromResult(new ExecResult("", "", 0)), _ => Task.FromResult(false), _ => Task.FromResult(false), fast);
            TestSuite.Equal(PowerStage.Unreachable, await op.RunAsync(CancellationToken.None));
            var id = Guid.NewGuid();
            PowerVerificationRegistry.Register(id, "srv:22", before);
            TestSuite.True(PowerVerificationRegistry.IsPending(id, "srv:22"));
            TestSuite.Equal(PowerStage.Confirmed, PowerVerificationRegistry.Complete(id, "srv:22", new BootMarker("b9", TimeSpan.FromSeconds(20), DateTimeOffset.UnixEpoch.AddMinutes(1)))!.Value.Stage);
            TestSuite.True(PowerVerificationRegistry.Complete(id, "srv:22", new BootMarker("b9", null, DateTimeOffset.UnixEpoch)) is null);
        });
        await t.Run("unavailable capability cannot construct an operation", () => TestSuite.Throws<InvalidOperationException>(() => new PowerOperation(new PowerCapability(PowerAction.Reboot, PowerMethod.None, "", "нет прав"), null, (_, _) => throw new Exception(), _ => throw new Exception(), _ => throw new Exception())));
        await t.Run("read-only info command runs on a real Linux shell (Linux hosts only)", async () =>
        {
            if (!OperatingSystem.IsLinux() || !File.Exists("/bin/sh")) { Console.WriteLine("SKIP real info collector: not a Linux host"); return; }
            var info = new System.Diagnostics.ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, UseShellExecute = false }; info.ArgumentList.Add("-c"); info.ArgumentList.Add(ServerInfoCollector.Command);
            using var process = System.Diagnostics.Process.Start(info)!; var output = await process.StandardOutput.ReadToEndAsync(); await process.WaitForExitAsync();
            var parsed = ServerInfoCollector.Parse(output, DateTimeOffset.UtcNow);
            TestSuite.True(parsed.Kernel is not null && parsed.CpuCount > 0 && parsed.MemoryTotalBytes > 0 && parsed.BootId is not null && parsed.Uptime is not null);
            Console.WriteLine($"INFO real host: {parsed.OsName}; kernel {parsed.Kernel}; reboot method {parsed.Reboot.Method}");
        });
    }
}
