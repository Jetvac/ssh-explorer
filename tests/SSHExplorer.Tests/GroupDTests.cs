#if CORE_TESTS
using SSHExplorer.Core;

/// <summary>0.3 D3: tunnel validation and descriptions.</summary>
internal static class GroupDTests
{
    public static async Task RunAsync(TestSuite t)
    {
        await t.Run("tunnels: ports and addresses are validated; remote needs an explicit port, local may pick one", () =>
        {
            TestSuite.True(Tunnels.Validate(new TunnelSpec { BindPort = 0, TargetHost = "db", TargetPort = 5432 }) is null, "local port 0 = any free port");
            TestSuite.True(Tunnels.Validate(new TunnelSpec { Kind = TunnelKind.Remote, BindPort = 0, TargetPort = 80 }) is not null, "remote port 0 refused");
            TestSuite.True(Tunnels.Validate(new TunnelSpec { BindPort = 70000, TargetPort = 80 }) is not null && Tunnels.Validate(new TunnelSpec { BindPort = 8080, TargetPort = 0 }) is not null, "port ranges");
            TestSuite.True(Tunnels.Validate(new TunnelSpec { BindPort = 1, TargetHost = "a b", TargetPort = 80 }) is not null && Tunnels.Validate(new TunnelSpec { BindAddress = "", BindPort = 1, TargetPort = 80 }) is not null
                && Tunnels.Validate(new TunnelSpec { BindPort = 1, TargetHost = "user@host", TargetPort = 80 }) is not null, "hosts");
        });
        await t.Run("tunnels: loopback is the default, other addresses are exposed; descriptions name both sides", () =>
        {
            TestSuite.True(!Tunnels.IsExposed(new TunnelSpec()) && !Tunnels.IsExposed(new TunnelSpec { BindAddress = "localhost" }) && !Tunnels.IsExposed(new TunnelSpec { BindAddress = "::1" })
                && Tunnels.IsExposed(new TunnelSpec { BindAddress = "0.0.0.0" }) && Tunnels.IsExposed(new TunnelSpec { BindAddress = "192.168.1.5" }), "exposure");
            TestSuite.Equal("127.0.0.1:8080 на компьютере → db.internal:5432 со стороны сервера", Tunnels.Describe(new TunnelSpec { BindPort = 8080, TargetHost = "db.internal", TargetPort = 5432 }));
            TestSuite.Equal("127.0.0.1:9000 на сервере → [::1]:3000 со стороны компьютера", Tunnels.Describe(new TunnelSpec { Kind = TunnelKind.Remote, BindPort = 9000, TargetHost = "::1", TargetPort = 3000 }));
            TestSuite.Equal("127.0.0.1:51234 на компьютере → db:1 со стороны сервера", Tunnels.Describe(new TunnelSpec { BindPort = 0, TargetHost = "db", TargetPort = 1 }, boundPort: 51234));
            var original = new TunnelSpec { Name = "pg" }; var copy = original.Clone(); copy.Name = "x";
            TestSuite.True(original.Name == "pg" && copy.Id == original.Id, "clone is independent and keeps the id");
        });
    }
}
#endif
