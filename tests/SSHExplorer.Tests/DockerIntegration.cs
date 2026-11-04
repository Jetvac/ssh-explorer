#if CORE_TESTS
using SSHExplorer.Core;

/// <summary>Real Docker on the CI runner over SSH (agent sign-in). Arguments: host port user.</summary>
internal static class DockerIntegration
{
    public static async Task RunAsync(TestSuite t, string host, int port, string user)
    {
        var (protect, unprotect) = (DockerSecrets.Protect, DockerSecrets.Unprotect);
        DockerSecrets.Protect = s => "ci:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s)); DockerSecrets.Unprotect = s => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(s[3..]));
        var store = new AppStore(Path.Combine(Path.GetTempPath(), "sshx-docker-" + Guid.NewGuid().ToString("N")));
        await using var session = new ServerSession(new ServerProfile { Name = "Docker · стенд", Host = host, Port = port, Username = user, Authentication = AuthenticationKind.Agent }, store);
        await session.ConnectAsync(new SessionCredentials(""), _ => Task.FromResult(TrustDecision.Session));
        var docker = new DockerService(session);
        var name = "sshx-ci-" + Guid.NewGuid().ToString("N")[..8];
        const string Secret = "ci-secret-value-7f3a";
        var secretEnv = new DockerEnv { Key = "SECRET_TOKEN" }; DockerSecrets.SetSecret(secretEnv, Secret);
        var template = new DockerTemplate { ContainerName = name, Image = "busybox:1.36", RestartPolicy = "no", Ports = ["127.0.0.1:18766:8080"],
            Command = "sh -c 'echo \"greeting=$GREETING token-length=${#SECRET_TOKEN}\"; sleep 600'", Env = [new DockerEnv { Key = "GREETING", Value = "hello ci; $(not run)" }, secretEnv] };
        try
        {
            await t.Run("docker integration: the runner's Docker is usable directly by this account", async () =>
                TestSuite.True(await docker.ProbeAsync() && docker.Version is { Length: > 0 }, docker.Problem ?? "no version"));
            string firstId = "";
            await t.Run("docker integration: a template runs with env from a 0600 env-file; values never reach the command line", async () =>
            {
                firstId = await docker.RunAsync(template, replaceExisting: false);
                var listed = (await docker.ListAsync()).Single(c => c.Name == name);
                TestSuite.True(listed.IsRunning && listed.Ports.Contains("18766", StringComparison.Ordinal), listed.ToString());
                // The value is assembled inside the shell and handed to awk through its environment: neither this probe's
                // own command line nor awk's argv contains it, so any hit is a real leak.
                var leaks = await session.ExecuteAsync("S=\"$(printf 'ci-secret-value-%s' 7f3a)\"; export S; for f in /proc/[0-9]*/cmdline; do tr '\\0' '\\n' <\"$f\" 2>/dev/null | awk 'index($0, ENVIRON[\"S\"]) { hit = 1 } END { exit !hit }' && echo \"$f\"; done | wc -l");
                TestSuite.Equal("0", leaks.Output.Trim());
                var leftovers = await session.ExecuteAsync("ls -A ~/.cache/sshexplorer 2>/dev/null | wc -l");
                TestSuite.Equal("0", leftovers.Output.Trim());
            });
            await t.Run("docker integration: logs show the literal env value (no shell expansion) and the secret's presence", async () =>
            {
                string logs = ""; for (int i = 0; i < 30 && !logs.Contains("greeting=", StringComparison.Ordinal); i++) { await Task.Delay(200); logs = await docker.LogsAsync(name, 50); }
                TestSuite.True(logs.Contains("greeting=hello ci; $(not run) token-length=" + Secret.Length, StringComparison.Ordinal), logs);
            });
            await t.Run("docker integration: a template taken from inspect has the container env but not the image's PATH", async () =>
            {
                var taken = await docker.TemplateFromAsync(name);
                TestSuite.True(taken.ContainerName == name && taken.Image == "busybox:1.36" && taken.Env.Any(e => e.Key == "GREETING") && taken.Env.Any(e => e.Key == "SECRET_TOKEN") && !taken.Env.Any(e => e.Key == "PATH")
                    && taken.Ports.SequenceEqual(["127.0.0.1:18766:8080"]) && taken.RestartPolicy == "no", string.Join(",", taken.Env.Select(e => e.Key)) + " " + string.Join(",", taken.Ports));
            });
            await t.Run("docker integration: recreate with changed env replaces the container and applies the new value", async () =>
            {
                template.Env[0].Value = "changed";
                var secondId = await docker.RunAsync(template, replaceExisting: true);
                TestSuite.True(secondId != firstId && (await docker.ListAsync()).Count(c => c.Name == name) == 1, "one new container");
                string logs = ""; for (int i = 0; i < 30 && !logs.Contains("greeting=changed", StringComparison.Ordinal); i++) { await Task.Delay(200); logs = await docker.LogsAsync(name, 50); }
                TestSuite.True(logs.Contains("greeting=changed", StringComparison.Ordinal), logs);
            });
            await t.Run("docker integration: stop, start, restart and remove change the real state", async () =>
            {
                await docker.StopAsync(name); TestSuite.True((await docker.ListAsync()).Single(c => c.Name == name) is { IsRunning: false }, "stopped");
                await docker.StartAsync(name); TestSuite.True((await docker.ListAsync()).Single(c => c.Name == name) is { IsRunning: true }, "started");
                await docker.RestartAsync(name); TestSuite.True((await docker.ListAsync()).Single(c => c.Name == name) is { IsRunning: true }, "restarted");
                await docker.RemoveAsync(name, force: true); TestSuite.True((await docker.ListAsync()).All(c => c.Name != name), "removed");
            });
        }
        finally
        {
            await session.ExecuteAsync("docker rm -f " + PosixPath.Quote(name) + " >/dev/null 2>&1 || true");
            (DockerSecrets.Protect, DockerSecrets.Unprotect) = (protect, unprotect);
        }
    }
}
#endif
