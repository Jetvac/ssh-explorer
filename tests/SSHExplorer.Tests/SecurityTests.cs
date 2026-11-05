#if CORE_TESTS
using System.Buffers.Binary;
using System.Text;
using SSHExplorer.Core;

/// <summary>Key login (key pair, public key lines, authorized_keys edits) and the sshd audit with its safe-apply script.</summary>
internal static class SecurityTests
{
    const string Effective = """
        #user deploy 1000
        #effective
        port 22
        permitrootlogin yes
        passwordauthentication yes
        kbdinteractiveauthentication no
        pubkeyauthentication yes
        permitemptypasswords no
        maxauthtries 6
        logingracetime 120
        x11forwarding yes
        usepam yes
        #include
        yes
        #ours
        MaxAuthTries 5
        #end
        """;

    static byte[] RsaBlob(int bits)
    {
        var ms = new MemoryStream();
        void Bytes(byte[] b) { Span<byte> n = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(n, (uint)b.Length); ms.Write(n); ms.Write(b); }
        Bytes(Encoding.ASCII.GetBytes("ssh-rsa")); Bytes([1, 0, 1]);
        var modulus = new byte[bits / 8 + 1]; modulus[1] = 0xC3; Bytes(modulus);
        return ms.ToArray();
    }

    public static async Task RunAsync(TestSuite t)
    {
        await t.Run("keys: a new Ed25519 key loads in SSH.NET and its public line matches", () =>
        {
            var key = SshKeys.GenerateEd25519("me@pc");
            var pub = SshKeys.Parse(key.PublicLine)!;
            using var file = new Renci.SshNet.PrivateKeyFile(new MemoryStream(Encoding.ASCII.GetBytes(key.PrivateKeyText)));
            TestSuite.True(key.PrivateKeyText.StartsWith("-----BEGIN OPENSSH PRIVATE KEY-----\n", StringComparison.Ordinal) && pub is { Type: "ssh-ed25519", Comment: "me@pc", Options: "" } && pub.Fingerprint == key.Fingerprint
                && file.Key.GetType().Name.Contains("ED25519", StringComparison.OrdinalIgnoreCase) && SshKeys.GenerateEd25519("").Fingerprint != key.Fingerprint, key.Fingerprint);
            TestSuite.Throws<ArgumentException>(() => SshKeys.GenerateEd25519("a\nb"));
        });

        await t.Run("keys: authorized_keys lines with options, RSA size, weak keys; private keys and junk are refused", () =>
        {
            var ed = SshKeys.GenerateEd25519("laptop").PublicLine;
            var withOptions = SshKeys.Parse("from=\"10.0.0.0/8\",command=\"echo hi there\" " + ed)!;
            var rsa1024 = SshKeys.Parse("ssh-rsa " + Convert.ToBase64String(RsaBlob(1024)) + " old")!; var rsa4096 = SshKeys.Parse("ssh-rsa " + Convert.ToBase64String(RsaBlob(4096)))!;
            TestSuite.True(withOptions is { Type: "ssh-ed25519", Comment: "laptop" } && withOptions.Options.StartsWith("from=", StringComparison.Ordinal) && rsa1024 is { Bits: 1024, Weak: true, TypeText: "RSA 1024" } && rsa4096 is { Bits: 4096, Weak: false }, $"{rsa1024.Bits} {rsa4096.Bits}");
            var list = SshKeys.ParseList("# comment\n" + ed + "\n\ngarbage line\n" + withOptions.Line + "\n#end\nafter");
            TestSuite.True(list.Count == 2, "list");
            TestSuite.True(SshKeys.ParsePublic("-----BEGIN OPENSSH PRIVATE KEY-----\nabc").Error!.Contains("закрытый", StringComparison.Ordinal) && SshKeys.ParsePublic(ed + "\n" + ed).Key is null
                && SshKeys.ParsePublic("ssh-ed25519 notbase64").Key is null && SshKeys.ParsePublic("ssh-ed25519 " + Convert.ToBase64String(RsaBlob(2048))).Key is null && SshKeys.ParsePublic(" " + ed + " \n").Error is null, "parse public");
        });

        await t.Run("keys: install adds once with sshd's permissions; removal is by exact line", () =>
        {
            var key = SshKeys.Parse(SshKeys.GenerateEd25519("it's me").PublicLine)!;
            var install = SshKeys.InstallCommand(key);
            TestSuite.True(install.Contains("chmod 700 ~/.ssh", StringComparison.Ordinal) && install.Contains("chmod 600 ~/.ssh/authorized_keys", StringComparison.Ordinal) && install.Contains("grep -qF --", StringComparison.Ordinal)
                && install.Contains("it'\"'\"'s me'", StringComparison.Ordinal) && install.Contains("#present", StringComparison.Ordinal), install);
            var remove = SshKeys.RemoveCommand(key);
            TestSuite.True(remove.Contains("grep -vxF --", StringComparison.Ordinal) && remove.Contains("cat \"$t\" > \"$f\"", StringComparison.Ordinal), remove);
        });

        await t.Run("sshd: effective settings are read; findings come most important first; lock-out fixes are blocked", () =>
        {
            var s = Sshd.Parse(Effective);
            TestSuite.True(s.Problem is null && s.Effective && s.IncludesDropIns && s.User == "deploy" && !s.IsRoot && s.Get("permitrootlogin") == "yes" && s.Ours["MaxAuthTries"] == "5", "parsed");
            var byPassword = Sshd.Audit(s, AuthenticationKind.Password, 1);
            var password = byPassword.Single(f => f.Id == "password");
            TestSuite.True(byPassword[0].Id == "password" && password.Blocked!.Contains("Вход по ключу", StringComparison.Ordinal) && password.Fix!["PasswordAuthentication"] == "no"
                && byPassword.Single(f => f.Id == "root").Fix!["PermitRootLogin"] == "no" && byPassword.Any(f => f.Id == "tries") && byPassword.Any(f => f.Id == "x11") && byPassword.Any(f => f.Id == "port" && f.Fix is null), string.Join(", ", byPassword.Select(f => f.Id)));
            var byKey = Sshd.Audit(s, AuthenticationKind.PrivateKey, 1);
            TestSuite.True(byKey.Single(f => f.Id == "password").Blocked is null && Sshd.Audit(s, AuthenticationKind.Agent, 0).Single(f => f.Id == "password").Blocked!.Contains("нет ни одного ключа", StringComparison.Ordinal), "with a key the fix is allowed; without keys on the server it is not");
            var root = Sshd.Parse(Effective.Replace("#user deploy 1000", "#user root 0"));
            TestSuite.True(Sshd.Audit(root, AuthenticationKind.PrivateKey, 1).Single(f => f.Id == "root").Fix!["PermitRootLogin"] == "prohibit-password" && Sshd.Audit(root, AuthenticationKind.Password, 1).Single(f => f.Id == "root").Blocked is not null, "root keeps key login");
            var hardened = Sshd.Parse("#user u 1\n#effective\nport 2200\npermitrootlogin prohibit-password\npasswordauthentication no\nkbdinteractiveauthentication no\nmaxauthtries 3\nlogingracetime 30\nx11forwarding no\nallowusers u\n#include\nyes\n#ours\n#end");
            TestSuite.True(Sshd.Audit(hardened, AuthenticationKind.PrivateKey, 1).Count == 0, string.Join(", ", Sshd.Audit(hardened, AuthenticationKind.PrivateKey, 1).Select(f => f.Id)));
        });

        await t.Run("sshd: without root the files are read like sshd — first value wins, Match blocks are someone else's", () =>
        {
            var s = Sshd.Parse("#user u 1000\n#noeffective sshd: no hostkeys available -- exiting.\n#files\nPasswordAuthentication no\nPasswordAuthentication yes\nMatch User backup\n  PasswordAuthentication yes\n  PermitRootLogin yes\n#include\nno\n#ours\n#end");
            TestSuite.True(s.Problem is null && !s.Effective && !s.IncludesDropIns && s.Get("passwordauthentication") == "no" && s.Get("permitrootlogin") == "", s.Get("passwordauthentication"));
            TestSuite.True(Sshd.Parse("#user u 1\n#noeffective denied\n#files\n#include\nno\n#ours\n#end").Problem!.Contains("denied", StringComparison.Ordinal), "nothing readable is a problem");
        });

        await t.Run("sshd: the safe apply writes only known settings, checks sshd -t, reloads, and can restore", () =>
        {
            var block = Sshd.Block(new Dictionary<string, string> { ["MaxAuthTries"] = "5", ["X11Forwarding"] = "no" }, new Dictionary<string, string> { ["passwordauthentication"] = "no", ["MaxAuthTries"] = "4" });
            TestSuite.True(block.Contains("MaxAuthTries 4\n", StringComparison.Ordinal) && block.Contains("PasswordAuthentication no\n", StringComparison.Ordinal) && block.Contains("X11Forwarding no\n", StringComparison.Ordinal) && !block.Contains("MaxAuthTries 5", StringComparison.Ordinal), block);
            TestSuite.Throws<ArgumentException>(() => Sshd.Block(new Dictionary<string, string>(), new Dictionary<string, string> { ["AllowUsers"] = "x" }));
            TestSuite.Throws<ArgumentException>(() => Sshd.Block(new Dictionary<string, string>(), new Dictionary<string, string> { ["MaxAuthTries"] = "4; reboot" }));
            var dropIn = Sshd.SafeApply("abc123", block, true, 90);
            var inline = Sshd.SafeApply("abc123", block, false, 90);
            TestSuite.True(dropIn.IndexOf("cp -p /etc/ssh/sshd_config", StringComparison.Ordinal) < dropIn.IndexOf("cat > '" + Sshd.DropIn + "'", StringComparison.Ordinal) && dropIn.Contains("systemd-run --quiet --unit='sshx-sshd-rollback-abc123' --on-active=90", StringComparison.Ordinal)
                && dropIn.Contains("sshd -t 2>&1 || { echo '#invalid'", StringComparison.Ordinal) && dropIn.Contains("systemctl reload", StringComparison.Ordinal) && dropIn.Contains("rm -f '" + Sshd.DropIn + "'", StringComparison.Ordinal), "drop-in order");
            TestSuite.True(inline.Contains("# SSH Explorer — begin", StringComparison.Ordinal) && inline.Contains("sed '/^# SSH Explorer — begin$/,/^# SSH Explorer — end$/d'", StringComparison.Ordinal) && !inline.Contains("cat > '" + Sshd.DropIn, StringComparison.Ordinal), "inline block at the top");
            TestSuite.Throws<ArgumentException>(() => Sshd.SafeApply("a/b", block, true, 90));
            TestSuite.Throws<ArgumentException>(() => Sshd.SafeApply("abc", "X $(reboot)\n", true, 90));
            TestSuite.True(Sshd.Confirm("abc").Contains("rm -rf '/var/tmp/sshx-sshd-abc'", StringComparison.Ordinal) && Sshd.RollbackNow("abc").Contains("rollback.sh", StringComparison.Ordinal), "confirm and restore");
        });
        await Task.CompletedTask;
    }
}
#endif
