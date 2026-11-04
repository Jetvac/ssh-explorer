#if CORE_TESTS
using SSHExplorer.Core;

internal static class PortsTests
{
    public static async Task RunAsync(TestSuite t)
    {
        await t.Run("ports: ss output with IPv4/IPv6, %iface, several processes per socket and owners from ps", () =>
        {
            var output = string.Join("\n",
                "#tool ss",
                "udp   UNCONN 0      0      127.0.0.53%lo:53        0.0.0.0:*    users:((\"systemd-resolve\",pid=600,fd=13))",
                "tcp   LISTEN 0      4096   0.0.0.0:22              0.0.0.0:*    users:((\"sshd\",pid=900,fd=3),(\"sshd\",pid=901,fd=3))",
                "tcp   LISTEN 0      511    [::]:80                 [::]:*",
                "tcp   LISTEN 0      128    127.0.0.1:5432          0.0.0.0:*    users:((\"post \\\"gres\\\"\",pid=1200,fd=5))",
                "#ps", "  600 systemd-resolve", "  900 root", "  901 root", " 1200 postgres", "#uid", "1000");
            var s = ListeningPorts.Parse(output);
            TestSuite.True(s.Tool == "ss" && !s.IsRoot && s.ProcessesHidden && s.Error is null, "flags");
            TestSuite.True(s.Ports.Select(p => $"{p.Protocol}/{p.Port}/{p.Pid}").SequenceEqual(["tcp/22/900", "tcp/22/901", "udp/53/600", "tcp/80/", "tcp/5432/1200"]), string.Join(",", s.Ports.Select(p => $"{p.Protocol}/{p.Port}/{p.Pid}")));
            var dns = s.Ports.Single(p => p.Port == 53); TestSuite.True(dns is { Address: "127.0.0.53", Interface: "lo", Process: "systemd-resolve", User: "systemd-resolve", State: "UNCONN" }, dns.ToString());
            var web = s.Ports.Single(p => p.Port == 80); TestSuite.True(web is { Address: "::", AllInterfaces: true, Pid: null, Process: null } && web.Endpoint == "[::]:80", web.ToString());
            TestSuite.True(s.Ports.Single(p => p.Port == 5432) is { User: "postgres" } pg && pg.SearchText.Contains("postgres", StringComparison.Ordinal), "owner and search text");
        });
        await t.Run("ports: netstat fallback (IPv6 :::22, PID/Program, udp without state), root sees everything", () =>
        {
            var output = string.Join("\n",
                "#tool netstat",
                "Active Internet connections (only servers)",
                "Proto Recv-Q Send-Q Local Address           Foreign Address         State       PID/Program name",
                "tcp        0      0 0.0.0.0:22              0.0.0.0:*               LISTEN      900/sshd: /usr/sbin",
                "tcp6       0      0 :::22                   :::*                    LISTEN      900/sshd: /usr/sbin",
                "udp        0      0 0.0.0.0:68              0.0.0.0:*                           512/dhclient",
                "#ps", "900 root", "512 root", "#uid", "0");
            var s = ListeningPorts.Parse(output);
            TestSuite.True(s.Tool == "netstat" && s.IsRoot && !s.ProcessesHidden, "flags");
            TestSuite.True(s.Ports.Select(p => $"{p.Protocol}/{p.Address}/{p.Port}/{p.State}/{p.Process}").SequenceEqual(["tcp/0.0.0.0/22/LISTEN/sshd", "tcp/::/22/LISTEN/sshd", "udp/0.0.0.0/68/UNCONN/dhclient"]), string.Join(",", s.Ports.Select(p => $"{p.Protocol}/{p.Address}/{p.Port}/{p.State}/{p.Process}")));
        });
        await t.Run("ports: no tool is an explicit message, not an empty list pretending nothing listens", () =>
        {
            var s = ListeningPorts.Parse("#tool none\n#ps\n#uid\n1000");
            TestSuite.True(s.Ports.Count == 0 && s.Error is { Length: > 0 }, s.Error ?? "");
        });
        await Task.CompletedTask;
    }
}
#endif
