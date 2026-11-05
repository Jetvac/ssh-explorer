#if CORE_TESTS
using SSHExplorer.Core;

/// <summary>Pending updates: apt and dnf output, security first, list age, reboot flag, the commands offered for the terminal.</summary>
internal static class PackagesTests
{
    public static async Task RunAsync(TestSuite t)
    {
        await t.Run("packages: apt upgradable list with security updates first, list age and a pending reboot", () =>
        {
            var r = Packages.Parse("#mgr apt\n#age 180000\ncurl/jammy-updates 7.81.0-1ubuntu1.16 amd64 [upgradable from: 7.81.0-1ubuntu1.15]\nopenssl/jammy-updates,jammy-security 3.0.2-0ubuntu1.15 amd64 [upgradable from: 3.0.2-0ubuntu1.14]\n#reboot\n#end\n");
            TestSuite.True(r.Problem is null && r.Manager == "apt" && r.Updates.Count == 2 && r.Updates[0] is { Name: "openssl", Version: "3.0.2-0ubuntu1.15", Current: "3.0.2-0ubuntu1.14", Security: true } && !r.Updates[1].Security
                && r.SecurityCount == 1 && r.RebootRequired && r.ListsAgeHours is > 49 and < 51 && r.Summary == "Обновлений: 2, безопасности: 1 · требуется перезагрузка", r.Summary);
            TestSuite.True(Packages.UpgradeCommand(r, true) == "sudo apt-get update && sudo apt-get install --only-upgrade 'openssl'" && Packages.UpgradeCommand(r, false) == "sudo apt-get update && sudo apt-get upgrade" && Packages.RefreshCommand(r) == "sudo apt-get update", Packages.UpgradeCommand(r, true));
        });
        await t.Run("packages: dnf check-update with security advisories; no manager and no updates are said plainly", () =>
        {
            var r = Packages.Parse("#mgr dnf\n\nopenssl.x86_64     1:3.1.4-2.fc39     updates\nvim-common.x86_64  2:9.1.0-1.fc39     updates\nObsoleting Packages\n#security\nFEDORA-2026-1a2b Important/Sec. openssl-1:3.1.4-2.fc39.x86_64\n#end\n");
            TestSuite.True(r.Updates.Count == 2 && r.Updates.Single(u => u.Name == "openssl").Security && !r.Updates.Single(u => u.Name == "vim-common").Security && Packages.UpgradeCommand(r, true) == "sudo dnf upgrade --security", string.Join(",", r.Updates.Select(u => u.Name + ":" + u.Security)));
            TestSuite.True(Packages.Parse("#mgr none\n#end").Problem!.Contains("не найден", StringComparison.Ordinal) && Packages.Parse("#mgr apt\n#age 60\n#end").Summary == "Обновлений нет", "none and empty");
        });
        await Task.CompletedTask;
    }
}
#endif
