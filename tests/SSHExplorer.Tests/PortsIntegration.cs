#if CORE_TESTS
using SSHExplorer.Core;

/// <summary>Real `ss` on the CI runner over SSH. Arguments: host port user own-listener-port own-listener-pid.</summary>
internal static class PortsIntegration
{
    public static async Task RunAsync(TestSuite t, string host, int port, string user, int listenerPort, int listenerPid)
    {
        var store = new AppStore(Path.Combine(Path.GetTempPath(), "sshx-ports-" + Guid.NewGuid().ToString("N")));
        await using var session = new ServerSession(new ServerProfile { Name = "Порты · стенд", Host = host, Port = port, Username = user, Authentication = AuthenticationKind.Agent }, store);
        await session.ConnectAsync(new SessionCredentials(""), _ => Task.FromResult(TrustDecision.Session));
        var snapshot = ListeningPorts.Parse((await session.ExecuteAsync(ListeningPorts.Command)).Output);
        await t.Run($"ports integration: {snapshot.Tool} lists {snapshot.Ports.Count} sockets; our listener {listenerPort} has its PID, process and owner", () =>
        {
            var own = snapshot.Ports.FirstOrDefault(p => p.Port == listenerPort && p.Protocol == "tcp");
            TestSuite.True(own is { State: "LISTEN", Address: "127.0.0.1" } && own.Pid == listenerPid && own.User == user && own.Process is { Length: > 0 }, own?.ToString() ?? string.Join(", ", snapshot.Ports.Select(p => p.Endpoint)));
        });
        await t.Run("ports integration: the root-owned sshd port is listed even though its process is hidden from this user", () =>
        {
            var sshd = snapshot.Ports.FirstOrDefault(p => p.Port == port && p.Protocol == "tcp");
            TestSuite.True(sshd is not null && (snapshot.IsRoot || (sshd.Pid is null && snapshot.ProcessesHidden)), sshd?.ToString() ?? "missing");
        });
    }
}
#endif
