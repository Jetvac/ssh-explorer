#if CORE_TESTS
using System.Net;
using System.Net.Sockets;
using System.Text;
using SSHExplorer.Core;

/// <summary>0.3 D3 on a real server: traffic through -L and -R, counters, a taken port, closing. Arguments: host port user.</summary>
internal static class GroupDIntegration
{
    public static async Task RunAsync(TestSuite t, string host, int port, string user)
    {
        var store = new AppStore(Path.Combine(Path.GetTempPath(), "sshx-d-store-" + Guid.NewGuid().ToString("N")));
        await using var session = new ServerSession(new ServerProfile { Name = "Туннели · стенд", Host = host, Port = port, Username = user, Authentication = AuthenticationKind.Agent }, store);
        await session.ConnectAsync(new SessionCredentials(""), _ => Task.FromResult(TrustDecision.Session));
        // An echo service on this machine: «ping» → «echo:ping». The server is the same runner, so both sides can reach it.
        var echo = new TcpListener(IPAddress.Loopback, 0); echo.Start(); var echoPort = ((IPEndPoint)echo.LocalEndpoint).Port;
        using var stopEcho = new CancellationTokenSource();
        var echoLoop = Task.Run(async () =>
        {
            while (!stopEcho.IsCancellationRequested)
            {
                TcpClient c; try { c = await echo.AcceptTcpClientAsync(stopEcho.Token); } catch { return; }
                _ = Task.Run(async () => { using (c) { var s = c.GetStream(); var buffer = new byte[256]; int n = await s.ReadAsync(buffer); var reply = Encoding.ASCII.GetBytes("echo:" + Encoding.ASCII.GetString(buffer, 0, n).Trim() + "\n"); await s.WriteAsync(reply); } });
            }
        });
        await using var manager = new TunnelManager(session);
        try
        {
            await t.Run("tunnel integration: -L on port 0 gets a free port and carries traffic; connections are counted; closing frees the port", async () =>
            {
                var spec = new TunnelSpec { Name = "L", BindPort = 0, TargetHost = "127.0.0.1", TargetPort = echoPort };
                await manager.StartAsync(spec);
                var status = manager.Status(spec.Id);
                var reply1 = await Talk(status.BoundPort!.Value, "one"); var reply2 = await Talk(status.BoundPort.Value, "two");
                for (int i = 0; i < 20 && manager.Status(spec.Id).Connections < 2; i++) await Task.Delay(100);
                var counted = manager.Status(spec.Id).Connections;
                await manager.StopAsync(spec.Id);
                var closed = await Refused(status.BoundPort.Value);
                TestSuite.True(status.State == TunnelState.Running && status.BoundPort > 0 && reply1 == "echo:one" && reply2 == "echo:two" && counted == 2 && closed && manager.Status(spec.Id).State == TunnelState.Stopped,
                    $"state={status.State} port={status.BoundPort} replies={reply1}/{reply2} counted={counted} closed={closed}");
            });
            await t.Run("tunnel integration: a taken local port fails with a clear reason and leaves nothing open", async () =>
            {
                var blocker = new TcpListener(IPAddress.Loopback, 0); blocker.Start(); var taken = ((IPEndPoint)blocker.LocalEndpoint).Port;
                var spec = new TunnelSpec { BindPort = taken, TargetHost = "127.0.0.1", TargetPort = echoPort };
                string error = "";
                try { await manager.StartAsync(spec); } catch (IOException ex) { error = ex.Message; }
                blocker.Stop();
                TestSuite.True(error.Contains(taken.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal) && manager.Status(spec.Id).State == TunnelState.Failed, error);
            });
            await t.Run("tunnel integration: -R opens a port on the server that reaches this computer; closing removes it", async () =>
            {
                int remotePort = 18000 + Random.Shared.Next(1000);
                var spec = new TunnelSpec { Name = "R", Kind = TunnelKind.Remote, BindPort = remotePort, TargetHost = "127.0.0.1", TargetPort = echoPort };
                await manager.StartAsync(spec);
                var probe = $"python3 -c \"import socket;s=socket.create_connection(('127.0.0.1',{remotePort}),5);s.sendall(b'from-server\\n');print(s.recv(100).decode().strip())\" 2>&1";
                var reply = (await session.ExecuteLongAsync(probe, TimeSpan.FromSeconds(30))).Output.Trim();
                for (int i = 0; i < 20 && manager.Status(spec.Id).Connections < 1; i++) await Task.Delay(100);
                var counted = manager.Status(spec.Id).Connections;
                await manager.StopAsync(spec.Id);
                var after = "";
                for (int i = 0; i < 20; i++) { after = (await session.ExecuteLongAsync(probe, TimeSpan.FromSeconds(30))).Output; if (after.Contains("refused", StringComparison.OrdinalIgnoreCase)) break; await Task.Delay(200); }
                TestSuite.True(reply == "echo:from-server" && counted == 1 && after.Contains("refused", StringComparison.OrdinalIgnoreCase), $"reply={reply} counted={counted} after={after.Trim()}");
            });
        }
        finally { stopEcho.Cancel(); echo.Stop(); try { await echoLoop; } catch { } }
    }

    static async Task<string> Talk(int port, string text)
    {
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, port);
        var s = client.GetStream(); await s.WriteAsync(Encoding.ASCII.GetBytes(text + "\n"));
        var buffer = new byte[256]; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int n = await s.ReadAsync(buffer, timeout.Token);
        return Encoding.ASCII.GetString(buffer, 0, n).Trim();
    }

    static async Task<bool> Refused(int port)
    {
        for (int i = 0; i < 20; i++)
        {
            try { using var c = new TcpClient(); await c.ConnectAsync(IPAddress.Loopback, port); }
            catch (SocketException) { return true; }
            await Task.Delay(100);
        }
        return false;
    }
}
#endif
