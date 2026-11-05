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
            <Node Name="win" Type="Connection" Hostname="win.example.net" Protocol="RDP" Port="3389" InheritUsername="true" />
            <Node Name="vnc" Type="Connection" Hostname="vnc.example.net" Protocol="VNC" Port="5900" />
          </Node>
          <Node Name="old" Type="Connection" Hostname="legacy.example.net" Protocol="SSH1" Port="22" Username="root" />
        </mrng:Connections>
        """;

    public static async Task RunAsync(TestSuite t)
    {
        await t.Run("mRemoteNG: SSH and RDP connections with folders as groups and inherited user and port; VNC is skipped", () =>
        {
            var (c, skipped) = ExternalSessionImport.FromMRemoteNg(Xml, "me", [], _ => true);
            var web1 = c.Single(x => x.Profile.Name == "web-1").Profile; var web2 = c.Single(x => x.Profile.Name == "web-2").Profile;
            var db = c.Single(x => x.Profile.Name == "db"); var old = c.Single(x => x.Profile.Name == "old");
            var win = c.Single(x => x.Profile.Name == "win").Profile;
            TestSuite.True(win is { IsRdp: true, Host: "win.example.net", Port: 3389, Username: "deploy", Group: "Прод" } && win.Inherit == InheritFlags.Username, $"rdp: {win.Username} {win.Port} {win.Inherit}");
            TestSuite.True(c.Count == 5 && web1 is { Host: "web1.example.net", Port: 2222, Username: "deploy", Group: "Прод/Web" } && web2 is { Host: "web2.example.net", Username: "ops" }
                && db.Profile is { Port: 22, Username: "postgres", Group: "Прод" } && db.Notes.Any(n => n.Contains("«keys»", StringComparison.Ordinal)) && old.Profile.Group == "" && old.Notes.Any(n => n.Contains("SSH1", StringComparison.Ordinal))
                && c.Single(x => x.Profile.Name == "web-1").Notes.Any(n => n.Contains("Пароль из mRemoteNG", StringComparison.Ordinal)) && skipped.Single() == "Прод/vnc: протокол VNC — не SSH и не RDP",
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
        await t.Run("mRemoteNG: passwords are decrypted only on request — with the built-in password, or the user's own", () =>
        {
            string File(string password, bool protectedByUser, string body) =>
                $"<Connections Name=\"Connections\" EncryptionEngine=\"AES\" BlockCipherMode=\"GCM\" KdfIterations=\"1000\" FullFileEncryption=\"false\" Protected=\"{MRemoteNg.EncryptForTest(protectedByUser ? "ThisIsProtected" : "ThisIsNotProtected", password)}\" ConfVersion=\"2.6\">{body}</Connections>";
            string Body(string password) =>
                $"<Node Name=\"Офис\" Type=\"Container\" Username=\"buh\" Domain=\"CORP\" Password=\"{MRemoteNg.EncryptForTest("Папка-1!", password)}\">"
                + $"<Node Name=\"pc-1\" Type=\"Connection\" Protocol=\"RDP\" Hostname=\"pc1.corp\" Port=\"3389\" InheritUsername=\"true\" InheritDomain=\"true\" InheritPassword=\"true\" Resolution=\"Res1600x900\" RedirectPrinters=\"true\" RedirectSound=\"DoNotPlay\" UseConsoleSession=\"true\" RDGatewayUsageMethod=\"Always\" RDGatewayHostname=\"gw.corp\" />"
                + $"<Node Name=\"pc-2\" Type=\"Connection\" Protocol=\"RDP\" Hostname=\"pc2.corp\" Username=\"CORP2\\boss\" Password=\"{MRemoteNg.EncryptForTest("Свой-2!", password)}\" />"
                + "</Node>";
            var plain = File(MRemoteNg.DefaultPassword, false, Body(MRemoteNg.DefaultPassword));
            var none = MRemoteNg.Read(plain, "me", []);
            TestSuite.True(none.HasPasswords && none.Secrets.Count == 0 && none.FolderSecrets.Count == 0 && none.Candidates.All(x => !x.Profile.SaveSecret), "without the request nothing is decrypted");
            var withDefault = MRemoteNg.Read(plain, "me", [], importPasswords: true);
            var pc1 = withDefault.Candidates.Single(x => x.Profile.Name == "pc-1").Profile; var pc2 = withDefault.Candidates.Single(x => x.Profile.Name == "pc-2").Profile;
            var folder = withDefault.Folders.Single();
            TestSuite.True(!withDefault.NeedsPassword && withDefault.Secrets[pc2.Id] == "Свой-2!" && pc2.SaveSecret && pc2 is { Username: "boss" } && pc2.Rdp.Domain == "CORP2"
                && folder is { Path: "Офис", Username: "buh", Domain: "CORP", HasPassword: true } && withDefault.FolderSecrets[folder.Id] == "Папка-1!",
                $"{pc2.Username} {pc2.Rdp.Domain} folder {folder.Path} {folder.Username}");
            TestSuite.True(pc1.Inherit == (InheritFlags.Username | InheritFlags.Domain | InheritFlags.Password) && !withDefault.Secrets.ContainsKey(pc1.Id) && pc1.Username == "buh"
                && pc1.Rdp is { Width: 1600, Height: 900, Printers: true, Audio: RdpAudio.Off, AdminSession: true, GatewayMode: RdpGatewayMode.Always, GatewayHost: "gw.corp" }
                && ProfileInheritance.Resolve(pc1, withDefault.Folders) is { Missing.Count: 0, PasswordFolder: { } pf } && pf == folder.Id, "pc-1 keeps inheriting from its folder; RDP options are mapped");
            var own = File("секрет mRemoteNG", true, Body("секрет mRemoteNG"));
            var needs = MRemoteNg.Read(own, "me", [], importPasswords: true);
            var wrong = MRemoteNg.Read(own, "me", [], importPasswords: true, password: "не тот");
            var right = MRemoteNg.Read(own, "me", [], importPasswords: true, password: "секрет mRemoteNG");
            TestSuite.True(needs.NeedsPassword && !needs.WrongPassword && needs.Secrets.Count == 0 && wrong.NeedsPassword && wrong.WrongPassword && !right.NeedsPassword && right.Secrets.Values.Single() == "Свой-2!", "an own mRemoteNG password is required and checked");
            var full = $"<Connections Name=\"Connections\" EncryptionEngine=\"AES\" BlockCipherMode=\"GCM\" KdfIterations=\"1000\" FullFileEncryption=\"true\" Protected=\"{MRemoteNg.EncryptForTest("ThisIsProtected", "к")}\">{MRemoteNg.EncryptForTest(Body("к"), "к")}</Connections>";
            var locked = MRemoteNg.Read(full, "me", []); var opened = MRemoteNg.Read(full, "me", [], password: "к");
            TestSuite.True(locked.Candidates.Count == 0 && locked.NeedsPassword && locked.Skipped.Single().Contains("зашифрован целиком", StringComparison.Ordinal) && opened.Candidates.Count == 2 && opened.Secrets.Count == 0, "a fully encrypted file opens with its password");
            var serpent = MRemoteNg.EncryptForTest("x", "p", 1000, "Serpent", "EAX");
            TestSuite.True(MRemoteNg.Read($"<Connections EncryptionEngine=\"Serpent\" BlockCipherMode=\"EAX\"><Node Name=\"s\" Type=\"Connection\" Protocol=\"RDP\" Hostname=\"h\" Username=\"u\" Password=\"{serpent}\"/></Connections>", "me", [], true, "p").Secrets.Values.Single() == "x", "Serpent/EAX files");
        });
        await Task.CompletedTask;
    }
}
#endif
