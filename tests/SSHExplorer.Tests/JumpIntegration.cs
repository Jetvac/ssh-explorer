#if CORE_TESTS
using SSHExplorer.Core;

/// <summary>
/// Runs inside a network namespace that reaches only the bastion. The target and the second bastion listen on the
/// host namespace's loopback, so they are reachable only through the jump chain.
/// Arguments: user bastion-host bastion-port inner-port target-port.
/// </summary>
internal static class JumpIntegration
{
    public static async Task RunAsync(TestSuite t, string user, string bastionHost, int bastionPort, int innerPort, int targetPort)
    {
        var directory = Path.Combine(Path.GetTempPath(), "sshx-jump-" + Guid.NewGuid().ToString("N"));
        var store = new AppStore(directory);
        ServerProfile Target(string jump) => new() { Name = "Цель за бастионом", Host = "127.0.0.1", Port = targetPort, Username = user, Authentication = AuthenticationKind.Agent, TimeoutSeconds = 20, ProxyJump = jump };
        var prompts = new List<string>();
        Task<TrustDecision> Trust(HostKeyPrompt p) { lock (prompts) prompts.Add(p.Host + ":" + p.Port); return Task.FromResult(TrustDecision.Session); }

        await t.Run("jump integration: the target is NOT reachable directly from this network namespace", async () =>
        {
            await using var direct = new ServerSession(Target(""), store);
            try { await direct.ConnectAsync(new SessionCredentials(""), Trust); throw new InvalidDataException("direct connection succeeded"); }
            catch (Exception ex) when (ex is not InvalidDataException) { }
        });
        await t.Run("jump integration: one address hop — exec and SFTP through the tunnel, trust asked for bastion and target", async () =>
        {
            prompts.Clear();
            await using var session = new ServerSession(Target($"{user}@{bastionHost}:{bastionPort}"), store);
            await session.ConnectAsync(new SessionCredentials(""), Trust);
            var r = await session.ExecuteAsync("printf %s \"${SSH_CONNECTION%% *}\"");
            TestSuite.True(r.ExitStatus == 0 && r.Output == "127.0.0.1", "target sees the bastion's loopback as the client: " + r.Output + r.Error);
            TestSuite.True(session.SftpAvailable && session.Route is { Length: > 0 }, "SFTP over the same tunnel; route " + session.Route);
            TestSuite.True(prompts.SequenceEqual([$"{bastionHost}:{bastionPort}", $"127.0.0.1:{targetPort}"]), "trust order: " + string.Join(", ", prompts));
        });
        await t.Run("jump integration: two hops (bastion, then inner bastion reachable only from the first)", async () =>
        {
            prompts.Clear();
            await using var session = new ServerSession(Target($"{user}@{bastionHost}:{bastionPort},{user}@127.0.0.1:{innerPort}"), store);
            await session.ConnectAsync(new SessionCredentials(""), Trust);
            TestSuite.Equal("two-hops", (await session.ExecuteAsync("printf two-hops")).Output);
            TestSuite.True(prompts.Count == 3 && prompts[0] == $"{bastionHost}:{bastionPort}" && prompts[1] == $"127.0.0.1:{innerPort}", "trust order: " + string.Join(", ", prompts));
            TestSuite.True(session.Route?.Contains('→') == true, "route names both hops: " + session.Route);
        });
        await t.Run("jump integration: a saved bastion profile is used by name", async () =>
        {
            var bastion = new ServerProfile { Name = "Бастион CI", Host = bastionHost, Port = bastionPort, Username = user, Authentication = AuthenticationKind.Agent };
            await store.SaveProfileAsync(bastion, null);
            await using var session = new ServerSession(Target("бастион ci"), store);
            await session.ConnectAsync(new SessionCredentials(""), Trust);
            TestSuite.Equal("named", (await session.ExecuteAsync("printf named")).Output);
        });
        await t.Run("jump integration: closing the session closes the tunnel's local port", async () =>
        {
            var session = new ServerSession(Target($"{user}@{bastionHost}:{bastionPort}"), store);
            await session.ConnectAsync(new SessionCredentials(""), Trust);
            var port = session.JumpPort ?? throw new InvalidDataException("no tunnel");
            await session.DisposeAsync();
            using var probe = new System.Net.Sockets.TcpClient();
            try { await probe.ConnectAsync("127.0.0.1", port); throw new InvalidDataException("tunnel port still open: " + port); }
            catch (System.Net.Sockets.SocketException) { }
        });
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }
}
#endif
