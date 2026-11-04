#if CORE_TESTS
using SSHExplorer.Core;

internal static class DockerTests
{
    public static async Task RunAsync(TestSuite t)
    {
        await t.Run("docker: probe picks direct CLI, sudo -n, or explains missing/denied access", () =>
        {
            TestSuite.True(DockerCommands.ParseProbe("#docker direct 27.3.1") is ("docker", "27.3.1", null));
            TestSuite.True(DockerCommands.ParseProbe("#docker sudo 24.0.7") is ("sudo -n docker", "24.0.7", null));
            TestSuite.True(DockerCommands.ParseProbe("#docker missing").Problem!.Contains("не установлен", StringComparison.Ordinal));
            var denied = DockerCommands.ParseProbe("#docker denied\npermission denied while trying to connect to the Docker daemon socket at unix:///var/run/docker.sock");
            TestSuite.True(denied.Cli is null && denied.Problem!.Contains("группу docker", StringComparison.Ordinal), denied.Problem!);
        });
        await t.Run("docker: ps JSON lines become containers, running first, names without slash", () =>
        {
            var list = DockerCommands.ParseList(string.Join('\n',
                """{"Command":"\"nginx -g 'daemon off;'\"","CreatedAt":"2026-11-01 10:00:00 +0000 UTC","ID":"aaaaaaaaaaaaaaaa","Image":"nginx:1.27","Names":"web","Ports":"0.0.0.0:8080->80/tcp","State":"exited","Status":"Exited (0) 2 days ago"}""",
                """{"Command":"\"docker-entrypoint.sh postgres\"","CreatedAt":"2026-11-02 10:00:00 +0000 UTC","ID":"bbbbbbbbbbbbbbbbbbbb","Image":"postgres:16","Names":"db","Ports":"5432/tcp","State":"running","Status":"Up 3 hours"}""",
                "WARNING: not json"));
            TestSuite.True(list.Select(c => c.Name).SequenceEqual(["db", "web"]) && list[0].IsRunning && list[0].ShortId == "bbbbbbbbbbbb" && list[1].Command.StartsWith("nginx", StringComparison.Ordinal), string.Join(",", list.Select(c => c.Name)));
        });
        await t.Run("docker: a template from inspect keeps container env, ports, binds, restart and drops image defaults", () =>
        {
            var inspect = """
                [{"Name":"/api","Config":{"Image":"ghcr.io/acme/api:2","Env":["PATH=/usr/local/bin:/usr/bin","DB_URL=postgres://db/app","TOKEN=a=b=c"],"Cmd":["serve","--port","8080"]},
                  "HostConfig":{"PortBindings":{"8080/tcp":[{"HostIp":"127.0.0.1","HostPort":"18080"}],"53/udp":[{"HostIp":"","HostPort":"5353"}]},"Binds":["/srv/api:/data:ro"],"RestartPolicy":{"Name":"always"},"NetworkMode":"backend"}}]
                """;
            var image = """[{"Config":{"Env":["PATH=/usr/local/bin:/usr/bin"],"Cmd":["serve"]}}]""";
            var tpl = DockerCommands.FromInspect(inspect, image);
            TestSuite.True(tpl is { ContainerName: "api", Image: "ghcr.io/acme/api:2", RestartPolicy: "always", Network: "backend", Command: "serve --port 8080" }, $"{tpl.ContainerName} {tpl.Command}");
            TestSuite.True(tpl.Env.Select(e => e.Key + "=" + e.Value).SequenceEqual(["DB_URL=postgres://db/app", "TOKEN=a=b=c"]), "image PATH dropped; '=' inside value kept");
            TestSuite.True(tpl.Ports.Order().SequenceEqual(["127.0.0.1:18080:8080", "5353:53/udp"]) && tpl.Volumes.SequenceEqual(["/srv/api:/data:ro"]), string.Join(",", tpl.Ports));
        });
        await t.Run("docker: validation names every problem (name, image, ports, volumes, env keys, duplicates, newlines, quotes)", () =>
        {
            var bad = new DockerTemplate { ContainerName = "-bad name", Image = "", Ports = ["80", "x:80", "127.0.0.1:8080:80", "[::1]:8443:443/tcp"], Volumes = ["/data"], RestartPolicy = "sometimes", Command = "echo 'open",
                Env = [new DockerEnv { Key = "1X", Value = "v" }, new DockerEnv { Key = "A", Value = "1" }, new DockerEnv { Key = "A", Value = "2" }, new DockerEnv { Key = "MULTI", Value = "a\nb" }] };
            var errors = DockerCommands.Validate(bad, e => e.Value);
            foreach (var expected in new[] { "Имя контейнера", "образ", "политика", "«x:80»", "Том «/data»", "«1X»", "«A» задана дважды", "«MULTI» содержит перевод", "кавычка" })
                TestSuite.True(errors.Any(e => e.Contains(expected, StringComparison.Ordinal)), "missing: " + expected + " in " + string.Join(" | ", errors));
            TestSuite.True(!errors.Any(e => e.Contains("«80»", StringComparison.Ordinal) || e.Contains("8080:80", StringComparison.Ordinal) || e.Contains("8443", StringComparison.Ordinal)), "valid port forms accepted");
        });
        await t.Run("docker: run quotes every argument, passes env only through --env-file, keeps the command words", () =>
        {
            var tpl = new DockerTemplate { ContainerName = "web", Image = "nginx:1.27", RestartPolicy = "unless-stopped", Ports = ["8080:80"], Volumes = ["/srv/my site:/usr/share/nginx/html:ro"], Command = "nginx -g 'daemon off;'",
                Env = [new DockerEnv { Key = "SECRET", Value = "p@ss; rm -rf /" }] };
            var run = DockerCommands.Run("docker", tpl, "/home/u/.cache/sshexplorer/env-1");
            TestSuite.True(run.StartsWith("docker 'run' '-d' '--name' 'web' '--restart' 'unless-stopped' '-p' '8080:80' '-v' '/srv/my site:/usr/share/nginx/html:ro' '--env-file' '/home/u/.cache/sshexplorer/env-1' 'nginx:1.27' 'nginx' '-g' 'daemon off;'", StringComparison.Ordinal), run);
            TestSuite.True(!run.Contains("p@ss", StringComparison.Ordinal), "env value never in the command line");
            TestSuite.Equal("SECRET=p@ss; rm -rf /\n", DockerCommands.EnvFile(tpl, e => e.Value));
        });
        await t.Run("docker: secret env values are stored encrypted and recovered only through the protector", () =>
        {
            var (protect, unprotect) = (DockerSecrets.Protect, DockerSecrets.Unprotect);
            try
            {
                DockerSecrets.Protect = s => "enc:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s)); DockerSecrets.Unprotect = s => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(s[4..]));
                var e = new DockerEnv { Key = "TOKEN" }; DockerSecrets.SetSecret(e, "s3cr3t");
                TestSuite.True(e is { Secret: true, Value: "" } && e.Protected.StartsWith("enc:", StringComparison.Ordinal) && !e.Protected.Contains("s3cr3t", StringComparison.Ordinal) && DockerSecrets.ValueOf(e) == "s3cr3t", e.Protected);
                var json = System.Text.Json.JsonSerializer.Serialize(new ServerProfile { Name = "x", Host = "h", Username = "u", DockerTemplates = [new DockerTemplate { ContainerName = "c", Image = "i", Env = [e] }] });
                TestSuite.True(!json.Contains("s3cr3t", StringComparison.Ordinal), "no plain secret in settings JSON");
            }
            finally { (DockerSecrets.Protect, DockerSecrets.Unprotect) = (protect, unprotect); }
        });
        await Task.CompletedTask;
    }
}
#endif
