#if CORE_TESTS
using SSHExplorer.Core;

internal static class ProxyJumpTests
{
    public static async Task RunAsync(TestSuite t)
    {
        await t.Run("ProxyJump: user@host:port, IPv6 in brackets, bare names and none", () =>
        {
            var hops = ProxyJumpSpec.Parse(" ops@bastion.example:2200 , [2001:db8::1]:22,jump ");
            TestSuite.True(hops.Count == 3 && hops[0] is { User: "ops", Host: "bastion.example", Port: 2200 } && hops[1] is { User: null, Host: "2001:db8::1", Port: 22 } && hops[2] is { Host: "jump", Port: null }, string.Join(" | ", hops));
            TestSuite.True(ProxyJumpSpec.Parse("2001:db8::5") is [{ Host: "2001:db8::5", Port: null }], "bare IPv6 has no port");
            TestSuite.True(ProxyJumpSpec.Parse("Бастион CI") is [{ Host: "Бастион CI", User: null, Port: null }], "a profile name may contain spaces");
            TestSuite.Equal(0, ProxyJumpSpec.Parse("none").Count); TestSuite.Equal(0, ProxyJumpSpec.Parse("  ").Count);
        });
        await t.Run("ProxyJump: invalid port, URI form, empty user, too many hops are explicit errors", () =>
        {
            foreach (var bad in new[] { "host:0", "host:99999", "ssh://host", "@host", "[::1", "a,b,c,d", "ops@bad host", "bad host:22" })
                TestSuite.Throws<ArgumentException>(() => ProxyJumpSpec.Parse(bad));
        });
        await t.Run("ProxyJump: the last hop is used first; earlier hops become its own chain", () =>
        {
            var target = new ServerProfile { Name = "app", Host = "10.0.0.5", Username = "deploy", Authentication = AuthenticationKind.Agent, ProxyJump = "edge.example,ops@inner:2200" };
            var r = ProxyJumpSpec.ResolveLast(target, []);
            TestSuite.True(r.Profile is { Host: "inner", Port: 2200, Username: "ops", Authentication: AuthenticationKind.Agent, ProxyJump: "edge.example" } && !r.FromSavedProfile, r.Profile.Endpoint + " via " + r.Profile.ProxyJump);
        });
        await t.Run("ProxyJump: a saved profile name uses that profile's own sign-in and secret, not the target's", () =>
        {
            var bastion = new ServerProfile { Name = "Бастион", Host = "bastion.example", Username = "ops", Authentication = AuthenticationKind.Password, SaveSecret = true, ProxyJump = "edge" };
            var target = new ServerProfile { Name = "app", Host = "10.0.0.5", Username = "deploy", Authentication = AuthenticationKind.Password, ProxyJump = "бастион" };
            var r = ProxyJumpSpec.ResolveLast(target, [bastion]);
            TestSuite.True(r.FromSavedProfile && r.NeedsSavedSecret && r.Profile.Id == bastion.Id && r.Profile.Username == "ops" && r.Profile.ProxyJump == "edge", "named profile keeps its chain");
            target.ProxyJump = "first,бастион";
            TestSuite.Equal("first", ProxyJumpSpec.ResolveLast(target, [bastion]).Profile.ProxyJump);
        });
        await t.Run("ProxyJump: an address hop is refused for a password target (password never sent to another host)", () =>
        {
            var target = new ServerProfile { Name = "app", Host = "10.0.0.5", Username = "deploy", Authentication = AuthenticationKind.Password, ProxyJump = "bastion.example" };
            try { ProxyJumpSpec.ResolveLast(target, []); throw new Exception("resolved"); }
            catch (InvalidOperationException ex) { TestSuite.True(ex.Message.Contains("Пароль не передаётся", StringComparison.Ordinal), ex.Message); }
        });
        await t.Run("ProxyJump: a name with spaces resolves to that profile or is reported as not found", () =>
        {
            var bastion = new ServerProfile { Name = "Бастион CI", Host = "10.200.1.1", Username = "ops", Authentication = AuthenticationKind.Agent };
            var target = new ServerProfile { Name = "app", Host = "10.0.0.5", Username = "u", Authentication = AuthenticationKind.Agent, ProxyJump = "бастион ci" };
            TestSuite.True(ProxyJumpSpec.ResolveLast(target, [bastion]).Profile.Id == bastion.Id, "named with spaces");
            try { ProxyJumpSpec.ResolveLast(target, []); throw new Exception("resolved"); }
            catch (InvalidOperationException ex) { TestSuite.True(ex.Message.Contains("не найден", StringComparison.Ordinal), ex.Message); }
        });
        await t.Run("ProxyJump: a loop back to the target or an earlier hop is refused", () =>
        {
            var target = new ServerProfile { Name = "app", Host = "app.example", Port = 22, Username = "u", Authentication = AuthenticationKind.Agent, ProxyJump = "APP.example:22" };
            TestSuite.Throws<InvalidOperationException>(() => ProxyJumpSpec.ResolveLast(target, []));
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "app.example:22", "edge:22" };
            target.ProxyJump = "edge";
            TestSuite.Throws<InvalidOperationException>(() => ProxyJumpSpec.ResolveLast(target, [], visited));
        });
        await Task.CompletedTask;
    }
}
#endif
