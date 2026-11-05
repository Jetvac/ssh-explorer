#if CORE_TESTS
using SSHExplorer.Core;

internal static class BatchRenameTests
{
    static IReadOnlySet<string> Folder(params string[] names) => names.ToHashSet(StringComparer.Ordinal);

    public static async Task RunAsync(TestSuite t)
    {
        await t.Run("batch rename: pattern tokens, padded counter, plain and regex replace, case", () =>
        {
            var items = new[] { ("/p", "IMG_001.JPG", false), ("/p", "IMG_002.JPG", false), ("/p", "notes", true) };
            var plan = BatchRename.Plan(items, new BatchRenameOptions { Pattern = "trip-{n}{ext}", Start = 7, Step = 2, Digits = 3, Case = RenameCase.Lower }, _ => Folder("IMG_001.JPG", "IMG_002.JPG", "notes"));
            TestSuite.True(plan.Select(p => p.NewName).SequenceEqual(["trip-007.jpg", "trip-009.jpg", "trip-011"]), string.Join(",", plan.Select(p => p.NewName)));
            var regex = BatchRename.Plan(items[..2], new BatchRenameOptions { Find = @"IMG_(\d+)", Replace = "photo-$1", UseRegex = true }, _ => Folder("IMG_001.JPG", "IMG_002.JPG"));
            TestSuite.True(regex.Select(p => p.NewName).SequenceEqual(["photo-001.JPG", "photo-002.JPG"]) && regex.All(p => p.Status == RenameStatus.Ok), string.Join(",", regex.Select(p => p.NewName)));
            var bad = BatchRename.Plan(items[..1], new BatchRenameOptions { Find = "(", UseRegex = true }, _ => Folder());
            TestSuite.True(bad.Single() is { Status: RenameStatus.Invalid } b && b.Message.StartsWith("Регулярное", StringComparison.Ordinal), bad.Single().Message);
        });
        await t.Run("batch rename: invalid names, duplicates in the batch and collisions with other objects are refused", () =>
        {
            var plan = BatchRename.Plan([("/p", "a.txt", false), ("/p", "b.txt", false), ("/p", "c.txt", false), ("/p", "d.txt", false)],
                new BatchRenameOptions { Pattern = "{name}", Find = "a", Replace = "x/y" }, _ => Folder("a.txt", "b.txt", "c.txt", "d.txt", "c", "keep"))
                .ToDictionary(p => p.OldName);
            TestSuite.True(plan["a.txt"].Status == RenameStatus.Invalid && plan["c.txt"].Status == RenameStatus.Exists && plan["b.txt"].Status == RenameStatus.Ok, string.Join(",", plan.Values.Select(p => p.OldName + "=" + p.Status)));
            var dup = BatchRename.Plan([("/p", "a.txt", false), ("/p", "b.txt", false)], new BatchRenameOptions { Pattern = "same{ext}" }, _ => Folder("a.txt", "b.txt"));
            TestSuite.True(dup.All(p => p.Status == RenameStatus.Duplicate), "duplicates");
            TestSuite.True(BatchRename.Plan([("/p", "a", false)], new BatchRenameOptions { Pattern = ".." }, _ => Folder("a")).Single().Status == RenameStatus.Invalid, "..");
        });
        await t.Run("batch rename: swaps and chains go through temporary names and never overwrite", () =>
        {
            var swap = BatchRename.Plan([("/p", "a", false), ("/p", "b", false)], new BatchRenameOptions { Find = "", Pattern = "{name}" }, _ => Folder("a", "b"));
            var swapPlan = new[] { swap[0] with { NewName = "b", Status = RenameStatus.Ok }, swap[1] with { NewName = "a", Status = RenameStatus.Ok } };
            int n = 0; var ops = BatchRename.Operations(swapPlan, _ => ".tmp" + n++);
            var files = new Dictionary<string, string> { ["/p/a"] = "A", ["/p/b"] = "B" };
            foreach (var (from, to) in ops) { TestSuite.True(!files.ContainsKey(to), "would overwrite " + to); files[to] = files[from]; files.Remove(from); }
            TestSuite.True(files["/p/a"] == "B" && files["/p/b"] == "A" && files.Count == 2, string.Join(",", ops));
            var chain = BatchRename.Plan([("/p", "1", false), ("/p", "2", false), ("/p", "3", false)], new BatchRenameOptions { Pattern = "{n}", Start = 2 }, _ => Folder("1", "2", "3"));
            TestSuite.True(chain.Select(p => p.NewName + ":" + p.Status).SequenceEqual(["2:Ok", "3:Ok", "4:Ok"]), string.Join(",", chain.Select(p => p.NewName + ":" + p.Status)));
            var state = new Dictionary<string, string> { ["/p/1"] = "one", ["/p/2"] = "two", ["/p/3"] = "three" };
            foreach (var (from, to) in BatchRename.Operations(chain, _ => ".tmp" + n++)) { TestSuite.True(!state.ContainsKey(to), "would overwrite " + to); state[to] = state[from]; state.Remove(from); }
            TestSuite.True(state["/p/2"] == "one" && state["/p/3"] == "two" && state["/p/4"] == "three" && state.Count == 3, string.Join(",", state));
        });
        await Task.CompletedTask;
    }
}
#endif
