using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace SSHExplorer.Core;

/// <summary>What an mRemoteNG file gives: profiles, folder defaults, and (only when asked) decrypted passwords.</summary>
public sealed record MRemoteNgImport(
    IReadOnlyList<ImportCandidate> Candidates, IReadOnlyList<string> Skipped,
    IReadOnlyList<FolderDefaults> Folders, IReadOnlyDictionary<Guid, string> Secrets, IReadOnlyDictionary<Guid, string> FolderSecrets,
    bool HasPasswords, bool NeedsPassword, bool WrongPassword);

/// <summary>
/// mRemoteNG <c>confCons.xml</c>. SSH and RDP connections become profiles, folders become Home groups. A server that
/// inherits user, domain or password from its folder keeps inheriting (the folder gets those values), as in mRemoteNG.
/// Passwords are decrypted only when the caller asks and gives the mRemoteNG password (empty — mRemoteNG's built-in
/// one, used when no own password was set); a fully encrypted file opens the same way.
/// </summary>
public static class MRemoteNg
{
    public const string DefaultPassword = "mR3m";

    public static MRemoteNgImport Read(string xml, string localUser, IReadOnlyCollection<ServerProfile> existing, bool importPasswords = false, string? password = null, Func<string, bool>? fileExists = null)
    {
        var candidates = new List<ImportCandidate>(); var skipped = new List<string>(); var folders = new List<FolderDefaults>();
        var secrets = new Dictionary<Guid, string>(); var folderSecrets = new Dictionary<Guid, string>();
        MRemoteNgImport Done(bool hasPasswords = false, bool needs = false, bool wrong = false) => new(candidates, skipped, folders, secrets, folderSecrets, hasPasswords, needs, wrong);
        XDocument doc;
        try { doc = Load(xml); }
        catch (XmlException ex) { skipped.Add("Файл не похож на confCons.xml mRemoteNG: " + ex.Message); return Done(); }
        var root = doc.Root;
        if (root is null || root.Name.LocalName != "Connections") { skipped.Add("Файл не похож на confCons.xml mRemoteNG: нет элемента Connections."); return Done(); }
        var cipher = Cipher.From(root);
        // Which password opens the file: the given one, else the built-in one. «Protected» tells whether it is right.
        var key = string.IsNullOrEmpty(password) ? DefaultPassword : password;
        bool keyWorks = cipher.Check(root, key);
        bool fullFile = string.Equals((string?)root.Attribute("FullFileEncryption"), "true", StringComparison.OrdinalIgnoreCase);
        if (fullFile)
        {
            string FullFileProblem() => string.IsNullOrEmpty(password)
                ? "Файл mRemoteNG зашифрован целиком своим паролем. Введите пароль mRemoteNG, чтобы открыть его (или в mRemoteNG снимите «Шифровать весь файл» и сохраните)."
                : "Пароль mRemoteNG не подходит к этому файлу.";
            if (!keyWorks) { skipped.Add(FullFileProblem()); return Done(needs: true, wrong: !string.IsNullOrEmpty(password)); }
            try
            {
                var inner = cipher.Decrypt(root.Value.Trim(), key);
                var wrapped = Load("<Connections>" + inner + "</Connections>").Root!;
                root.RemoveNodes(); root.Add(wrapped.Elements());
            }
            catch (Exception ex) when (ex is CryptographicException or InvalidCipherTextException or FormatException or ArgumentException)
            { skipped.Add(FullFileProblem()); return Done(needs: true, wrong: !string.IsNullOrEmpty(password)); }
            catch (XmlException ex) { skipped.Add("Расшифрованный файл mRemoteNG повреждён: " + ex.Message); return Done(); }
        }
        bool hasPasswords = root.Descendants().Any(e => e.Name.LocalName == "Node" && A(e, "Password") is not null);
        bool decrypt = importPasswords && hasPasswords && keyWorks;
        string? Secret(XElement e)
        {
            if (!decrypt || A(e, "Password") is not { } value) return null;
            try { var plain = cipher.Decrypt(value, key); return plain.Length > 0 ? plain : null; }
            catch (Exception ex) when (ex is CryptographicException or InvalidCipherTextException or FormatException or ArgumentException) { return null; }
        }
        void Walk(XElement parent, string? group, Scope scope)
        {
            foreach (var node in parent.Elements().Where(e => e.Name.LocalName == "Node"))
            {
                var name = A(node, "Name") ?? "(без имени)";
                var own = scope.Child(node);
                if (string.Equals(A(node, "Type"), "Container", StringComparison.OrdinalIgnoreCase))
                {
                    var path = group is null ? name : group + "/" + name;
                    // The folder holds what its servers may inherit.
                    var folder = new FolderDefaults { Path = path, Username = Inherits(node, "Username") ? "" : A(node, "Username") ?? "", Domain = Inherits(node, "Domain") ? "" : A(node, "Domain") ?? "" };
                    if (!Inherits(node, "Password") && Secret(node) is { } folderPassword) { folder.HasPassword = true; folderSecrets[folder.Id] = folderPassword; }
                    if (folder.Username.Length > 0 || folder.Domain.Length > 0 || folder.HasPassword) folders.Add(folder);
                    Walk(node, path, own);
                    continue;
                }
                var protocol = A(node, "Protocol") ?? "";
                var label = group is null ? name : group + "/" + name;
                var notes = new List<string>();
                if (protocol.Equals("RDP", StringComparison.OrdinalIgnoreCase))
                {
                    var (host, port) = (A(node, "Hostname") ?? "", own.Port);
                    var options = RdpOptionsOf(own, notes);
                    var inherit = (Inherits(node, "Username") ? InheritFlags.Username : 0) | (Inherits(node, "Domain") ? InheritFlags.Domain : 0) | (Inherits(node, "Password") ? InheritFlags.Password : 0);
                    var user = own.Username ?? ""; var domain = options.Domain; RdpFile.SplitUser(ref user, ref domain); options.Domain = domain;
                    var candidate = RdpFile.Candidate(name, host, port, user, options, group, localUser, existing, notes, inherit);
                    if (inherit.HasFlag(InheritFlags.Password) && !decrypt && hasPasswords) notes.Add("Пароль наследуется из папки; пароли mRemoteNG не импортируются.");
                    AddSecret(node, candidate, notes);
                    candidates.Add(candidate); continue;
                }
                if (!protocol.StartsWith("SSH", StringComparison.OrdinalIgnoreCase)) { skipped.Add($"{label}: протокол {(protocol.Length > 0 ? protocol : "не указан")} — не SSH и не RDP"); continue; }
                if (protocol.Equals("SSH1", StringComparison.OrdinalIgnoreCase)) notes.Add("SSH1 устарел; подключение пойдёт по SSH2.");
                if (A(node, "PuttySession") is { } putty && !putty.Equals("Default Settings", StringComparison.OrdinalIgnoreCase)) notes.Add($"Используется сессия PuTTY «{putty}»: её ключ и параметры не переносятся — при необходимости импортируйте PuTTY отдельно.");
                var ssh = ExternalSessionImport.MapSsh(name, group, A(node, "Hostname"), own.Port is 0 ? null : own.Port, own.Username, localUser, existing, fileExists, notes);
                if (Inherits(node, "Password")) ssh.Profile.Inherit |= InheritFlags.Password;
                if (Inherits(node, "Username") && own.Username is { Length: > 0 }) ssh.Profile.Inherit |= InheritFlags.Username;
                AddSecret(node, ssh, notes);
                candidates.Add(ssh);
            }
        }
        void AddSecret(XElement node, ImportCandidate candidate, List<string> notes)
        {
            if (Inherits(node, "Password")) return;
            if (A(node, "Password") is null) return;
            if (Secret(node) is { } secret) { secrets[candidate.Profile.Id] = secret; candidate.Profile.SaveSecret = true; notes.Add("Пароль импортирован и будет сохранён с защитой Windows."); }
            else notes.Add(decrypt ? "Пароль mRemoteNG не расшифрован." : "Пароль из mRemoteNG не переносится.");
        }
        Walk(root, null, Scope.Root);
        // Inheritance needs a folder value; a folder without one keeps the copied value in the profile instead.
        foreach (var c in candidates)
        {
            var chain = ProfileInheritance.Chain(c.Profile.Group, folders);
            if (c.Profile.Inherit.HasFlag(InheritFlags.Username) && !chain.Any(f => f.Username.Length > 0)) c.Profile.Inherit &= ~InheritFlags.Username;
            if (c.Profile.Inherit.HasFlag(InheritFlags.Domain) && !chain.Any(f => f.Domain.Length > 0)) c.Profile.Inherit &= ~InheritFlags.Domain;
            if (c.Profile.Inherit.HasFlag(InheritFlags.Password) && !chain.Any(f => f.HasPassword)) c.Profile.Inherit &= ~InheritFlags.Password;
        }
        if (importPasswords && hasPasswords && !keyWorks) return Done(true, needs: true, wrong: !string.IsNullOrEmpty(password));
        return Done(hasPasswords);
    }

