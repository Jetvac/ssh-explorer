#if CORE_TESTS
using SSHExplorer.Core;

internal static class ExternalImportTests
{
    static ExternalSession S(string key, params (string, object?)[] values) => new(key, values.ToDictionary(v => v.Item1, v => v.Item2, StringComparer.OrdinalIgnoreCase));

    public static async Task RunAsync(TestSuite t)
    {
        await t.Run("PuTTY import: URL-encoded names, user@host, ppk keys, non-SSH and Default Settings skipped", () =>
        {
            var (candidates, skipped) = ExternalSessionImport.FromPutty([
                S("Default%20Settings", ("HostName", "")),
                S("Web%20%D0%BF%D1%80%D0%BE%D0%B4", ("HostName", "deploy@web.example"), ("PortNumber", 2222), ("Protocol", "ssh"), ("PublicKeyFile", @"C:\keys\web.ppk"), ("ProxyMethod", 2)),
                S("db", ("HostName", "db.example"), ("UserName", "postgres"), ("PublicKeyFile", @"C:\keys\missing.ppk")),
                S("router", ("HostName", "10.0.0.1"), ("Protocol", "telnet")),
            ], "me", [], f => f.EndsWith("web.ppk", StringComparison.Ordinal));
            var web = candidates.Single(c => c.Profile.Name == "Web прод"); var db = candidates.Single(c => c.Profile.Name == "db");
            TestSuite.True(web.Profile is { Host: "web.example", Username: "deploy", Port: 2222, Authentication: AuthenticationKind.PrivateKey } && web.Profile.PrivateKeyPath.EndsWith("web.ppk", StringComparison.Ordinal), web.Profile.Endpoint);
            TestSuite.True(web.Notes.Any(n => n.Contains("Прокси", StringComparison.Ordinal)), string.Join(" | ", web.Notes));
            TestSuite.True(db.Profile is { Username: "postgres", Port: 22, Authentication: AuthenticationKind.Password } && db.Notes.Any(n => n.Contains("не найден", StringComparison.Ordinal)), string.Join(" | ", db.Notes));
            TestSuite.True(candidates.Count == 2 && skipped.Single().StartsWith("router", StringComparison.Ordinal), string.Join(" | ", skipped));
        });
        await t.Run("WinSCP import: INI sections, folders become groups, FTP skipped, password and tunnel are not carried over", () =>
        {
            var ini = """
                [Configuration\Interface]
                Theme=Dark
                [Sessions\%D0%9F%D1%80%D0%BE%D0%B4/web]
                HostName=web.example
                UserName=deploy
                FSProtocol=2
                Password=A35C4F6E
                RemoteDirectory=/srv/app
                TunnelHostName=bastion.example
                [Sessions\ftp-box]
                HostName=files.example
                FSProtocol=5
                [Sessions\scp-only]
                HostName=old.example
                FSProtocol=0
                """;
            var sessions = ExternalSessionImport.ParseWinScpIni(ini);
            TestSuite.Equal(3, sessions.Count);
            var existing = new[] { new ServerProfile { Name = "Старый", Host = "OLD.example", Port = 22, Username = "me" } };
            var (candidates, skipped) = ExternalSessionImport.FromWinScp(sessions, "me", existing, _ => false);
            var web = candidates.Single(c => c.Profile.Name == "web");
            TestSuite.True(web.Profile is { Group: "Прод", Host: "web.example", Username: "deploy", InitialPath: "/srv/app" }, web.Profile.Group + " " + web.Profile.InitialPath);
            TestSuite.True(web.Notes.Any(n => n.Contains("пароль", StringComparison.Ordinal)) && web.Notes.Any(n => n.Contains("bastion.example", StringComparison.Ordinal)), string.Join(" | ", web.Notes));
            TestSuite.True(candidates.Single(c => c.Profile.Name == "scp-only").DuplicateOf == "Старый", "same host/port/user as an existing profile is a duplicate");
            TestSuite.True(skipped.Single().Contains("FTP", StringComparison.Ordinal), string.Join(" | ", skipped));
        });
        await Task.CompletedTask;
    }
}
#endif
