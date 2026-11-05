#if CORE_TESTS
using System.Security.Cryptography;
using SSHExplorer.Core;

/// <summary>.sshx export and import: without secrets, sealed with a passphrase, conflicts, safety defaults, host keys.</summary>
internal static class ExchangeTests
{
    static ServerProfile Sample(string name, string host) => new()
    {
        Name = name, Host = host, Username = "deploy", SaveSecret = true, Group = "Прод", ProxyJump = "bastion",
        Scripts = [new SavedScript { Name = "ночной бэкап", Body = "tar czf /tmp/b.tgz /etc", Trigger = ScriptTrigger.Daily, LastRunUtc = DateTime.UtcNow, LastExitCode = 0 }, new SavedScript { Name = "uptime", Body = "uptime" }],
        Tunnels = [new TunnelSpec { Name = "db", BindPort = 15432, TargetPort = 5432, StartOnConnect = true }, new TunnelSpec { Name = "open", BindAddress = "0.0.0.0", BindPort = 8080, TargetPort = 80, StartOnConnect = true }],
        DockerTemplates = [new DockerTemplate { ContainerName = "web", Image = "nginx", Env = [new DockerEnv { Key = "MODE", Value = "prod" }, new DockerEnv { Key = "TOKEN", Secret = true, Protected = "DPAPI(s3cret)" }] }],
    };
    static string FakeUnprotect(string stored) => stored.StartsWith("DPAPI(", StringComparison.Ordinal) ? stored[6..^1] : throw new CryptographicException("not ours");
    static string FakeProtect(string plain) => "LOCAL(" + plain + ")";

