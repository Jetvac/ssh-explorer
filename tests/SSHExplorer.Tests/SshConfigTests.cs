#if CORE_TESTS
using SSHExplorer.Core;

internal static class SshConfigTests
{
    const string Home = "/home/fixture";

    public static async Task RunAsync(TestSuite t)
    {
        await t.Run("ssh_config: first value wins across matching Host blocks; Host * supplies defaults", () =>
        {
            var config = SshConfig.Parse("""
                Host web
                    HostName 10.0.0.5
                    User deploy
                Host *
                    User root
                    Port 2222
                    HostName ignored.example
                """, "/ssh");
            var web = config.Resolve("web", "local", Home);
            TestSuite.Equal("10.0.0.5", web.HostName); TestSuite.Equal("deploy", web.User); TestSuite.Equal(2222, web.Port);
            TestSuite.True(config.Aliases.SequenceEqual(["web"]), "only concrete aliases are import candidates");
        });
        await t.Run("ssh_config: wildcard, ? and negated patterns follow OpenSSH pattern lists", () =>
        {
            TestSuite.True(SshConfig.Matches(["*.prod", "!db.prod"], "web.prod"));
            TestSuite.True(!SshConfig.Matches(["*.prod", "!db.prod"], "db.prod"), "negation excludes");
            TestSuite.True(!SshConfig.Matches(["!db"], "web"), "a negation alone matches nothing");
            TestSuite.True(SshConfig.Matches(["node-?"], "NODE-7"), "case-insensitive single character");
            TestSuite.True(!SshConfig.Matches(["node-?"], "node-17"));
        });
        await t.Run("ssh_config: Keyword=value, quotes and trailing comments", () =>
        {
            var config = SshConfig.Parse("Host \"my box\" other\n  HostName=box.example # comment\n  User = \"admin user\"\n  IdentityFile \"~/.ssh/key with space\"\n", "/ssh");
            var host = config.Resolve("my box", "local", Home);
            TestSuite.Equal("box.example", host.HostName); TestSuite.Equal("admin user", host.User);
            var keyPath = host.IdentityFiles.Single().Replace('\\', '/'); TestSuite.True(keyPath == Home + "/.ssh/key with space", keyPath);
        });
        await t.Run("ssh_config: IdentityFile accumulates and expands %d %u %h %r %n %%", () =>
        {
            var config = SshConfig.Parse("Host app\n HostName app.example\n User svc\n IdentityFile %d/.ssh/%r@%h\n IdentityFile ~/.ssh/%u-%n-100%%\nHost *\n IdentityFile ~/.ssh/id_ed25519\n", "/ssh");
            var files = config.Resolve("app", "me", Home).IdentityFiles.Select(f => f.Replace('\\', '/')).ToArray();
            TestSuite.True(files.SequenceEqual([$"{Home}/.ssh/svc@app.example", $"{Home}/.ssh/me-app-100%", $"{Home}/.ssh/id_ed25519"]), string.Join(" | ", files));
        });
        await t.Run("ssh_config: Include inside Host applies to that block; missing and deep Include are warnings", () =>
        {
            var files = new Dictionary<string, string> { ["/ssh/conf.d/a"] = "User included\n", ["/ssh/loop"] = "Include loop\n" };
            var config = SshConfig.Parse("Host app\n Include conf.d/*\n Include missing\nHost other\n Include loop\n", "/ssh",
                readFile: p => files.GetValueOrDefault(p.Replace('\\', '/')),
                listFiles: p => files.Keys.Where(k => SshConfig.Matches([p.Replace('\\', '/')], k)).Order());
            TestSuite.Equal("included", config.Resolve("app", "me", Home).User);
            TestSuite.Equal<string?>(null, config.Resolve("other", "me", Home).User);
            TestSuite.True(config.Warnings.Any(w => w.Contains("missing", StringComparison.Ordinal)) && config.Warnings.Any(w => w.Contains("вложенность", StringComparison.Ordinal)), string.Join(" | ", config.Warnings));
        });
        await t.Run("ssh_config: ProxyCommand, LocalCommand and Match are reported, never applied or run", () =>
        {
            var config = SshConfig.Parse("Host app\n ProxyCommand nc %h %p\n LocalCommand calc.exe\nMatch exec \"true\"\n User pwned\n", "/ssh");
            var host = config.Resolve("app", "me", Home);
            TestSuite.Equal<string?>(null, host.User);
            TestSuite.True(host.Ignored.Count == 2 && host.Ignored.All(i => i.StartsWith("ProxyCommand", StringComparison.Ordinal) || i.StartsWith("LocalCommand", StringComparison.Ordinal)), string.Join(", ", host.Ignored));
            TestSuite.True(config.Warnings.Any(w => w.Contains("Match", StringComparison.Ordinal)));
        });
        await t.Run("ssh_config: ProxyJump none, invalid Port and unclosed quotes are handled explicitly", () =>
        {
            var config = SshConfig.Parse("Host a\n ProxyJump none\n Port 70000\nHost b\n ProxyJump bastion,jump2:2200\n User \"broken\n", "/ssh");
            var a = config.Resolve("a", "me", Home); var b = config.Resolve("b", "me", Home);
            TestSuite.Equal<string?>(null, a.ProxyJump); TestSuite.Equal<int?>(null, a.Port); TestSuite.True(a.Ignored.Any(i => i.Contains("70000", StringComparison.Ordinal)));
            TestSuite.Equal("bastion,jump2:2200", b.ProxyJump); TestSuite.Equal<string?>(null, b.User);
            TestSuite.True(config.Warnings.Any(w => w.Contains("кавычка", StringComparison.Ordinal)));
        });
        await t.Run("profile import: key, password fallback, duplicates, notes and no secrets", () =>
        {
            var config = SshConfig.Parse("Host web\n HostName 10.0.0.5\n User deploy\n IdentityFile ~/.ssh/missing\n IdentityFile ~/.ssh/present\n ProxyJump bastion\n ConnectTimeout 300\nHost db\n HostName db.example\n IdentityFile ~/.ssh/none\n", "/ssh");
            var existing = new[] { new ServerProfile { Name = "Старый db", Host = "DB.example", Port = 22, Username = "me" } };
            var candidates = ProfileImport.FromSshConfig(config, "me", Home, existing, f => f.Replace('\\', '/').EndsWith("present", StringComparison.Ordinal));
            var web = candidates.Single(c => c.Profile.Name == "web"); var db = candidates.Single(c => c.Profile.Name == "db");
            TestSuite.True(web.Profile.Authentication == AuthenticationKind.PrivateKey && web.Profile.PrivateKeyPath.Replace('\\', '/').EndsWith("present", StringComparison.Ordinal), "first existing key is used");
            TestSuite.Equal("bastion", web.Profile.ProxyJump); TestSuite.Equal(120, web.Profile.TimeoutSeconds);
            TestSuite.True(web.Notes.Any(n => n.Contains("300", StringComparison.Ordinal)) && web.Notes.Any(n => n.Contains("bastion", StringComparison.Ordinal)), string.Join(" | ", web.Notes));
            TestSuite.True(db.Profile.Authentication == AuthenticationKind.Password && db.Profile.Username == "me" && db.DuplicateOf == "Старый db", "missing key falls back to password; same host/port/user is a duplicate");
            TestSuite.True(db.Notes.Any(n => n.Contains("имя Windows", StringComparison.Ordinal)) && db.Notes.Any(n => n.Contains("не найден", StringComparison.Ordinal)), string.Join(" | ", db.Notes));
        });
    }
}
#endif
