#if CORE_TESTS
using System.Text;
using SSHExplorer.Core;

/// <summary>0.5: RDP profiles — .rdp files, folder inheritance, the pinned server key, the SSH route.</summary>
internal static class RdpTests
{
    public static async Task RunAsync(TestSuite t)
    {
        await t.Run("RDP: a .rdp file (UTF-16, as mstsc writes it) becomes an RDP profile; the password is never taken", () =>
        {
            var text = "screen mode id:i:2\r\nfull address:s:win01.corp.local:3390\r\nusername:s:CORP\\admin\r\nredirectclipboard:i:1\r\nredirectprinters:i:1\r\ndrivestoredirect:s:*\r\n"
                + "audiomode:i:2\r\nenablecredsspsupport:i:1\r\nadministrative session:i:1\r\ngatewayhostname:s:gw.corp.local\r\ngatewayusagemethod:i:1\r\npromptcredentialonce:i:1\r\npassword 51:b:01000000D08C9DDF\r\n";
            var bytes = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes(text)).ToArray();
            var c = RdpFile.ToCandidate("win01", RdpFile.Parse(RdpFile.Decode(bytes)), "me", []);
            var p = c.Profile;
            TestSuite.True(p is { IsRdp: true, Host: "win01.corp.local", Port: 3390, Username: "admin" } && p.Rdp is { Domain: "CORP", Printers: true, Drives: true, Clipboard: true, Audio: RdpAudio.Off, Nla: true, AdminSession: true, GatewayMode: RdpGatewayMode.Always, GatewayHost: "gw.corp.local", FitsTab: true }
                && c.Notes.Any(n => n.Contains("Пароль", StringComparison.Ordinal)), $"{p.Host}:{p.Port} {p.Rdp.Domain}\\{p.Username} " + string.Join(" | ", c.Notes));
            var again = RdpFile.ToCandidate("x", RdpFile.Parse(RdpFile.Write(p)), "me", [p]);
            TestSuite.True(again.Profile is { Host: "win01.corp.local", Port: 3390, Username: "admin" } && again.Profile.Rdp.Domain == "CORP" && again.DuplicateOf == "win01" && !RdpFile.Write(p).Contains("password", StringComparison.OrdinalIgnoreCase), "write/read round trip, duplicate marked, no password written");
            TestSuite.True(RdpFile.SplitAddress("[fe80::1]:3391") == ("fe80::1", 3391) && RdpFile.SplitAddress("fe80::1") == ("fe80::1", null) && RdpFile.SplitAddress("host") == ("host", null), "addresses");
        });
        await t.Run("RDP: options are validated; an SSH profile is unchanged by the new fields", () =>
        {
            TestSuite.True(RdpOptions.Validate(new RdpOptions()) is null && RdpOptions.Validate(new RdpOptions { Width = 500, Height = 400 }) is not null
                && RdpOptions.Validate(new RdpOptions { GatewayMode = RdpGatewayMode.Always }) is not null && RdpOptions.Validate(new RdpOptions { Domain = "a\\b" }) is not null, "rules");
            var rdp = new ServerProfile { Name = "w", Host = "w.example", Port = 3389, Username = "u", Protocol = ConnectionProtocol.Rdp, Authentication = AuthenticationKind.PrivateKey, PrivateKeyPath = "missing.key" };
            rdp.Validate(); // the SSH key file does not matter for RDP
            var json = System.Text.Json.JsonSerializer.Serialize(new ServerProfile { Name = "s", Host = "h", Username = "u" });
            var back = System.Text.Json.JsonSerializer.Deserialize<ServerProfile>("{\"Name\":\"s\",\"Host\":\"h\",\"Username\":\"u\",\"Port\":22}")!;
            TestSuite.True(!back.IsRdp && back.Rdp.Clipboard && back.Inherit == InheritFlags.None && json.Contains("\"Protocol\"", StringComparison.Ordinal), "older settings load as SSH");
        });
        await t.Run("RDP: inheritance takes the nearest folder's value, reports what is missing", () =>
        {
            var prod = new FolderDefaults { Path = "Прод", Username = "deploy", Domain = "CORP", HasPassword = true, Rdp = new RdpOptions { Printers = true, Domain = "IGNORED" } };
            var web = new FolderDefaults { Path = "Прод/Web", Username = "web" };
            var p = new ServerProfile { Name = "a", Host = "h", Username = "", Group = " Прод / Web ", Protocol = ConnectionProtocol.Rdp, Inherit = InheritFlags.Username | InheritFlags.Domain | InheritFlags.Password | InheritFlags.RdpOptions };
            var r = ProfileInheritance.Resolve(p, [prod, web]);
            TestSuite.True(r.Missing.Count == 0 && r.Profile.Username == "web" && r.Profile.Rdp.Domain == "CORP" && r.Profile.Rdp.Printers && r.PasswordFolder == prod.Id && r.Profile.Inherit == InheritFlags.None && p.Username == "",
                $"{r.Profile.Username} {r.Profile.Rdp.Domain} missing: {string.Join(",", r.Missing)}");
            var lonely = ProfileInheritance.Resolve(new ServerProfile { Name = "b", Host = "h", Group = "Тест", Inherit = InheritFlags.Password | InheritFlags.Username }, [prod]);
            TestSuite.True(lonely.Missing.SequenceEqual(["пользователь", "пароль"]), string.Join(",", lonely.Missing));
            p.Validate();
        });
        await t.Run("RDP: the server key is confirmed once; a changed key is reported, replacing it needs an explicit call", async () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "sshx-rdp-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            try
            {
                var store = new AppStore(dir); byte[] key = [1, 2, 3, 4], other = [5, 6, 7, 8];
                TestSuite.True(RdpTrust.Check(store.Trust, "WIN01.corp.local.", 3389, key).Verdict == RdpTrust.Verdict.Unknown, "unknown at first");
                var session = new HashSet<string> { RdpTrust.SessionKey("win01.corp.local", 3389, key) };
                TestSuite.True(RdpTrust.Check(store.Trust, "win01.corp.local", 3389, key, session).Verdict == RdpTrust.Verdict.Trusted, "trusted for this session");
                await store.Trust.TrustAsync("win01.corp.local", 3389, RdpTrust.Algorithm, key, replace: false);
                var reopened = new AppStore(dir);
                var changed = RdpTrust.Check(reopened.Trust, "win01.corp.local", 3389, other);
                TestSuite.True(RdpTrust.Check(reopened.Trust, "win01.corp.local", 3389, key).Verdict == RdpTrust.Verdict.Trusted && changed.Verdict == RdpTrust.Verdict.Changed && changed.Prompt.PreviousFingerprint == HostTrustStore.Fingerprint(key), "saved and compared");
                var refused = false; try { await reopened.Trust.TrustAsync("win01.corp.local", 3389, RdpTrust.Algorithm, other, replace: false); } catch (InvalidOperationException) { refused = true; }
                await reopened.Trust.TrustAsync("win01.corp.local", 3389, RdpTrust.Algorithm, other, replace: true);
                TestSuite.True(refused && RdpTrust.Check(reopened.Trust, "win01.corp.local", 3389, other).Verdict == RdpTrust.Verdict.Trusted && reopened.Trust.Records.Count(r => r.Algorithm == RdpTrust.Algorithm) == 1, "replaced only on request");
                TestSuite.True(RdpTrust.Check(reopened.Trust, "win01.corp.local", 3390, other).Verdict == RdpTrust.Verdict.Unknown, "another port is another server");
            }
            finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
        });
        await t.Run("RDP: an RDP profile is never a jump host; an address-only hop is refused for a password profile", () =>
        {
            var desktop = new ServerProfile { Name = "desk", Host = "d", Username = "u", Protocol = ConnectionProtocol.Rdp };
            var target = new ServerProfile { Name = "t", Host = "t", Port = 3389, Username = "u", Protocol = ConnectionProtocol.Rdp, ProxyJump = "desk" };
            var a = false; try { ProxyJumpSpec.ResolveLast(target, [desktop, target]); } catch (InvalidOperationException ex) { a = ex.Message.Contains("RDP", StringComparison.Ordinal); }
            var b = false; try { ProxyJumpSpec.ResolveLast(new ServerProfile { Name = "t2", Host = "t", Port = 3389, Username = "u", Protocol = ConnectionProtocol.Rdp, ProxyJump = "ops@bastion:22" }, []); } catch (InvalidOperationException ex) { b = ex.Message.Contains("Пароль не передаётся", StringComparison.Ordinal); }
            TestSuite.True(a && b, $"{a} {b}");
        });
    }
}
#endif
