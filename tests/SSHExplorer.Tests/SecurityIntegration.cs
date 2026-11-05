#if CORE_TESTS
using SSHExplorer.Core;

/// <summary>
/// Real server: a key made here is installed into authorized_keys, a new connection logs in with it, it is removed
/// and the login stops working. Then sshd hardening on the runner's own /etc/ssh: the safe apply is confirmed,
/// restored when the check fails, and refused by sshd -t. The test connects to a separate fixture sshd, so this proves
/// the mechanics, not a real lock-out. /etc/ssh is restored afterwards. Arguments: host port user.
/// </summary>
internal static class SecurityIntegration
{
    public static async Task RunAsync(TestSuite t, string host, int port, string user)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sshx-sec-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        var store = new AppStore(Path.Combine(dir, "store"));
        await using var session = new ServerSession(new ServerProfile { Name = "Защита · стенд", Host = host, Port = port, Username = user, Authentication = AuthenticationKind.Agent }, store);
        await session.ConnectAsync(new SessionCredentials(""), _ => Task.FromResult(TrustDecision.Session));
        async Task<string> Sh(string command) => (await session.ExecuteLongAsync(command, TimeSpan.FromMinutes(2))).Output;
        var keys = new KeyLoginService(session, store);
        var generated = SshKeys.GenerateEd25519("sshx-integration");
        var path = Path.Combine(dir, "id_test");
        await File.WriteAllTextAsync(path, generated.PrivateKeyText);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var pub = SshKeys.Parse(generated.PublicLine)!;
        try
        {
            await t.Run("security integration: a key made here is added once and a brand-new connection logs in with it", async () =>
            {
                var before = await keys.ListAsync();
                bool added = await keys.InstallAsync(pub), again = await keys.InstallAsync(pub);
                var listed = await keys.ListAsync();
                var login = await keys.VerifyAsync(path, null);
                TestSuite.True(added && !again && listed.Count == before.Count + 1 && listed.Any(k => k.Fingerprint == generated.Fingerprint) && login is null, $"added={added} again={again} count={before.Count}->{listed.Count} login={login}");
            });
            await t.Run("security integration: removing the key stops its login; other keys stay", async () =>
            {
                var before = await keys.ListAsync();
                await keys.RemoveAsync(before.Single(k => k.Fingerprint == generated.Fingerprint));
                var after = await keys.ListAsync();
                var login = await keys.VerifyAsync(path, null);
                var mode = (await Sh("stat -c %a ~/.ssh/authorized_keys")).Trim();
                TestSuite.True(after.Count == before.Count - 1 && after.All(k => k.Fingerprint != generated.Fingerprint) && login is not null && mode == "600", $"{before.Count}->{after.Count} login={login} mode={mode}");
            });

            var sshd = new SshdService(session);
            await sshd.ProbeAsync();
            var first = await sshd.ReadAsync();
            await t.Run("security integration: sshd -T gives the effective settings with root rights", () =>
                TestSuite.True(sshd.CanChange && first.Problem is null && first.Effective && first.Get("pubkeyauthentication").Length > 0 && first.User == user, $"rights={sshd.Rights} problem={first.Problem} effective={first.Effective}"));

            await t.Run("security integration: a confirmed change is written, re-read by sshd -T and the restore timer is cancelled", async () =>
            {
                var result = await sshd.ApplyAsync(first, new Dictionary<string, string> { ["MaxAuthTries"] = "4", ["X11Forwarding"] = "no" });
                var after = await sshd.ReadAsync();
                var leftovers = (await Sh("ls -d /var/tmp/sshx-sshd-* 2>/dev/null | wc -l")).Trim();
                TestSuite.True(result.Outcome == ApplyOutcome.Confirmed && after.Get("maxauthtries") == "4" && after.Get("x11forwarding") == "no" && after.Ours["MaxAuthTries"] == "4" && leftovers == "0", $"{result.Outcome} {result.Message} {result.Output} tries={after.Get("maxauthtries")} left={leftovers}");
            });

            await t.Run("security integration: when the new connection fails the previous settings come back at once", async () =>
            {
                var failing = new SshdService(session) { VerifyOverride = _ => Task.FromResult(false) }; await failing.ProbeAsync();
                var current = await failing.ReadAsync();
                var result = await failing.ApplyAsync(current, new Dictionary<string, string> { ["MaxAuthTries"] = "2" });
                var after = await sshd.ReadAsync();
                TestSuite.True(result.Outcome == ApplyOutcome.RolledBack && after.Get("maxauthtries") == "4", $"{result.Outcome} tries={after.Get("maxauthtries")}");
            });

            await t.Run("security integration: settings sshd refuses are restored by the script itself", async () =>
            {
                var id = "sshxbad01";
                var script = Sshd.SafeApply(id, "MaxAuthTries many\n", first.IncludesDropIns, 30);
                await Sh($"printf %s {PosixPath.Quote(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(script)))} | base64 -d > /tmp/sshx-bad.sh");
                var output = await Sh($"{sshd.Prefix}sh /tmp/sshx-bad.sh 2>&1; rm -f /tmp/sshx-bad.sh");
                var after = await sshd.ReadAsync();
                TestSuite.True(output.Contains("#invalid", StringComparison.Ordinal) && !output.Contains("#applied", StringComparison.Ordinal) && after.Get("maxauthtries") == "4" && (await Sh($"[ -d {Sshd.Directory(id)} ] && echo present || echo gone")).Trim() == "gone", output);
            });
        }
        finally
        {
            try { foreach (var k in (await keys.ListAsync()).Where(k => k.Fingerprint == generated.Fingerprint)) await keys.RemoveAsync(k); } catch (Exception) { }
            await Sh($"sudo -n rm -f '{Sshd.DropIn}'; sudo -n sed -i '/^# SSH Explorer — begin$/,/^# SSH Explorer — end$/d' /etc/ssh/sshd_config; sudo -n rm -rf /var/tmp/sshx-sshd-*; true");
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
#endif