    public static async Task RunAsync(TestSuite t)
    {
        var settings = new AppSettings();
        var bastion = new ServerProfile { Name = "bastion", Host = "203.0.113.5", Username = "jump" };
        var trust = new[] { new TrustedHostKey { Host = "app.example.net", Port = 22, Algorithm = "ssh-ed25519", PublicKey = Convert.ToBase64String(new byte[] { 1, 2, 3 }), Fingerprint = "x" }, new TrustedHostKey { Host = "other.example.net", Port = 22, Algorithm = "ssh-ed25519", PublicKey = "AQID", Fingerprint = "y" } };
        var profile = Sample("Приложение", "app.example.net");
        int reads = 0; Task<string?> Read(Guid _) { reads++; return Task.FromResult<string?>("пароль сервера"); }

        await t.Run("exchange: without secrets nothing secret leaves the computer; run history is dropped", async () =>
        {
            var doc = await ProfileExchange.BuildAsync([profile, bastion], new ExportOptions(IncludeHostKeys: true), "0.4.0", Read, settings, trust, FakeUnprotect);
            var text = ProfileExchange.Serialize(doc);
            var p = doc.Servers[0].Profile;
            TestSuite.True(doc.Seal is null && reads == 0 && doc.Servers.All(s => s.Secret is null) && !text.Contains("s3cret", StringComparison.Ordinal) && !text.Contains("DPAPI(", StringComparison.Ordinal) && !text.Contains("пароль сервера", StringComparison.Ordinal),
                "no seal, no secret read, no secret text");
            TestSuite.True(p.Scripts[0].LastRunUtc is null && p.Scripts[0].LastExitCode is null && !p.SaveSecret && p.DockerTemplates[0].Env[1] is { Secret: true, Protected: "", Value: "" } && p.DockerTemplates[0].Env[0].Value == "prod", "history and secret values dropped, plain env kept");
            TestSuite.True(doc.HostKeys!.Count == 1 && doc.HostKeys[0].Host == "app.example.net" && profile.Scripts[0].LastRunUtc is not null, "only the exported servers' keys; the source profile is not changed");
        });

        await t.Run("exchange: a sealed file opens only with its passphrase and re-protects secrets for this computer", async () =>
        {
            var doc = await ProfileExchange.BuildAsync([profile], new ExportOptions(true, "верная фраза 1"), "0.4.0", Read, settings, [], FakeUnprotect);
            var text = ProfileExchange.Serialize(doc);
            TestSuite.True(doc.Seal is { Iterations: ProfileExchange.Iterations } && !text.Contains("пароль сервера", StringComparison.Ordinal) && !text.Contains("s3cret", StringComparison.Ordinal) && ProfileExchange.HasSecrets(doc), "sealed, not readable in the file");
            var back = ProfileExchange.Parse(text);
            TestSuite.True(!ProfileExchange.CheckPassphrase(back, "неверная фраза") && ProfileExchange.CheckPassphrase(back, "верная фраза 1"), "the check tells a wrong passphrase");
            TestSuite.Throws<CryptographicException>(() => ProfileExchange.Plan(back, "неверная фраза", [], _ => true, FakeProtect));
            var plan = ProfileExchange.Plan(back, "верная фраза 1", [], _ => true, FakeProtect).Single();
            TestSuite.True(plan.Secret == "пароль сервера" && plan.Profile.DockerTemplates[0].Env[1].Protected == "LOCAL(s3cret)" && plan.Profile.Id != profile.Id && plan.Existing is null, "secret opened, Docker secret re-protected, fresh id");
            var without = ProfileExchange.Plan(back, null, [], _ => true, FakeProtect).Single();
            TestSuite.True(without.Secret is null && without.Notes.Any(n => n.Contains("пароль не перенесён", StringComparison.Ordinal)) && without.Profile.DockerTemplates[0].Env[1].Protected == "", "without the passphrase: imported without secrets, and said so");
        });

        await t.Run("exchange: imported commands never start by themselves", async () =>
        {
            var doc = ProfileExchange.Parse(ProfileExchange.Serialize(await ProfileExchange.BuildAsync([profile], new ExportOptions(), "0.4.0", Read, settings, [], FakeUnprotect)));
            var p = ProfileExchange.Plan(doc, null, [bastion], _ => true, FakeProtect).Single();
            TestSuite.True(!p.Profile.Scripts[0].Enabled && p.Profile.Scripts[1].Enabled && p.Profile.Tunnels[0].StartOnConnect && !p.Profile.Tunnels[1].StartOnConnect
                && p.Notes.Any(n => n.Contains("приостановленными", StringComparison.Ordinal)) && p.Notes.Any(n => n.Contains("Туннели, открытые", StringComparison.Ordinal)), string.Join(" | ", p.Notes));
            TestSuite.True(!p.Notes.Any(n => n.Contains("Промежуточный сервер", StringComparison.Ordinal)), "the jump server is known here");
            var lonely = ProfileExchange.Plan(doc, null, [], _ => true, FakeProtect).Single();
            TestSuite.True(lonely.Notes.Any(n => n.Contains("«bastion» не найден", StringComparison.Ordinal)), "a missing jump server is pointed out");
        });

        await t.Run("exchange: a matching server is found; replace keeps its id, a copy gets a free name; a missing key needs a fix", async () =>
        {
            var doc = ProfileExchange.Parse(ProfileExchange.Serialize(await ProfileExchange.BuildAsync([profile], new ExportOptions(), "0.4.0", Read, settings, [], FakeUnprotect)));
            var mine = new ServerProfile { Name = "Приложение", Host = "APP.example.net.", Username = "deploy", SaveSecret = true };
            var taken = new ServerProfile { Name = "Приложение (импорт)", Host = "x", Username = "y" };
            var c = ProfileExchange.Plan(doc, null, [mine, taken, bastion], _ => true, FakeProtect).Single();
            var replaced = ProfileExchange.Resolve(c, ConflictChoice.Replace, [mine, taken]);
            var copy = ProfileExchange.Resolve(c, ConflictChoice.KeepBoth, [mine, taken]);
            TestSuite.True(c.Existing == mine && replaced.Id == mine.Id && replaced.SaveSecret && copy.Id != mine.Id && copy.Name == "Приложение (импорт) 2" && !copy.SaveSecret, $"{replaced.Id} {copy.Name}");
            var keyed = Sample("ключ", "k.example.net"); keyed.Authentication = AuthenticationKind.PrivateKey; keyed.PrivateKeyPath = @"C:\Users\old\.ssh\id_ed25519";
            var k = ProfileExchange.Plan(ProfileExchange.Parse(ProfileExchange.Serialize(await ProfileExchange.BuildAsync([keyed], new ExportOptions(), "0.4.0", Read, settings, [], FakeUnprotect))), null, [], _ => false, FakeProtect).Single();
            TestSuite.True(k.NeedsFix && k.Notes.Any(n => n.Contains("ключ не найден", StringComparison.Ordinal)), string.Join(" | ", k.Notes));
        });

        await t.Run("exchange: foreign, damaged and newer files are refused with a reason; short passphrases are refused", async () =>
        {
            TestSuite.Throws<InvalidDataException>(() => ProfileExchange.Parse("{\"Format\":\"other\"}"));
            TestSuite.Throws<InvalidDataException>(() => ProfileExchange.Parse("not json"));
            TestSuite.Throws<InvalidDataException>(() => ProfileExchange.Parse("{\"Format\":\"sshexplorer-export\",\"Version\":99}"));
            TestSuite.True(ProfileExchange.ValidatePassphrase("короткая", "короткая") is null && ProfileExchange.ValidatePassphrase("1234567", "1234567") is not null && ProfileExchange.ValidatePassphrase("12345678", "12345679") is not null, "passphrase rules");
            var threw = false; try { await ProfileExchange.BuildAsync([profile], new ExportOptions(true, "short"), "0.4.0", Read, settings, [], FakeUnprotect); } catch (ArgumentException) { threw = true; }
            TestSuite.True(threw, "a short passphrase cannot seal");
        });

        await t.Run("exchange: extras — panel layout and firewall rule sets travel when asked", async () =>
        {
            var s = new AppSettings { SidebarOrder = ["files", "server"], SidebarHidden = ["tunnels"], DockerModule = true };
            s.FirewallRuleSets.Add(new FirewallRuleSet { Name = "веб", Rules = [new RuleDraft { Port = "80,443" }], DefaultIncoming = "deny" });
            var doc = ProfileExchange.Parse(ProfileExchange.Serialize(await ProfileExchange.BuildAsync([bastion], new ExportOptions(IncludeSidebar: true, IncludeRuleSets: true), "0.4.0", Read, s, [], FakeUnprotect)));
            TestSuite.True(doc.Sidebar is { Docker: true } side && side.Order.SequenceEqual(["files", "server"]) && side.Hidden.SequenceEqual(["tunnels"]) && doc.RuleSets!.Single().Rules.Single().Port == "80,443" && doc.RuleSets[0].DefaultIncoming == "deny", "extras");
            var plain = ProfileExchange.Parse(ProfileExchange.Serialize(await ProfileExchange.BuildAsync([bastion], new ExportOptions(), "0.4.0", Read, s, [], FakeUnprotect)));
            TestSuite.True(plain.Sidebar is null && plain.RuleSets is null && plain.HostKeys is null, "not asked — not included");
        });

        await t.Run("exchange: imported host keys are added, never replace a different trusted key", async () =>
        {
            var store = new AppStore(Path.Combine(Path.GetTempPath(), "sshx-exchange-" + Guid.NewGuid().ToString("N")));
            var (added, conflicts) = await store.Trust.ImportAsync(trust);
            var changed = new TrustedHostKey { Host = "APP.example.net", Port = 22, Algorithm = "ssh-ed25519", PublicKey = Convert.ToBase64String(new byte[] { 9, 9 }) };
            var (again, clash) = await store.Trust.ImportAsync([changed, trust[0]]);
            var reloaded = new AppStore(store.DirectoryPath).Trust.Records;
            TestSuite.True(added == 2 && conflicts.Count == 0 && again == 0 && clash.Count == 1 && reloaded.Count == 2 && reloaded.Single(r => r.Host == "app.example.net").PublicKey == trust[0].PublicKey
                && reloaded[0].Fingerprint.StartsWith("SHA256:", StringComparison.Ordinal), $"{added} {again} {clash.Count} {reloaded.Count}");
        });
    }
}
#endif
