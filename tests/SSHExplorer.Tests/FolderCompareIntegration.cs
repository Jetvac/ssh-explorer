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
    }
}
#endif
