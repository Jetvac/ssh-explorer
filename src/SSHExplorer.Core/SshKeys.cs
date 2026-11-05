using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace SSHExplorer.Core;

/// <summary>A new key pair: the OpenSSH private key file text and the one-line public key.</summary>
public sealed record GeneratedKey(string PrivateKeyText, string PublicLine, string Fingerprint);

/// <summary>A line of <c>~/.ssh/authorized_keys</c>: what it is, where it came from, and the exact line for removal.</summary>
public sealed record AuthorizedKey(string Line, string Type, string Fingerprint, string Comment, string Options, int Bits)
{
    public string TypeText => Type switch { "ssh-ed25519" => "Ed25519", "ssh-rsa" => $"RSA {Bits}", "ecdsa-sha2-nistp256" => "ECDSA P-256", "ecdsa-sha2-nistp384" => "ECDSA P-384", "ecdsa-sha2-nistp521" => "ECDSA P-521", "sk-ssh-ed25519@openssh.com" => "Ed25519 (ключ-токен)", "sk-ecdsa-sha2-nistp256@openssh.com" => "ECDSA (ключ-токен)", _ => Type };
    public bool Weak => Type == "ssh-rsa" && Bits is > 0 and < 2048 || Type == "ssh-dss";
}

/// <summary>
/// Key login: Ed25519 key pairs made on this computer (the private key never leaves it), the public key added to
/// the server idempotently with the permissions sshd insists on, and <c>authorized_keys</c> listed and edited by
/// exact line. The commands only touch the connected user's own <c>~/.ssh</c>.
/// </summary>
public static class SshKeys
{
    static readonly string[] Types = ["ssh-ed25519", "ssh-rsa", "ecdsa-sha2-nistp256", "ecdsa-sha2-nistp384", "ecdsa-sha2-nistp521", "sk-ssh-ed25519@openssh.com", "sk-ecdsa-sha2-nistp256@openssh.com", "ssh-dss"];

    public static string? ValidateComment(string comment) => comment.Length > 100 || comment.IndexOfAny(['\n', '\r', '\0']) >= 0 ? "Комментарий: одна строка до 100 символов." : null;

