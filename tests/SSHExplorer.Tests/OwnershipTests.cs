#if CORE_TESTS
using SSHExplorer.Core;

internal static class OwnershipTests
{
    public static async Task RunAsync(TestSuite t)
    {
        await t.Run("ownership: names parse, numbers need no lookup, names are validated before a command is built", () =>
        {
            TestSuite.True(Ownership.ParseNames("u:deploy\ng:www-data\n") is ("deploy", "www-data"));
            TestSuite.True(Ownership.ParseNames("u:\ng:\n") is (null, null), "unknown ids have no name");
            TestSuite.Equal<string?>(null, Ownership.IdCommand("1000", group: false));
            TestSuite.Equal("getent group 'www-data' | cut -d: -f3", Ownership.IdCommand("www-data", group: true));
            TestSuite.True(Ownership.IdCommand("x; rm -rf /", false) is null && !Ownership.IsValidName("a b") && Ownership.IsValidName("svc$"), "invalid names refused");
            TestSuite.Equal("find '/srv/my dir' -xdev 2>/dev/null | wc -l", Ownership.CountCommand("/srv/my dir"));
        });
        await Task.CompletedTask;
    }
}
#endif
