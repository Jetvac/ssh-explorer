#if CORE_TESTS
using SSHExplorer.Core;

/// <summary>mRemoteNG confCons.xml: SSH nodes become profiles, folders become groups, inheritance, skipped protocols, encrypted files.</summary>
internal static class MRemoteNgTests
{
    const string Xml = """
        <?xml version="1.0" encoding="utf-8"?>
        <mrng:Connections xmlns:mrng="http://mremoteng.org" Name="Connections" Export="false" EncryptionEngine="AES" BlockCipherMode="GCM" KdfIterations="10000" FullFileEncryption="false" Protected="abc" ConfVersion="2.6">
          <Node Name="Прод" Type="Container" Username="deploy" Port="22">
            <Node Name="Web" Type="Container" InheritUsername="true">
              <Node Name="web-1" Type="Connection" Hostname="web1.example.net" Protocol="SSH2" Port="2222" InheritUsername="true" Password="AAAAenc" PuttySession="Default Settings" />
              <Node Name="web-2" Type="Connection" Hostname="ops@web2.example.net" Protocol="SSH2" Port="22" Username="" />
            </Node>
            <Node Name="db" Type="Connection" Hostname="db.example.net" Protocol="SSH2" InheritPort="true" Username="postgres" PuttySession="keys" />
            <Node Name="win" Type="Connection" Hostname="win.example.net" Protocol="RDP" Port="3389" />
          </Node>
          <Node Name="old" Type="Connection" Hostname="legacy.example.net" Protocol="SSH1" Port="22" Username="root" />
        </mrng:Connections>
        """;

    public static async Task RunAsync(TestSuite t)
    {
        await t.Run("mRemoteNG: SSH connections with folders as groups and inherited user and port; RDP is skipped", () =>
        {
            var (c, skipped) = ExternalSessionImport.FromMRemoteNg(Xml, "me", [], _ => true);
            var web1 = c.Single(x => x.Profile.Name == "web-1").Profile; var web2 = c.Single(x => x.Profile.Name == "web-2").Profile;
            var db = c.Single(x => x.Profile.Name == "db"); var old = c.Single(x => x.Profile.Name == "old");
            TestSuite.True(c.Count == 4 && web1 is { Host: "web1.example.net", Port: 2222, Username: "deploy", Group: "Прод/Web" } && web2 is { Host: "web2.example.net", Username: "ops" }
                && db.Profile is { Port: 22, Username: "postgres", Group: "Прод" } && db.Notes.Any(n => n.Contains("«keys»", StringComparison.Ordinal)) && old.Profile.Group == "" && old.Notes.Any(n => n.Contains("SSH1", StringComparison.Ordinal))
                && c.Single(x => x.Profile.Name == "web-1").Notes.Any(n => n.Contains("Пароль из mRemoteNG", StringComparison.Ordinal)) && skipped.Single() == "Прод/win: протокол RDP — не SSH",
                string.Join(" | ", c.Select(x => $"{x.Profile.Group}/{x.Profile.Name} {x.Profile.Username}@{x.Profile.Host}:{x.Profile.Port}")) + " ; " + string.Join(" | ", skipped));
            var existing = new ServerProfile { Name = "x", Host = "WEB1.example.net", Port = 2222, Username = "deploy" };
            TestSuite.True(ExternalSessionImport.FromMRemoteNg(Xml, "me", [existing], _ => true).Candidates.Single(x => x.Profile.Name == "web-1").DuplicateOf == "x", "duplicates are marked");
        });
        await t.Run("mRemoteNG: a fully encrypted file, junk and DTDs are refused with a reason", () =>
        {
            var encrypted = ExternalSessionImport.FromMRemoteNg("<Connections FullFileEncryption=\"true\">zzzz</Connections>", "me", []);
            var junk = ExternalSessionImport.FromMRemoteNg("not xml", "me", []);
            var dtd = ExternalSessionImport.FromMRemoteNg("<!DOCTYPE x [<!ENTITY e \"boom\">]><Connections><Node Name=\"&e;\" Type=\"Connection\" Hostname=\"h\" Protocol=\"SSH2\"/></Connections>", "me", []);
            var other = ExternalSessionImport.FromMRemoteNg("<Other/>", "me", []);
            TestSuite.True(encrypted.Candidates.Count == 0 && encrypted.Skipped.Single().Contains("зашифрован целиком", StringComparison.Ordinal) && junk.Skipped.Single().StartsWith("Файл не похож", StringComparison.Ordinal)
                && dtd.Candidates.Count == 0 && dtd.Skipped.Single().StartsWith("Файл не похож", StringComparison.Ordinal) && other.Skipped.Single().Contains("Connections", StringComparison.Ordinal), "refusals");
        });
        await Task.CompletedTask;
    }
}
#endif
