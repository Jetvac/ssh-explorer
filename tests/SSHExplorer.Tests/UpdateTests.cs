#if CORE_TESTS
using SSHExplorer.Core;

/// <summary>Update check: version order with pre-releases, GitHub's answer parsed safely.</summary>
internal static class UpdateTests
{
    public static async Task RunAsync(TestSuite t)
    {
        await t.Run("updates: versions compare by semantic version, pre-releases before the release, build metadata ignored", () =>
        {
            TestSuite.True(UpdateCheck.Compare("0.4.0", "0.4.0-preview.1") > 0 && UpdateCheck.Compare("0.4.0-preview.2", "0.4.0-preview.10") < 0 && UpdateCheck.Compare("v0.3.1", "0.3.1+abc") == 0
                && UpdateCheck.Compare("0.10.0", "0.9.9") > 0 && UpdateCheck.Compare("0.3.1", "0.4.0-preview.1") < 0 && UpdateCheck.Compare("1.0.0-beta", "1.0.0-alpha") > 0, "order");
        });
        await t.Run("updates: GitHub's latest release is read; drafts, pre-releases, junk and foreign links are not trusted", () =>
        {
            var r = UpdateCheck.Parse("{\"tag_name\":\"v0.4.0\",\"html_url\":\"https://github.com/Jetvac/ssh-explorer/releases/tag/v0.4.0\",\"published_at\":\"2026-11-20T10:00:00Z\",\"draft\":false,\"prerelease\":false}");
            TestSuite.True(r is { Version: "0.4.0" } && r.Url.EndsWith("/v0.4.0", StringComparison.Ordinal) && r.Published == new DateTime(2026, 11, 20, 10, 0, 0) && UpdateCheck.IsNewer(r, "0.4.0-preview.1+abc") && !UpdateCheck.IsNewer(r, "0.4.0"), r?.ToString() ?? "null");
            TestSuite.True(UpdateCheck.Parse("{\"tag_name\":\"v9\",\"prerelease\":true}") is null && UpdateCheck.Parse("{\"tag_name\":\"v9\",\"draft\":true}") is null && UpdateCheck.Parse("not json") is null && UpdateCheck.Parse("[]") is null
                && UpdateCheck.Parse("{\"tag_name\":\"v1.0.0\",\"html_url\":\"https://evil.example/x\"}")!.Url == "https://github.com/Jetvac/ssh-explorer/releases", "untrusted answers");
        });
        await Task.CompletedTask;
    }
}
#endif
