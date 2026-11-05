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

            await t.Run("group run integration: one script on several servers over new connections; an unconfirmed host key and a missing password are reported", async () =>
            {
                // The fixture's key is trusted permanently once, as a user would in a tab.
                var trusted = new ServerProfile { Name = "Группа · 1", Host = host, Port = port, Username = user, Authentication = AuthenticationKind.Agent, Group = "Группа" };
                await using (var once = new ServerSession(trusted, store)) await once.ConnectAsync(new SessionCredentials(""), _ => Task.FromResult(TrustDecision.Permanent));
                var second = new ServerProfile { Name = "Группа · 2", Host = host, Port = port, Username = user, Authentication = AuthenticationKind.Agent, Group = "Группа" };
                var unknownKey = new ServerProfile { Name = "Группа · новый ключ", Host = host, Port = port, Username = user, Authentication = AuthenticationKind.Agent, Group = "Группа" };
                var password = new ServerProfile { Name = "Группа · пароль", Host = host, Port = port, Username = user, Authentication = AuthenticationKind.Password, Group = "Группа" };
                var results = new System.Collections.Concurrent.ConcurrentDictionary<Guid, GroupRunResult>();
                await new GroupRunner(store) { Parallelism = 2 }.RunAsync([trusted, second, password], new SavedScript { Name = "проверка", Body = "echo group-ok\nid -un" }, r => results[r.ProfileId] = r);
                // Another computer's store: the same server's key is not trusted there yet.
                await new GroupRunner(new AppStore(Path.Combine(dir, "fresh-store"))).RunAsync([unknownKey], new SavedScript { Name = "x", Body = "true" }, r => results[r.ProfileId] = r);
                TestSuite.True(results[trusted.Id] is { State: GroupRunState.Succeeded, ExitCode: 0 } ok && ok.Output.Contains("group-ok", StringComparison.Ordinal) && ok.Output.Contains(user, StringComparison.Ordinal)
                    && results[second.Id].State == GroupRunState.Succeeded && results[unknownKey.Id] is { State: GroupRunState.Failed } bad && bad.Problem!.Contains("не подтверждён", StringComparison.Ordinal)
                    && results[password.Id].State == GroupRunState.Skipped,
                    string.Join(" | ", results.Values.Select(r => $"{r.State} {r.ExitCode} {r.Problem}")));
                var failing = new System.Collections.Concurrent.ConcurrentDictionary<Guid, GroupRunResult>();
                await new GroupRunner(store).RunAsync([trusted], new SavedScript { Name = "сбой", Body = "echo before\nfalse\necho after" }, r => failing[r.ProfileId] = r);
                TestSuite.True(failing[trusted.Id] is { State: GroupRunState.Failed, ExitCode: 1, Retryable: true } f && f.Output.Contains("before", StringComparison.Ordinal) && !f.Output.Contains("after", StringComparison.Ordinal), failing[trusted.Id].Output);
            });

            await t.Run("cron integration: a job is added, switched off and on, and a crontab changed meanwhile is not overwritten", async () =>
            {
                await Sh("crontab -r 2>/dev/null; true");
                var cron = new CronService(session);
                var empty = await cron.ReadAsync();
                var saved = await cron.SaveAsync(empty, Cron.Edit(empty, -1, Cron.JobLine("*/10 * * * *", "echo sshx-cron >/dev/null"), "проверка"));
                var one = await cron.ReadAsync();
                var job = one.Jobs.Single();
                var off = await cron.SaveAsync(one, Cron.Edit(one, job.Index, Cron.Toggle(job)));
                var two = await cron.ReadAsync();
                await Sh("(crontab -l; echo '0 1 * * * echo someone-else') | crontab -");
                var stale = await cron.SaveAsync(two, Cron.Edit(two, -1, Cron.JobLine("@daily", "echo lost")));
                var three = await cron.ReadAsync();
                var backups = (await Sh("ls ~/.cache/sshexplorer/crontab-user-*.bak 2>/dev/null | wc -l")).Trim();
                TestSuite.True(empty.Problem is null && empty.Lines.Count == 0 && saved == CronSaveOutcome.Saved && job is { Schedule: "*/10 * * * *", Enabled: true } && one.Lines.Any(l => l.Raw == "# проверка")
                    && off == CronSaveOutcome.Saved && two.Jobs.Single().Enabled == false && stale == CronSaveOutcome.Changed && three.Jobs.Count() == 2 && !three.Text.Contains("echo lost", StringComparison.Ordinal) && int.Parse(backups) >= 2,
                    $"saved={saved} off={off} stale={stale} jobs={three.Jobs.Count()} backups={backups} problem={empty.Problem}");
                await Sh("crontab -r 2>/dev/null; rm -f ~/.cache/sshexplorer/crontab-*.bak; true");
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
