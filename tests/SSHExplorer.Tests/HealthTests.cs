#if CORE_TESTS
using SSHExplorer.Core;

/// <summary>Alerts: full disks and failed services from df and systemctl; only new issues count, gone ones resolve.</summary>
internal static class HealthTests
{
    public static async Task RunAsync(TestSuite t)
    {
        await t.Run("health: nearly full disks and failed services are found; the threshold is respected", () =>
        {
            var output = "/dev/sda1 41152832 39100000 2052832 96% /\n/dev/sdb1 1000000 500000 500000 50% /data\n/dev/sdc1 1000000 910000 90000 91% /mnt/my disk\n#failed\nnginx.service loaded failed failed A high performance web server\nbackup.timer loaded failed failed Backup\n#end\n";
            var issues = ServerHealth.Parse(output, 90);
            TestSuite.True(issues.Count == 4 && issues[0] is { Key: "disk:/" } root && root.Text.StartsWith("Диск / заполнен на 96% (свободно 2,0 ГБ)", StringComparison.Ordinal) | root.Text.StartsWith("Диск / заполнен на 96% (свободно 2.0 ГБ)", StringComparison.Ordinal)
                && issues.Any(i => i.Key == "disk:/mnt/my disk") && issues.Any(i => i.Key == "unit:nginx.service") && issues.Any(i => i.Key == "unit:backup.timer"), string.Join(" | ", issues.Select(i => i.Key + " " + i.Text)));
            TestSuite.True(ServerHealth.Parse(output, 97).Count(i => i.Key.StartsWith("disk:", StringComparison.Ordinal)) == 0 && ServerHealth.Parse("#failed\n#end", 90).Count == 0, "threshold and nothing");
        });
        await t.Run("health: only new issues are reported; issues that went away are resolved", () =>
        {
            var a = new HealthIssue("disk:/", "x"); var b = new HealthIssue("unit:nginx.service", "y");
            var (n1, r1) = ServerHealth.Diff(new HashSet<string>(), [a, b]);
            var (n2, r2) = ServerHealth.Diff(new HashSet<string> { "disk:/", "unit:nginx.service" }, [a]);
            TestSuite.True(n1.Count == 2 && r1.Count == 0 && n2.Count == 0 && r2.SequenceEqual(["unit:nginx.service"]), "diff");
        });
        await Task.CompletedTask;
    }
}
#endif
