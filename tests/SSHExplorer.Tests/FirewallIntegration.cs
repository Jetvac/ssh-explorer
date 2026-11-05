#if CORE_TESTS
using System.Text;
using SSHExplorer.Core;

/// <summary>
/// Real ufw on the CI runner (passwordless sudo): read, safe apply confirmed by a new connection, immediate restore
/// when the check fails, the scheduled restore when nobody confirms, and the jump-only plan. ufw is reset and turned
/// off afterwards. Loopback traffic always passes ufw, so this proves the mechanics, not a real lockout.
/// Arguments: host port user.
/// </summary>
internal static class FirewallIntegration
{
    public static async Task RunAsync(TestSuite t, string host, int port, string user)
    {
        var store = new AppStore(Path.Combine(Path.GetTempPath(), "sshx-fw-store-" + Guid.NewGuid().ToString("N")));
        await using var session = new ServerSession(new ServerProfile { Name = "Файервол · стенд", Host = host, Port = port, Username = user, Authentication = AuthenticationKind.Agent }, store);
        await session.ConnectAsync(new SessionCredentials(""), _ => Task.FromResult(TrustDecision.Session));
        async Task<string> Sh(string command) => (await session.ExecuteLongAsync(command, TimeSpan.FromMinutes(2))).Output;
        await Sh("sudo -n ufw --force reset >/dev/null 2>&1; sudo -n ufw disable >/dev/null 2>&1; true");
        var firewall = new FirewallService(session);
        try
        {
            var problem = await firewall.ProbeAsync();
            var first = await firewall.ReadAsync();
            await t.Run("firewall integration: rights via sudo -n, ufw installed and off, this connection's address and port are known", () =>
                TestSuite.True(problem is null && firewall.Rights is "sudo" or "root" && first.Installed && !first.Active && first.ClientAddress == "127.0.0.1" && first.SshPort == port && first.Problem is null,
                    $"problem={problem} rights={firewall.Rights} installed={first.Installed} active={first.Active} client={first.ClientAddress} port={first.SshPort} {first.Problem}"));

            await t.Run("firewall integration: a confirmed safe apply adds rules and turns ufw on; the restore timer is cancelled", async () =>
            {
                var commands = new[] { Ufw.AddCommand(new RuleDraft { From = "127.0.0.1", Port = port.ToString(), Comment = "sshx test ssh" }, 0), Ufw.AddCommand(new RuleDraft { Port = "8443", Comment = "sshx test web" }, 1), Ufw.EnableCommand };
                var stages = new List<string>();
                var result = await firewall.ApplyAsync(commands, stages.Add);
                var after = await firewall.ReadAsync();
                await Task.Delay(1500);
                var leftovers = (await Sh("ls -d /var/tmp/sshx-ufw-* 2>/dev/null | wc -l; systemctl list-timers --all 2>/dev/null | grep -c sshx-ufw-rollback || true")).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                TestSuite.True(result.Outcome == ApplyOutcome.Confirmed && after.Active && after.Rules.Any(r => r.Comment == "sshx test web" && r.ToPort == "8443") && after.Rules.Any(r => r.From == "127.0.0.1")
                    && stages.Count >= 2 && leftovers[0].Trim() == "0" && leftovers[^1].Trim() == "0", $"{result.Outcome} {result.Message} active={after.Active} rules={after.Rules.Count} leftovers={string.Join(",", leftovers)}");
            });

            await t.Run("firewall integration: when the new connection fails the change is restored at once", async () =>
            {
                var failing = new FirewallService(session) { VerifyOverride = _ => Task.FromResult(false) };
                await failing.ProbeAsync();
                var result = await failing.ApplyAsync([Ufw.AddCommand(new RuleDraft { Action = FirewallAction.Deny, Port = "9999", Comment = "sshx should vanish" }, 2)]);
                var after = await firewall.ReadAsync();
                TestSuite.True(result.Outcome == ApplyOutcome.RolledBack && after.Active && !after.Rules.Any(r => r.Comment == "sshx should vanish") && after.Rules.Any(r => r.Comment == "sshx test web"), $"{result.Outcome} rules={string.Join(" | ", after.Rules.Select(r => r.Spec))}");
            });

            await t.Run("firewall integration: a refused command restores the previous rules and reports it", async () =>
            {
                var result = await firewall.ApplyAsync([Ufw.AddCommand(new RuleDraft { Port = "7001", Comment = "sshx half" }, 2), "ufw allow 99999/tcp"]);
                var after = await firewall.ReadAsync();
                TestSuite.True(result.Outcome == ApplyOutcome.Failed && !after.Rules.Any(r => r.Comment == "sshx half"), $"{result.Outcome} {result.Message} {result.Output}");
            });

            await t.Run("firewall integration: if nobody confirms, the scheduled restore returns the old rules by itself", async () =>
            {
                const string id = "sshxtimer01";
                var script = FirewallScripts.SafeApply(id, [Ufw.AddCommand(new RuleDraft { Port = "7777", Comment = "sshx unconfirmed" }, 2)], 6);
                var path = "/tmp/sshx-ufw-timer-test.sh";
                await Sh($"printf %s {PosixPath.Quote(Convert.ToBase64String(Encoding.UTF8.GetBytes(script)))} | base64 -d > {path}");
                var applied = await Sh($"sudo -n sh {path} 2>&1; rm -f {path}");
                var during = await firewall.ReadAsync();
                string? after = null;
                for (int i = 0; i < 30; i++)
                {
                    await Task.Delay(1000);
                    var snap = await firewall.ReadAsync();
                    if (!snap.Rules.Any(r => r.Comment == "sshx unconfirmed")) { after = string.Join(" | ", snap.Rules.Select(r => r.Spec)); break; }
                }
                var gone = (await Sh($"[ -d {FirewallScripts.Directory(id)} ] && echo present || echo gone")).Trim();
                TestSuite.True(applied.Contains("#applied", StringComparison.Ordinal) && during.Rules.Any(r => r.Comment == "sshx unconfirmed") && after is not null && after.Contains("8443", StringComparison.Ordinal) && gone == "gone",
                    $"applied={applied.Trim()} during={during.Rules.Count} after={after} dir={gone}");
            });

            await t.Run("firewall integration: the jump-only plan applies with confirmation; SSH stays open only for the allowed address", async () =>
            {
                var s = await firewall.ReadAsync();
                var open = Ufw.OpenSshRules(s.Rules, port, ["127.0.0.1"]);
                await firewall.ApplyAsync([Ufw.AddCommand(new RuleDraft { Port = port.ToString(), Comment = "sshx open to all" }, s.Rules.Count)]);
                s = await firewall.ReadAsync(); open = Ufw.OpenSshRules(s.Rules, port, ["127.0.0.1"]);
                var plan = Ufw.BastionCommands(s, ["127.0.0.1"], port, open, [], "стенд");
                var result = await firewall.ApplyAsync(plan);
                var after = await firewall.ReadAsync();
                TestSuite.True(open.Any(r => r.Comment == "sshx open to all") && result.Outcome == ApplyOutcome.Confirmed && !after.Rules.Any(r => r.Comment == "sshx open to all")
                    && Ufw.CheckAccess(after.Rules, after.Active, after.DefaultIncoming, "127.0.0.1", port).Allowed == true && Ufw.CheckAccess(after.Rules, after.Active, after.DefaultIncoming, "198.51.100.9", port).Allowed == false,
                    $"{result.Outcome} {result.Message} rules={string.Join(" | ", after.Rules.Select(r => r.Spec))}");
            });
        }
        finally { await Sh("sudo -n ufw --force reset >/dev/null 2>&1; sudo -n ufw disable >/dev/null 2>&1; sudo -n rm -rf /var/tmp/sshx-ufw-*; true"); }
    }
}
#endif