    /// <summary>An unencrypted OpenSSH Ed25519 key. (A passphrase, when wanted, is added by ssh-keygen on this computer.)</summary>
    public static GeneratedKey GenerateEd25519(string comment)
    {
        if (ValidateComment(comment) is { } error) throw new ArgumentException(error);
        var generator = new Ed25519KeyPairGenerator(); generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var pair = generator.GenerateKeyPair();
        var secret = ((Ed25519PrivateKeyParameters)pair.Private).GetEncoded(); var publicKey = ((Ed25519PublicKeyParameters)pair.Public).GetEncoded();
        try
        {
            var blob = PublicBlob(publicKey);
            var line = "ssh-ed25519 " + Convert.ToBase64String(blob) + (comment.Length > 0 ? " " + comment : "");
            return new GeneratedKey(PrivateFile(secret, publicKey, blob, comment), line, HostTrustStore.Fingerprint(blob));
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    static byte[] PublicBlob(byte[] publicKey) { var w = new Writer(); w.String("ssh-ed25519"); w.Bytes(publicKey); return w.ToArray(); }

    /// <summary>openssh-key-v1 with cipher «none»: what <c>ssh-keygen -N ""</c> writes.</summary>
    static string PrivateFile(byte[] secret, byte[] publicKey, byte[] blob, string comment)
    {
        var inner = new Writer(); uint check = BinaryPrimitives.ReadUInt32BigEndian(RandomNumberGenerator.GetBytes(4));
        inner.UInt(check); inner.UInt(check); inner.String("ssh-ed25519"); inner.Bytes(publicKey); inner.Bytes([.. secret, .. publicKey]); inner.String(comment);
        for (byte pad = 1; inner.Length % 8 != 0; pad++) inner.Raw([pad]);
        var outer = new Writer(); outer.Raw(Encoding.ASCII.GetBytes("openssh-key-v1\0")); outer.String("none"); outer.String("none"); outer.String(""); outer.UInt(1); outer.Bytes(blob);
        var section = inner.ToArray(); outer.Bytes(section); CryptographicOperations.ZeroMemory(section);
        var body = Convert.ToBase64String(outer.ToArray());
        var sb = new StringBuilder("-----BEGIN OPENSSH PRIVATE KEY-----\n");
        for (int i = 0; i < body.Length; i += 70) sb.Append(body, i, Math.Min(70, body.Length - i)).Append('\n');
        return sb.Append("-----END OPENSSH PRIVATE KEY-----\n").ToString();
    }

    /// <summary>Parses one public key (an <c>.pub</c> file or an authorized_keys line, options allowed); null when it is not one.</summary>
    public static AuthorizedKey? Parse(string line)
    {
        line = line.Trim(); if (line.Length == 0 || line.StartsWith('#')) return null;
        var words = SplitOutsideQuotes(line);
        int at = words.FindIndex(w => Types.Contains(w)); if (at < 0 || at + 1 >= words.Count) return null;
        byte[] blob; try { blob = Convert.FromBase64String(words[at + 1]); } catch (FormatException) { return null; }
        var reader = new Reader(blob); if (reader.String() != words[at]) return null;
        int bits = 0;
        if (words[at] == "ssh-rsa") { reader.Bytes(); var modulus = reader.Bytes(); int lead = modulus.TakeWhile(b => b == 0).Count(); bits = (modulus.Length - lead) * 8; if (modulus.Length > lead) bits -= byte.LeadingZeroCount(modulus[lead]); }
        return new AuthorizedKey(line, words[at], HostTrustStore.Fingerprint(blob), string.Join(' ', words.Skip(at + 2)), string.Join(' ', words.Take(at)), bits);
    }

    static List<string> SplitOutsideQuotes(string line)
    {
        var words = new List<string>(); var current = new StringBuilder(); bool quoted = false;
        foreach (var c in line)
        {
            if (c == '"') quoted = !quoted;
            if (!quoted && c is ' ' or '\t') { if (current.Length > 0) { words.Add(current.ToString()); current.Clear(); } continue; }
            current.Append(c);
        }
        if (current.Length > 0) words.Add(current.ToString());
        return words;
    }

    /// <summary>Exactly one public key on one line, from a file or the clipboard.</summary>
    public static (AuthorizedKey? Key, string? Error) ParsePublic(string text)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToArray();
        if (lines.Length == 0) return (null, "Вставьте открытый ключ: строку вида «ssh-ed25519 AAAA… комментарий».");
        if (lines.Length > 1) return (null, "Нужен один открытый ключ — одна строка.");
        if (lines[0].Contains("PRIVATE KEY", StringComparison.Ordinal)) return (null, "Это закрытый ключ. Его нельзя отправлять на сервер — нужен файл .pub.");
        var key = Parse(lines[0]);
        if (key is null || key.Options.Length > 0) return (null, "Строка не похожа на открытый ключ OpenSSH.");
        return (key, key.Type == "ssh-dss" ? "Ключи DSA отключены в современных OpenSSH — создайте Ed25519." : null);
    }

    public const string ListCommand = "if [ -f ~/.ssh/authorized_keys ]; then cat ~/.ssh/authorized_keys; fi; echo '#end'";

    public static IReadOnlyList<AuthorizedKey> ParseList(string output) =>
        output.Split('\n').Select(l => l.TrimEnd('\r')).TakeWhile(l => l != "#end").Select(Parse).OfType<AuthorizedKey>().ToArray();

    /// <summary>Adds the key once; fixes the permissions sshd requires. Prints #added or #present.</summary>
    public static string InstallCommand(AuthorizedKey key)
    {
        var line = PosixPath.Quote(key.Type + " " + key.Line.Split(' ', StringSplitOptions.RemoveEmptyEntries).SkipWhile(w => w != key.Type).Skip(1).First() + (key.Comment.Length > 0 ? " " + key.Comment : ""));
        var blob = PosixPath.Quote(key.Line.Split(' ', StringSplitOptions.RemoveEmptyEntries).SkipWhile(w => w != key.Type).Skip(1).First());
        return "umask 077; mkdir -p ~/.ssh && chmod 700 ~/.ssh && touch ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys && " +
               $"if grep -qF -- {blob} ~/.ssh/authorized_keys; then echo '#present'; else " +
               "{ [ -s ~/.ssh/authorized_keys ] && [ \"$(tail -c 1 ~/.ssh/authorized_keys)\" != \"\" ] && echo; " + $"printf '%s\\n' {line}; }} >> ~/.ssh/authorized_keys && echo '#added'; fi";
    }

    /// <summary>Removes exactly this line, keeping the file (and its permissions); prints #removed or #missing.</summary>
    public static string RemoveCommand(AuthorizedKey key)
    {
        var line = PosixPath.Quote(key.Line);
        return "f=~/.ssh/authorized_keys; t=\"$f.sshx-$$\"; " +
               $"if grep -qxF -- {line} \"$f\"; then grep -vxF -- {line} \"$f\" > \"$t\"; cat \"$t\" > \"$f\" && rm -f \"$t\" && echo '#removed'; else echo '#missing'; fi";
    }

    sealed class Writer
    {
        readonly MemoryStream stream = new();
        public long Length => stream.Length;
        public void Raw(byte[] data) => stream.Write(data);
        public void UInt(uint value) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, value); stream.Write(b); }
        public void Bytes(byte[] data) { UInt((uint)data.Length); stream.Write(data); }
        public void String(string text) => Bytes(Encoding.UTF8.GetBytes(text));
        public byte[] ToArray() => stream.ToArray();
    }

    sealed class Reader(byte[] data)
    {
        int at;
        public byte[] Bytes()
        {
            if (at + 4 > data.Length) return [];
            int n = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at)); at += 4;
            if (n < 0 || at + n > data.Length) { at = data.Length; return []; }
            var r = data.AsSpan(at, n).ToArray(); at += n; return r;
        }
        public string String() => Encoding.UTF8.GetString(Bytes());
    }
}