    static XDocument Load(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64 * 1024 * 1024 });
        return XDocument.Load(reader);
    }

    internal static string? A(XElement e, string name) => (string?)e.Attribute(name) is { Length: > 0 } v ? v : null;
    static bool Inherits(XElement e, string what) => string.Equals(A(e, "Inherit" + what), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Effective values of one node: its own, or its folder's when it inherits them (per attribute).</summary>
    sealed class Scope
    {
        readonly Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase);
        public static readonly Scope Root = new();
        static readonly string[] Inheritable = ["Username", "Domain", "Port", "Resolution", "Colors", "RedirectDiskDrives", "RedirectPrinters", "RedirectClipboard", "RedirectSmartCards", "RedirectSound", "RedirectAudioCapture",
            "RDPAuthenticationLevel", "UseCredSsp", "UseConsoleSession", "RDGatewayUsageMethod", "RDGatewayHostname", "RDGatewayUseConnectionCredentials", "RDGatewayUsername", "AutomaticResize"];
        public Scope Child(XElement node)
        {
            var child = new Scope();
            foreach (var name in Inheritable)
            {
                // InheritRedirectDiskDrives etc.; mRemoteNG names the port flag InheritPort.
                bool inherit = Inherits(node, name);
                child.values[name] = inherit ? values.GetValueOrDefault(name) : A(node, name) ?? (node.Attribute(name) is null ? values.GetValueOrDefault(name) : null);
            }
            return child;
        }
        public string? this[string name] => values.GetValueOrDefault(name);
        public string? Username => this["Username"];
        public int? Port => int.TryParse(this["Port"], NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is >= 1 and <= 65535 ? p : null;
        public bool Flag(string name, bool fallback) => this[name] is { } v ? v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("All", StringComparison.OrdinalIgnoreCase) || v.Equals("Local", StringComparison.OrdinalIgnoreCase) : fallback;
    }

    static RdpOptions RdpOptionsOf(Scope s, List<string> notes)
    {
        var o = new RdpOptions
        {
            Domain = s["Domain"] ?? "",
            Clipboard = s.Flag("RedirectClipboard", true), Drives = s.Flag("RedirectDiskDrives", false), Printers = s.Flag("RedirectPrinters", false), SmartCards = s.Flag("RedirectSmartCards", false),
            Microphone = s.Flag("RedirectAudioCapture", false), AdminSession = s.Flag("UseConsoleSession", false), Nla = s.Flag("UseCredSsp", true),
            Audio = (s["RedirectSound"] ?? "").ToLowerInvariant() switch { "leaveatremotecomputer" => RdpAudio.RemoteComputer, "donotplay" => RdpAudio.Off, _ => RdpAudio.ThisComputer },
            ColorDepth = (s["Colors"] ?? "") switch { "Colors15Bit" => 15, "Colors16Bit" => 16, "Colors24Bit" => 24, _ => 32 },
        };
        if (Regex.Match(s["Resolution"] ?? "", @"^Res(\d+)x(\d+)$") is { Success: true } m && int.TryParse(m.Groups[1].Value, out var w) && int.TryParse(m.Groups[2].Value, out var h) && w >= 640 && h >= 480 && w <= 8192 && h <= 8192)
        { o.Width = w; o.Height = h; }
        switch ((s["RDGatewayUsageMethod"] ?? "Never").ToLowerInvariant())
        {
            case "always": o.GatewayMode = RdpGatewayMode.Always; break;
            case "detect": o.GatewayMode = RdpGatewayMode.Detect; break;
        }
        if (o.GatewayMode != RdpGatewayMode.Never)
        {
            o.GatewayHost = s["RDGatewayHostname"] ?? "";
            o.GatewaySameCredentials = !string.Equals(s["RDGatewayUseConnectionCredentials"], "No", StringComparison.OrdinalIgnoreCase);
            if (!o.GatewaySameCredentials) { o.GatewayUsername = s["RDGatewayUsername"] ?? ""; notes.Add("Пароль шлюза не переносится: Windows запросит его при подключении."); }
        }
        if (string.Equals(s["RDPAuthenticationLevel"], "AuthRequired", StringComparison.OrdinalIgnoreCase) && !o.Nla) notes.Add("Сертификат сервера проверит Windows (NLA выключена).");
        return o;
    }

    /// <summary>mRemoteNG's cipher: AES/Serpent/Twofish in GCM/CCM/EAX with PBKDF2-SHA1 keys (2.6+), or the legacy AES-CBC with an MD5 key.</summary>
    sealed class Cipher
    {
        string engine = "AES", mode = "GCM"; int iterations = 1000; bool legacy;
        public static Cipher From(XElement root)
        {
            var c = new Cipher();
            if (A(root, "EncryptionEngine") is { } e) c.engine = e; else c.legacy = true;
            if (A(root, "BlockCipherMode") is { } m) c.mode = m;
            if (int.TryParse(A(root, "KdfIterations"), NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i is >= 1000 and <= 50_000_000) c.iterations = i;
            return c;
        }

        /// <summary>«Protected» holds «ThisIsProtected» or «ThisIsNotProtected» encrypted with the file password.</summary>
        public bool Check(XElement root, string password)
        {
            if (A(root, "Protected") is not { } marker) return true;
            try { return Decrypt(marker, password) is "ThisIsProtected" or "ThisIsNotProtected"; }
            catch (Exception ex) when (ex is CryptographicException or InvalidCipherTextException or FormatException or ArgumentException) { return false; }
        }

        public string Decrypt(string base64, string password)
        {
            var data = Convert.FromBase64String(base64.Trim());
            if (legacy)
            {
                if (data.Length < 32) throw new CryptographicException("Слишком короткие данные.");
                using var aes = Aes.Create(); aes.Key = MD5.HashData(Encoding.UTF8.GetBytes(password)); aes.IV = data[..16];
                return Encoding.UTF8.GetString(aes.DecryptCbc(data[16..], data[..16], PaddingMode.PKCS7));
            }
            const int salt = 16, nonce = 16, mac = 128;
            if (data.Length < salt + nonce + mac / 8) throw new CryptographicException("Слишком короткие данные.");
            var generator = new Pkcs5S2ParametersGenerator();
            generator.Init(PbeParametersGenerator.Pkcs5PasswordToBytes(password.ToCharArray()), data[..salt], iterations);
            var key = (KeyParameter)generator.GenerateDerivedMacParameters(256);
            IBlockCipher block = engine.ToUpperInvariant() switch { "SERPENT" => new SerpentEngine(), "TWOFISH" => new TwofishEngine(), _ => new AesEngine() };
            IAeadBlockCipher aead = mode.ToUpperInvariant() switch { "CCM" => new CcmBlockCipher(block), "EAX" => new EaxBlockCipher(block), _ => new GcmBlockCipher(block) };
            aead.Init(false, new AeadParameters(key, mac, data[salt..(salt + nonce)], data[..salt]));
            var cipherText = data[(salt + nonce)..];
            var plain = new byte[aead.GetOutputSize(cipherText.Length)];
            int length = aead.ProcessBytes(cipherText, 0, cipherText.Length, plain, 0);
            length += aead.DoFinal(plain, length);
            return Encoding.UTF8.GetString(plain, 0, length);
        }

        /// <summary>The same scheme for encryption — used by tests to build files as mRemoteNG writes them.</summary>
        public static string Encrypt(string plain, string password, int iterations, byte[] salt, byte[] nonce, string engine = "AES", string mode = "GCM")
        {
            var generator = new Pkcs5S2ParametersGenerator();
            generator.Init(PbeParametersGenerator.Pkcs5PasswordToBytes(password.ToCharArray()), salt, iterations);
            var key = (KeyParameter)generator.GenerateDerivedMacParameters(256);
            IBlockCipher block = engine.ToUpperInvariant() switch { "SERPENT" => new SerpentEngine(), "TWOFISH" => new TwofishEngine(), _ => new AesEngine() };
            IAeadBlockCipher aead = mode.ToUpperInvariant() switch { "CCM" => new CcmBlockCipher(block), "EAX" => new EaxBlockCipher(block), _ => new GcmBlockCipher(block) };
            aead.Init(true, new AeadParameters(key, 128, nonce, salt));
            var input = Encoding.UTF8.GetBytes(plain); var output = new byte[aead.GetOutputSize(input.Length)];
            int length = aead.ProcessBytes(input, 0, input.Length, output, 0); length += aead.DoFinal(output, length);
            return Convert.ToBase64String([.. salt, .. nonce, .. output[..length]]);
        }
    }

    /// <summary>Test hook: encrypts as mRemoteNG 2.6+ does.</summary>
    public static string EncryptForTest(string plain, string password, int iterations = 1000, string engine = "AES", string mode = "GCM")
        => Cipher.Encrypt(plain, password, iterations, RandomNumberGenerator.GetBytes(16), RandomNumberGenerator.GetBytes(16), engine, mode);
}
