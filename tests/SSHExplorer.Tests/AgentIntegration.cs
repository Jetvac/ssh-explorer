#if CORE_TESTS
using SSHExplorer.Core;

/// <summary>
/// Real OpenSSH server + real ssh-agent (SSH_AUTH_SOCK). Run by the Linux workflow on an isolated sshd port.
/// Arguments: host port user [empty-agent-socket].
/// </summary>
internal static class AgentIntegration
{
    public static async Task RunAsync(TestSuite t, string host, int port, string user, string? emptyAgentSocket)
    {
        var directory = Path.Combine(Path.GetTempPath(), "sshx-agent-" + Guid.NewGuid().ToString("N"));
        var store = new AppStore(directory);
        var profile = new ServerProfile { Name = "Агент · стенд", Host = host, Port = port, Username = user, Authentication = AuthenticationKind.Agent, TimeoutSeconds = 15 };
        var identities = SshAgentEndpoint.Default().ListIdentities();

        await t.Run($"agent integration: real ssh-agent lists {identities.Count} identities ({string.Join(", ", identities.Select(i => i.Algorithm))})", () =>
            TestSuite.True(identities.Any(i => i.Algorithm == "ssh-ed25519") && identities.Any(i => i.Algorithm == "ssh-rsa"), "ed25519 and rsa keys are loaded"));

        foreach (var algorithm in new[] { "ssh-ed25519", "ssh-rsa" })
            await t.Run($"agent integration: real OpenSSH login and exec with the agent's {algorithm} key only", async () =>
            {
                // Offer exactly one identity so the server proves that this key type authenticates.
                SessionAuthentication.AgentFilter = identity => identity.Algorithm == algorithm;
                try
                {
                    await using var session = new ServerSession(profile, store);
                    await session.ConnectAsync(new SessionCredentials(""), _ => Task.FromResult(TrustDecision.Session));
                    var result = await session.ExecuteAsync("printf agent-ok");
                    TestSuite.True(result.ExitStatus == 0 && result.Output == "agent-ok", result.Output + result.Error);
                }
                finally { SessionAuthentication.AgentFilter = null; }
            });

        if (emptyAgentSocket is not null)
            await t.Run("agent integration: an agent without keys fails before contacting the server with a clear message", async () =>
            {
                var previous = SessionAuthentication.AgentFactory;
                SessionAuthentication.AgentFactory = () => SshAgentEndpoint.Unix(emptyAgentSocket, "Пустой агент");
                try
                {
                    await using var session = new ServerSession(profile, store);
                    try { await session.ConnectAsync(new SessionCredentials(""), _ => Task.FromResult(TrustDecision.Session)); throw new Exception("connected without keys"); }
                    catch (Exception ex) when (ex is not InvalidOperationException { Message: "connected without keys" })
                    { TestSuite.True(ErrorMessages.Describe(ex).Contains("нет подходящих ключей", StringComparison.Ordinal), ex.GetType().Name + ": " + ErrorMessages.Describe(ex)); }
                }
                finally { SessionAuthentication.AgentFactory = previous; }
            });
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

}
#endif
