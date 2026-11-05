#if CORE_TESTS
using System.Collections.Concurrent;
using SSHExplorer.Core;

/// <summary>
/// 0.3 C1–C4 on a real server (agent sign-in): archives with every available format, grep, tail -F streaming that ends on
/// the server when cancelled, and systemd with a fixture unit «sshx-fixture.service» installed by the workflow.
/// Arguments: host port user.
/// </summary>
internal static class GroupCIntegration
{
    public static async Task RunAsync(TestSuite t, string host, int port, string user)
    {
        var store = new AppStore(Path.Combine(Path.GetTempPath(), "sshx-c-store-" + Guid.NewGuid().ToString("N")));
        await using var session = new ServerSession(new ServerProfile { Name = "Группа C · стенд", Host = host, Port = port, Username = user, Authentication = AuthenticationKind.Agent }, store);
        await session.ConnectAsync(new SessionCredentials(""), _ => Task.FromResult(TrustDecision.Session));
        var root = "/tmp/sshx-c-" + Guid.NewGuid().ToString("N")[..8];
        async Task<CommandResult> Sh(string command) => await session.ExecuteLongAsync(command, TimeSpan.FromMinutes(2));
        await Sh($"mkdir -p {root}/src/sub && cd {root}/src && printf 'alpha\\n' > -dash.txt && printf 'beta\\n' > \"it's here.txt\" && printf 'gamma\\n' > sub/deep.txt && ln -s deep.txt sub/link");
        try
        {
            var tools = Archives.ParseTools((await session.ExecuteAsync(Archives.ToolsCommand)).Output);
            foreach (var format in Enum.GetValues<ArchiveFormat>())
            {
                var missing = Archives.Missing(format, true, tools) ?? Archives.Missing(format, false, tools);
                if (missing is not null) { Console.WriteLine($"SKIP archives {format}: no {missing} on the runner"); continue; }
                await t.Run($"archives integration ({format}): create, refuse to overwrite, list, extract into a new folder with the same content", async () =>
                {
                    var name = "pack" + Archives.Extension(format); var dir = root + "/src";
                    var names = new[] { "-dash.txt", "it's here.txt", "sub" };
                    var created = await Sh(Archives.CreateCommand(dir, names, name, format, ".sshexplorer-archive-t.part"));
                    TestSuite.True(created.ExitStatus == 0, "create: " + created.Error);
                    var size = (await Sh($"stat -c %s {PosixPath.Quote(dir + "/" + name)}")).Output.Trim();
                    var again = await Sh(Archives.CreateCommand(dir, ["sub"], name, format, ".sshexplorer-archive-t.part"));
                    var sizeAfter = (await Sh($"stat -c %s {PosixPath.Quote(dir + "/" + name)}")).Output.Trim();
                    var leftovers = (await Sh($"ls -A {PosixPath.Quote(dir)} | grep -c sshexplorer-archive || true")).Output.Trim();
                    TestSuite.True(again.ExitStatus == 17 && size == sizeAfter && leftovers == "0", $"second create exit={again.ExitStatus} size {size}->{sizeAfter} leftovers={leftovers}");
                    var listing = Archives.ParseListing((await Sh(Archives.ListCommand(dir + "/" + name, format))).Output);
                    TestSuite.True(listing.Entries.Any(e => e.EndsWith("-dash.txt", StringComparison.Ordinal)) && listing.Entries.Any(e => e.EndsWith("sub/deep.txt", StringComparison.Ordinal)) && listing.Unsafe.Count == 0, string.Join(",", listing.Entries));
                    var target = root + "/out-" + format;
                    var extracted = await Sh(Archives.ExtractCommand(dir + "/" + name, format, target, ExtractMode.NewFolder));
                    TestSuite.True(extracted.ExitStatus == 0, "extract: " + extracted.Error + extracted.Output);
                    var content = (await Sh($"cd {PosixPath.Quote(target)} && cat ./-dash.txt \"it's here.txt\" sub/deep.txt && readlink sub/link")).Output.Replace("\n", "|");
                    TestSuite.Equal("alpha|beta|gamma|deep.txt|", content);
                    var twice = await Sh(Archives.ExtractCommand(dir + "/" + name, format, target, ExtractMode.NewFolder));
                    TestSuite.True(twice.ExitStatus == 17, "extracting into an existing folder is refused: " + twice.ExitStatus);
                    // Keep-existing mode: a changed file stays as it is, a missing one comes back.
                    await Sh($"cd {PosixPath.Quote(target)} && printf 'mine\\n' > ./-dash.txt && rm sub/deep.txt");
                    var keep = await Sh(Archives.ExtractCommand(dir + "/" + name, format, target, ExtractMode.HereKeepExisting));
                    var kept = (await Sh($"cd {PosixPath.Quote(target)} && cat ./-dash.txt sub/deep.txt")).Output.Replace("\n", "|");
                    TestSuite.True(keep.ExitStatus == 0 && kept == "mine|gamma|", $"keep exit={keep.ExitStatus} {keep.Error} content={kept}");
                    await Sh($"rm -f {PosixPath.Quote(dir + "/" + name)}");
                });
            }
            await t.Run("archives integration: entries with .. are reported as unsafe before extracting", async () =>
            {
                await Sh($"cd {root}/src && printf x > {root}/outside.txt && tar -P -cf {root}/evil.tar ../outside.txt 2>/dev/null");
                var listing = Archives.ParseListing((await Sh(Archives.ListCommand(root + "/evil.tar", ArchiveFormat.Tar))).Output);
                TestSuite.True(listing.Unsafe.Contains("../outside.txt"), string.Join(",", listing.Entries));
            });

            await t.Run("content search integration: fixed and regex, case, masks, binary and .git skipped, names with colons", async () =>
            {
                var g = root + "/grep";
                await Sh($"mkdir -p {g}/.git {g}/conf && cd {g} && printf 'listen 80;\\nserver_name Example.org;\\n' > conf/site.conf && printf 'Listen 8080\\n' > 'a:b.yml' " +
                         $"&& printf 'listen in git\\n' > .git/config && printf 'listen\\0binary' > blob.bin && printf 'a+b\\n' > plus.txt");
                async Task<ContentSearchResult> Find(ContentSearchOptions o) { var r = await Sh(ContentSearch.Command(g, o)); return ContentSearch.Parse(r.Output, r.Error); }
                var any = await Find(new ContentSearchOptions { Pattern = "listen" });
                var paths = any.Matches.Select(m => m.Path + ":" + m.Line).Order(StringComparer.Ordinal).ToArray();
                TestSuite.True(paths.SequenceEqual(["a:b.yml:1", "conf/site.conf:1"]), "ignore-case, binary and .git skipped: " + string.Join(", ", paths));
                var exact = await Find(new ContentSearchOptions { Pattern = "Listen", IgnoreCase = false, Include = "*.yml" });
                TestSuite.True(exact.Matches.Count == 1 && exact.Matches[0] is { Path: "a:b.yml", Text: "Listen 8080" }, string.Join(", ", exact.Matches));
                var fixedPlus = await Find(new ContentSearchOptions { Pattern = "a+b" });
                var regexPlus = await Find(new ContentSearchOptions { Pattern = "a+b", Regex = true });
                TestSuite.True(fixedPlus.Matches.Any(m => m.Path == "plus.txt") && !regexPlus.Matches.Any(m => m.Path == "plus.txt"), "-F takes + literally, -E as a quantifier");
                var withGit = await Find(new ContentSearchOptions { Pattern = "in git", SkipCommonFolders = false });
                TestSuite.True(withGit.Matches.Any(m => m.Path == ".git/config"), "skipping .git can be turned off");
                var bad = await Find(new ContentSearchOptions { Pattern = "(", Regex = true });
                TestSuite.True(bad.Matches.Count == 0 && bad.Error is not null, "a bad expression is reported: " + bad.Error);
            });

            await t.Run("log follow integration: tail -F streams appended lines, survives rotation, and ends on the server when cancelled", async () =>
            {
                var log = root + "/app.log";
                await Sh($"printf 'one\\ntwo\\n' > {log}");
                var lines = new ConcurrentQueue<string>(); using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var stream = session.StreamLinesAsync(LogFollow.Wrap(Systemd.TailCommand(log, 10, true)), lines.Enqueue, cts.Token);
                async Task Wait(string text) { for (int i = 0; i < 100 && !lines.Contains(text); i++) await Task.Delay(100); }
                await Wait("two");
                await Sh($"printf 'three\\n' >> {log}"); await Wait("three");
                await Sh($"mv {log} {log}.1 && printf 'after rotation\\n' > {log}"); await Wait("after rotation");
                var running = (await Sh($"pgrep -f -- {PosixPath.Quote("[t]ail -n 10 -F -- " + log)} | wc -l")).Output.Trim();
                cts.Cancel();
                try { await stream; } catch (OperationCanceledException) { }
                string left = "?";
                for (int i = 0; i < 30; i++) { left = (await Sh($"pgrep -f -- {PosixPath.Quote("[t]ail -n 10 -F -- " + log)} | wc -l")).Output.Trim(); if (left == "0") break; await Task.Delay(200); }
                TestSuite.True(lines.Contains("one") && lines.Contains("three") && lines.Contains("after rotation") && running != "0" && left == "0", $"lines=[{string.Join("|", lines)}] running={running} left={left}");
            });
            await t.Run("log follow integration: a one-shot read ends by itself with the command's status", async () =>
            {
                var lines = new ConcurrentQueue<string>();
                var status = await session.StreamLinesAsync(Systemd.TailCommand(root + "/missing.log", 5, false), lines.Enqueue);
                TestSuite.True(status is not 0 && lines.Any(l => l.Contains("missing.log", StringComparison.Ordinal)), $"status={status} [{string.Join("|", lines)}]");
            });

            var systemd = new SystemdService(session);
            var snapshot = await systemd.ListAsync();
            await t.Run("systemd integration: services are listed with state; passwordless sudo is detected", () =>
                TestSuite.True(snapshot.Available && snapshot.Units.Count > 10 && snapshot.Units.Any(u => u.Name == "systemd-journald.service" && u.IsRunning)
                    && snapshot.Units.Any(u => u.Name == "sshx-fixture.service" && !u.IsRunning) && (snapshot.IsRoot || snapshot.Sudo), $"available={snapshot.Available} units={snapshot.Units.Count} sudo={snapshot.Sudo} error={snapshot.Error}"));
            await t.Run("systemd integration: start, journal, restart, enable/disable and stop the fixture unit", async () =>
            {
                const string unit = "sshx-fixture.service";
                async Task<ServiceUnit> State() => (await systemd.ListAsync()).Units.Single(u => u.Name == unit);
                await systemd.ActAsync(unit, ServiceAction.Start);
                var started = await State();
                var details = await systemd.ShowAsync(unit);
                var journal = new ConcurrentQueue<string>();
                // journald writes asynchronously: read again for a few seconds until the service's own line is there.
                for (int i = 0; i < 20 && !journal.Any(l => l.Contains("sshx fixture says hello", StringComparison.Ordinal)); i++)
                {
                    journal.Clear(); await systemd.JournalAsync(unit, 50, false, journal.Enqueue, CancellationToken.None);
                    if (!journal.Any(l => l.Contains("sshx fixture says hello", StringComparison.Ordinal))) await Task.Delay(300);
                }
                await systemd.ActAsync(unit, ServiceAction.Enable); var enabled = await State();
                await systemd.ActAsync(unit, ServiceAction.Disable); var disabled = await State();
                await systemd.ActAsync(unit, ServiceAction.Stop); var stopped = await State();
                TestSuite.True(started.IsRunning && details.TryGetValue("MainPID", out var pid) && pid != "0" && journal.Any(l => l.Contains("sshx fixture says hello", StringComparison.Ordinal))
                    && enabled.UnitFileState == "enabled" && disabled.UnitFileState == "disabled" && !stopped.IsRunning,
                    $"started={started.Active}/{started.Sub} pid={details.GetValueOrDefault("MainPID")} journal={journal.Count} enabled={enabled.UnitFileState} disabled={disabled.UnitFileState} stopped={stopped.Active}");
            });
            await t.Run("systemd integration: a followed journal shows new entries and ends with the viewer", async () =>
            {
                const string unit = "sshx-fixture.service";
                var lines = new ConcurrentQueue<string>(); using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var follow = systemd.JournalAsync(unit, 50, true, lines.Enqueue, cts.Token);
                await Task.Delay(1500);
                await systemd.ActAsync(unit, ServiceAction.Start);
                for (int i = 0; i < 100 && lines.Count(l => l.Contains("sshx fixture says hello", StringComparison.Ordinal)) < 2; i++) await Task.Delay(100);
                cts.Cancel(); try { await follow; } catch (OperationCanceledException) { }
                await systemd.ActAsync(unit, ServiceAction.Stop);
                string left = "?";
                for (int i = 0; i < 30; i++) { left = (await Sh("pgrep -f -- '[j]ournalctl --no-pager -o short-iso -n 50 -f' | wc -l")).Output.Trim(); if (left == "0") break; await Task.Delay(200); }
                TestSuite.True(lines.Count(l => l.Contains("sshx fixture says hello", StringComparison.Ordinal)) >= 2 && left == "0", $"lines={lines.Count} left={left}");
            });
            await t.Run("systemd integration: an unknown unit fails with systemctl's own message", async () =>
            {
                var error = "";
                try { await systemd.ActAsync("sshx-no-such-unit.service", ServiceAction.Start); } catch (IOException ex) { error = ex.Message; }
                TestSuite.True(error.Contains("not found", StringComparison.OrdinalIgnoreCase) || error.Contains("not loaded", StringComparison.OrdinalIgnoreCase), error);
            });
        }
        finally { await Sh("rm -rf " + PosixPath.Quote(root)); }
    }
}
#endif
