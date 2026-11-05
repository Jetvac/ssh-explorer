#if CORE_TESTS
using SSHExplorer.Core;

internal static class FolderCompareTests
{
    static readonly DateTime T = new(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc);
    static CompareEntry F(string path, long size, double seconds = 0) => new(path, false, size, T.AddSeconds(seconds));
    static CompareEntry D(string path) => new(path, true, 0, T);

    public static async Task RunAsync(TestSuite t)
    {
        await t.Run("compare: only-local, only-remote, size, newer side, type conflict and 2 s tolerance", () =>
        {
            var results = FolderCompare.Compare(
                [F("a.txt", 10), F("b.txt", 10), F("c.txt", 10, 60), F("same.txt", 5, 1.5), D("dir"), F("dir/x", 1), F("clash", 1), F("local-only", 3)],
                [F("a.txt", 11), F("b.txt", 10), F("c.txt", 10), F("same.txt", 5), D("dir"), F("dir/x", 1), D("clash"), F("remote-only", 4)]).ToDictionary(r => r.RelativePath);
            TestSuite.True(results["a.txt"] is { Kind: CompareKind.Different, Reason: "Размер различается" }, results["a.txt"].Reason);
            TestSuite.True(results["c.txt"] is { Kind: CompareKind.Different, Newer: "local" } && results["c.txt"].Reason.Contains("компьютере", StringComparison.Ordinal), results["c.txt"].Reason);
            TestSuite.True(results["same.txt"].Kind == CompareKind.Same && results["b.txt"].Kind == CompareKind.Same && results["dir"].Kind == CompareKind.Same, "tolerance and equal");
            TestSuite.True(results["clash"].Kind == CompareKind.TypeConflict && results["local-only"].Kind == CompareKind.OnlyLocal && results["remote-only"].Kind == CompareKind.OnlyRemote, "sides");
        });
        await t.Run("compare: content hashes override time; escaped or missing hashes never count as equal", () =>
        {
            var results = FolderCompare.Compare([F("same-time.bin", 4), F("changed.bin", 4, 100)], [F("same-time.bin", 4), F("changed.bin", 4)],
                sameContent: new HashSet<string> { "changed.bin" }, differentContent: new HashSet<string> { "same-time.bin" }).ToDictionary(r => r.RelativePath);
            TestSuite.True(results["same-time.bin"] is { Kind: CompareKind.Different } && results["changed.bin"].Kind == CompareKind.Same, "hash decides");
            var hashes = FolderCompare.ParseRemoteHashes(string.Join('\n', new string('a', 64) + "  dir/file name.txt", "\\" + new string('b', 64) + "  odd\\nname", new string('c', 64) + " *binary.bin", "sha256sum: missing: No such file"));
            TestSuite.True(hashes.Count == 2 && hashes["dir/file name.txt"] == new string('a', 64) && hashes.ContainsKey("binary.bin"), string.Join(",", hashes.Keys));
        });
        await t.Run("compare: a local tree lists files and folders with '/' paths in order", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "sshx-compare-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "sub", "deep")); File.WriteAllText(Path.Combine(root, "b.txt"), "b"); File.WriteAllText(Path.Combine(root, "sub", "deep", "a.txt"), "aa");
            try
            {
                var (entries, skipped, truncated) = FolderCompare.ListLocal(root);
                TestSuite.True(entries.Select(e => e.RelativePath + (e.IsDirectory ? "/" : ":" + e.Length)).SequenceEqual(["b.txt:1", "sub/", "sub/deep/", "sub/deep/a.txt:2"]) && skipped.Count == 0 && !truncated, string.Join(" ", entries.Select(e => e.RelativePath)));
            }
            finally { Directory.Delete(root, true); }
        });
        await Task.CompletedTask;
    }
}
#endif
