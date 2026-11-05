#if CORE_TESTS
using SSHExplorer.Core;

/// <summary>Real folder comparison over SFTP (agent sign-in). Arguments: host port user.</summary>
internal static class FolderCompareIntegration
{
    public static async Task RunAsync(TestSuite t, string host, int port, string user)
    {
        var store = new AppStore(Path.Combine(Path.GetTempPath(), "sshx-cmp-store-" + Guid.NewGuid().ToString("N")));
        await using var session = new ServerSession(new ServerProfile { Name = "Сравнение · стенд", Host = host, Port = port, Username = user, Authentication = AuthenticationKind.Agent }, store);
        await session.ConnectAsync(new SessionCredentials(""), _ => Task.FromResult(TrustDecision.Session));
        var local = Path.Combine(Path.GetTempPath(), "sshx-cmp-" + Guid.NewGuid().ToString("N")); var remote = "/tmp/sshx-cmp-" + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(Path.Combine(local, "docs"));
        File.WriteAllText(Path.Combine(local, "equal.txt"), "same"); File.WriteAllText(Path.Combine(local, "local-only.txt"), "L");
        File.WriteAllText(Path.Combine(local, "docs", "size.txt"), "short"); File.WriteAllText(Path.Combine(local, "twin.txt"), "AAAA");
        // Same size and the same time on both sides, different bytes: only a content comparison sees it.
        var stamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        foreach (var f in Directory.EnumerateFiles(local, "*", SearchOption.AllDirectories)) File.SetLastWriteTimeUtc(f, stamp);
        var touch = "-d '2026-01-02 03:04:05 UTC'";
        await session.ExecuteAsync($"mkdir -p {remote}/docs && cd {remote} && printf same > equal.txt && printf R > remote-only.txt && printf 'longer text' > docs/size.txt && printf BBBB > twin.txt && ln -s equal.txt link && mkfifo pipe && touch {touch} equal.txt remote-only.txt docs/size.txt twin.txt");
        try
        {
            var comparer = new FolderComparer(session);
            var quick = (await comparer.CompareAsync(local, remote, byContent: false)).Results.ToDictionary(r => r.RelativePath);
            await t.Run("compare integration: size and sides are found; same size and time look equal without content", () =>
                TestSuite.True(quick["equal.txt"].Kind == CompareKind.Same && quick["local-only.txt"].Kind == CompareKind.OnlyLocal && quick["remote-only.txt"].Kind == CompareKind.OnlyRemote
                    && quick["docs/size.txt"].Kind == CompareKind.Different && quick["twin.txt"].Kind == CompareKind.Same, string.Join(", ", quick.Values.Select(r => r.RelativePath + "=" + r.Kind))));
            var report = await comparer.CompareAsync(local, remote, byContent: true);
            var deep = report.Results.ToDictionary(r => r.RelativePath);
            await t.Run("compare integration: SHA-256 finds equal-size, equal-time files with different bytes; links and FIFOs are skipped", () =>
                TestSuite.True(deep["twin.txt"] is { Kind: CompareKind.Different, Reason: "Содержимое различается (SHA-256)" } && deep["equal.txt"] is { Kind: CompareKind.Same, Reason: "Содержимое совпадает (SHA-256)" }
                    && !deep.ContainsKey("link") && !deep.ContainsKey("pipe") && report.Skipped.Count(s => s.StartsWith("Сервер:", StringComparison.Ordinal)) == 2, string.Join(" | ", report.Skipped)));
        }
        finally { await session.ExecuteAsync("rm -rf " + PosixPath.Quote(remote)); Directory.Delete(local, true); }
        await t.Run("rename integration: a swap and a chain over real SFTP keep every file's content and overwrite nothing", async () =>
        {
            var dir = "/tmp/sshx-rename-" + Guid.NewGuid().ToString("N")[..8];
            await session.ExecuteAsync($"mkdir -p {dir} && cd {dir} && printf A > a && printf B > b && printf 1 > 1 && printf 2 > 2 && printf 3 > 3");
            var swap = new[] { new RenamePlanItem(dir, "a", "b", RenameStatus.Ok, ""), new RenamePlanItem(dir, "b", "a", RenameStatus.Ok, "") };
            var chain = BatchRename.Plan([(dir, "1", false), (dir, "2", false), (dir, "3", false)], new BatchRenameOptions { Pattern = "{n}", Start = 2 }, _ => new HashSet<string> { "1", "2", "3", "a", "b" });
            foreach (var (from, to) in BatchRename.Operations([.. swap, .. chain], _ => ".sshexplorer-rename-" + Guid.NewGuid().ToString("N")[..12])) await session.Files.RenameAsync(from, to);
            var listing = (await session.ExecuteAsync($"cd {dir} && for f in $(ls -A | sort); do printf '%s=%s ' \"$f\" \"$(cat \"$f\")\"; done")).Output.Trim();
            await session.ExecuteAsync("rm -rf " + dir);
            TestSuite.Equal("2=1 3=2 4=3 a=B b=A", listing);
        });
        await t.Run("ownership integration: getent names the account's UID/GID both ways; a recursive count sees the whole tree", async () =>
        {
            var ids = (await session.ExecuteAsync("id -u; id -g; id -un; id -gn")).Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var (name, group) = Ownership.ParseNames((await session.ExecuteAsync(Ownership.NamesCommand(int.Parse(ids[0]), int.Parse(ids[1])))).Output);
            var back = (await session.ExecuteAsync(Ownership.IdCommand(ids[2], group: false)!)).Output.Trim();
            var tree = "/tmp/sshx-own-" + Guid.NewGuid().ToString("N")[..8];
            await session.ExecuteAsync($"mkdir -p {tree}/a/b && touch {tree}/a/b/f {tree}/g");
            var count = (await session.ExecuteAsync(Ownership.CountCommand(tree))).Output.Trim();
            await session.ExecuteAsync("rm -rf " + tree);
            TestSuite.True(name == ids[2] && group == ids[3] && back == ids[0] && count == "5", $"{name}/{group} back={back} count={count}");
        });
    }
}
#endif
