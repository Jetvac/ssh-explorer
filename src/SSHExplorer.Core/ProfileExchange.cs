using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SSHExplorer.Core;

/// <summary>What goes into an export file besides the selected servers.</summary>
public sealed record ExportOptions(bool IncludeSecrets = false, string? Passphrase = null, bool IncludeHostKeys = false, bool IncludeSidebar = false, bool IncludeRuleSets = false);

/// <summary>An <c>.sshx</c> file: servers with their tunnels, scripts and Docker templates, and optional extras.</summary>
public sealed class ExportDocument
{
    public string Format { get; set; } = ProfileExchange.FormatName;
    public int Version { get; set; } = ProfileExchange.FormatVersion;
    public DateTime CreatedUtc { get; set; }
    public string Application { get; set; } = "";
    /// <summary>Present when secrets are sealed with a passphrase; null — the file holds no secrets.</summary>
    public ExportSeal? Seal { get; set; }
    public List<ExportedServer> Servers { get; set; } = [];
    public List<TrustedHostKey>? HostKeys { get; set; }
    public ExportedSidebar? Sidebar { get; set; }
    public List<FirewallRuleSet>? RuleSets { get; set; }
    /// <summary>Home folder values the exported servers inherit (user, domain, RDP options); folder passwords stay here.</summary>
    public List<FolderDefaults>? Folders { get; set; }
}

public sealed class ExportSeal
{
    public string Kdf { get; set; } = "PBKDF2-SHA256";
    public int Iterations { get; set; }
    public string Salt { get; set; } = "";
    /// <summary>A known text sealed with the key: tells a wrong passphrase apart before anything is imported.</summary>
    public string Check { get; set; } = "";
}

public sealed class ExportedServer
{
    public ServerProfile Profile { get; set; } = new();
    /// <summary>The saved password or key passphrase, sealed with the export passphrase; null — not exported.</summary>
    public string? Secret { get; set; }
}

public sealed class ExportedSidebar
{
    public List<string> Order { get; set; } = [];
    public List<string> Hidden { get; set; } = [];
    public bool Docker { get; set; }
    public bool Scripts { get; set; }
}

/// <summary>A server proposed by an import of an <c>.sshx</c> file.</summary>
public sealed record ExchangeCandidate(ServerProfile Profile, string? Secret, IReadOnlyList<string> Notes, ServerProfile? Existing)
{
    public bool NeedsFix => Notes.Any(n => n.StartsWith("Требует исправления", StringComparison.Ordinal));
}

/// <summary>How a server that already exists here is imported.</summary>
public enum ConflictChoice { Skip, Replace, KeepBoth }

/// <summary>
/// Export and import of servers between computers. Secrets protected by Windows (DPAPI) only open for the same
/// Windows account, so they are either left out (the default; the file is safe to share) or re-sealed with a
/// passphrase (PBKDF2-SHA256, 600 000 rounds → AES-256-GCM). Private key files are never included, only their path.
/// Nothing here writes to the store: the caller previews the plan and commits the accepted rows.
/// </summary>
public static class ProfileExchange
{
    public const string FormatName = "sshexplorer-export", Extension = ".sshx";
    public const int FormatVersion = 1, Iterations = 600_000, MinPassphrase = 8;
    const string CheckText = "sshexplorer-export-check";
    static readonly byte[] Associated = Encoding.UTF8.GetBytes("sshexplorer-export-v1");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() }, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static string? ValidatePassphrase(string passphrase, string repeat) =>
        passphrase.Length < MinPassphrase ? $"Парольная фраза — не короче {MinPassphrase} символов." : passphrase != repeat ? "Парольные фразы не совпадают." : null;

    /// <summary>Builds the file contents. <paramref name="readSecret"/> returns a profile's saved secret (or null).</summary>
    public static async Task<ExportDocument> BuildAsync(IReadOnlyList<ServerProfile> profiles, ExportOptions options, string application,
        Func<Guid, Task<string?>> readSecret, AppSettings settings, IReadOnlyList<TrustedHostKey> trust, Func<string, string>? unprotectDocker = null)
    {
        if (options.IncludeSecrets && (options.Passphrase is null || options.Passphrase.Length < MinPassphrase)) throw new ArgumentException($"Для секретов нужна парольная фраза не короче {MinPassphrase} символов.");
        unprotectDocker ??= DockerSecrets.Unprotect;
        byte[]? key = null; var doc = new ExportDocument { CreatedUtc = DateTime.UtcNow, Application = application };
        try
        {
            if (options.IncludeSecrets)
            {
                var salt = RandomNumberGenerator.GetBytes(16); key = Derive(options.Passphrase!, salt, Iterations);
                doc.Seal = new ExportSeal { Iterations = Iterations, Salt = Convert.ToBase64String(salt), Check = Seal(key, CheckText) };
            }
            foreach (var source in profiles)
            {
                var profile = Copy(source);
                foreach (var script in profile.Scripts) { script.LastRunUtc = null; script.LastExitCode = null; }
                foreach (var env in profile.DockerTemplates.SelectMany(t => t.Env).Where(e => e.Secret))
                {
                    string? plain = null;
                    if (key is not null && env.Protected.Length > 0) { try { plain = unprotectDocker(env.Protected); } catch (CryptographicException) { } }
                    env.Value = ""; env.Protected = plain is null ? "" : Seal(key!, plain);
                }
                string? secret = null;
                if (key is not null && source.SaveSecret && await readSecret(source.Id).ConfigureAwait(false) is { } plainSecret) secret = Seal(key, plainSecret);
                profile.SaveSecret = false;
                doc.Servers.Add(new ExportedServer { Profile = profile, Secret = secret });
            }
            if (options.IncludeHostKeys)
            {
                // Only the keys of the exported servers.
                var endpoints = profiles.Select(p => (HostTrustStore.NormalizeHost(p.Host), p.Port)).ToHashSet();
                doc.HostKeys = trust.Where(r => endpoints.Contains((r.Host, r.Port))).ToList();
            }
            if (options.IncludeSidebar) doc.Sidebar = new ExportedSidebar { Order = [.. settings.SidebarOrder], Hidden = [.. settings.SidebarHidden], Docker = settings.DockerModule, Scripts = settings.ScriptsModule };
            if (options.IncludeRuleSets) doc.RuleSets = settings.FirewallRuleSets.Select(r => r.Clone()).ToList();
            var folders = settings.Folders.Where(f => profiles.Any(p => p.Inherit != InheritFlags.None && ProfileInheritance.Chain(p.Group, [f]).Count > 0))
                .Select(f => { var c = f.Clone(); c.HasPassword = false; return c; }).ToList();
            if (folders.Count > 0) doc.Folders = folders;
            return doc;
        }
        finally { if (key is not null) CryptographicOperations.ZeroMemory(key); }
    }

    public static string Serialize(ExportDocument doc) => JsonSerializer.Serialize(doc, Json);

    public static ExportDocument Parse(string text)
    {
        ExportDocument? doc;
        try { doc = JsonSerializer.Deserialize<ExportDocument>(text, Json); }
        catch (JsonException ex) { throw new InvalidDataException("Файл повреждён или это не файл экспорта SSH Explorer: " + ex.Message, ex); }
        if (doc is null || doc.Format != FormatName) throw new InvalidDataException("Это не файл экспорта SSH Explorer.");
        if (doc.Version > FormatVersion) throw new InvalidDataException($"Файл создан более новой версией приложения (формат {doc.Version}). Обновите SSH Explorer.");
        if (doc.Seal is { } seal && (seal.Kdf != "PBKDF2-SHA256" || seal.Iterations is < 100_000 or > 10_000_000)) throw new InvalidDataException("Неизвестный способ защиты секретов в файле.");
        doc.Servers ??= [];
        foreach (var s in doc.Servers) { s.Profile ??= new(); s.Profile.Bookmarks ??= []; s.Profile.Scripts ??= []; s.Profile.DockerTemplates ??= []; s.Profile.Tunnels ??= []; }
        return doc;
    }

    public static bool HasSecrets(ExportDocument doc) => doc.Seal is not null && (doc.Servers.Any(s => s.Secret is not null) || doc.Servers.SelectMany(s => s.Profile.DockerTemplates).SelectMany(t => t.Env).Any(e => e.Secret && e.Protected.Length > 0));

    /// <summary>True when the passphrase opens the file's secrets.</summary>
    public static bool CheckPassphrase(ExportDocument doc, string passphrase)
    {
        if (doc.Seal is null) return true;
        var key = Derive(passphrase, Convert.FromBase64String(doc.Seal.Salt), doc.Seal.Iterations);
        try { return Open(key, doc.Seal.Check) == CheckText; }
        catch (CryptographicException) { return false; }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    /// <summary>
    /// Proposes each server of the file: a fresh id, secrets opened with the passphrase (or dropped without it),
    /// Docker secrets re-protected for this computer, and notes on what to check. A server whose address and user,
    /// or whose name, already exist here is matched to that profile.
    /// </summary>
    public static IReadOnlyList<ExchangeCandidate> Plan(ExportDocument doc, string? passphrase, IReadOnlyCollection<ServerProfile> existing,
        Func<string, bool>? fileExists = null, Func<string, string>? protectDocker = null)
    {
        fileExists ??= File.Exists; protectDocker ??= DockerSecrets.Protect;
        byte[]? key = null;
        if (doc.Seal is not null && passphrase is not null)
        {
            if (!CheckPassphrase(doc, passphrase)) throw new CryptographicException("Неверная парольная фраза.");
            key = Derive(passphrase, Convert.FromBase64String(doc.Seal.Salt), doc.Seal.Iterations);
        }
        try
        {
            var names = doc.Servers.Select(s => s.Profile.Name).Concat(existing.Select(p => p.Name)).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
            var result = new List<ExchangeCandidate>();
            foreach (var server in doc.Servers)
            {
                var profile = Copy(server.Profile); profile.Id = Guid.NewGuid(); profile.SaveSecret = false;
                foreach (var t in profile.DockerTemplates) t.Id = Guid.NewGuid();
                foreach (var s in profile.Scripts) s.Id = Guid.NewGuid();
                foreach (var t in profile.Tunnels) t.Id = Guid.NewGuid();
                var notes = new List<string>();
                string? secret = null;
                if (server.Secret is not null) { if (key is not null) secret = Open(key, server.Secret); else notes.Add("Сохранённый пароль не перенесён: файл открыт без парольной фразы."); }
                int lostDocker = 0;
                foreach (var env in profile.DockerTemplates.SelectMany(t => t.Env).Where(e => e.Secret))
                {
                    if (key is not null && env.Protected.Length > 0) env.Protected = protectDocker(Open(key, env.Protected));
                    else { if (env.Protected.Length > 0 || doc.Seal is null) lostDocker++; env.Protected = ""; }
                    env.Value = "";
                }
                // Commands from a file never start by themselves: automatic scripts arrive paused, exposed tunnels manual.
                int paused = 0; foreach (var script in profile.Scripts.Where(x => x.Trigger != ScriptTrigger.Manual && x.Enabled)) { script.Enabled = false; paused++; }
                if (paused > 0) notes.Add($"Автоматические скрипты ({paused}) импортированы приостановленными — проверьте и включите их в «Скриптах».");
                int manual = 0; foreach (var tunnel in profile.Tunnels.Where(x => x.StartOnConnect && Tunnels.IsExposed(x))) { tunnel.StartOnConnect = false; manual++; }
                if (manual > 0) notes.Add($"Туннели, открытые для других компьютеров ({manual}), не будут открываться при подключении сами.");
                if (lostDocker > 0) notes.Add($"Значения секретных переменных Docker не перенесены ({lostDocker}); задайте их заново.");
                if (profile.Authentication == AuthenticationKind.PrivateKey && !fileExists(profile.PrivateKeyPath)) notes.Add("Требует исправления: ключ не найден на этом компьютере — " + profile.PrivateKeyPath);
                foreach (var hop in ProxyJumpNames(profile.ProxyJump))
                    if (!names.Contains(hop) && !hop.Contains('@') && !hop.Contains('.') && !hop.Contains(':')) notes.Add($"Промежуточный сервер «{hop}» не найден ни в файле, ни среди сохранённых.");
                var match = existing.FirstOrDefault(p => HostTrustStore.NormalizeHost(p.Host) == HostTrustStore.NormalizeHost(profile.Host) && p.Port == profile.Port && p.Username == profile.Username)
                    ?? existing.FirstOrDefault(p => string.Equals(p.Name, profile.Name, StringComparison.CurrentCultureIgnoreCase));
                try { profile.Validate(); }
                catch (ArgumentException ex) { notes.Add("Требует исправления: " + ex.Message); }
                catch (FileNotFoundException) { }
                result.Add(new ExchangeCandidate(profile, secret, notes, match));
            }
            return result;
        }
        finally { if (key is not null) CryptographicOperations.ZeroMemory(key); }
    }

    /// <summary>The profile to save for a candidate: replacing keeps the existing id (and its saved secret), keeping both gets a free name.</summary>
    public static ServerProfile Resolve(ExchangeCandidate candidate, ConflictChoice choice, IReadOnlyCollection<ServerProfile> existing)
    {
        var profile = Copy(candidate.Profile);
        if (candidate.Existing is { } old && choice == ConflictChoice.Replace) { profile.Id = old.Id; profile.SaveSecret = candidate.Secret is not null || old.SaveSecret; }
        else
        {
            profile.SaveSecret = candidate.Secret is not null;
            if (existing.Any(p => string.Equals(p.Name, profile.Name, StringComparison.CurrentCultureIgnoreCase)))
            {
                var baseName = profile.Name + " (импорт)"; var name = baseName;
                for (int i = 2; existing.Any(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase)); i++) name = $"{baseName} {i}";
                profile.Name = name;
            }
        }
        return profile;
    }

    static IEnumerable<string> ProxyJumpNames(string chain) => chain.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    static ServerProfile Copy(ServerProfile p) => JsonSerializer.Deserialize<ServerProfile>(JsonSerializer.Serialize(p, Json), Json)!;

    static byte[] Derive(string passphrase, byte[] salt, int iterations) => Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, iterations, HashAlgorithmName.SHA256, 32);

    static string Seal(byte[] key, string plain)
    {
        var data = Encoding.UTF8.GetBytes(plain); var nonce = RandomNumberGenerator.GetBytes(12); var cipher = new byte[data.Length]; var tag = new byte[16];
        try { using var aes = new AesGcm(key, 16); aes.Encrypt(nonce, data, cipher, tag, Associated); return Convert.ToBase64String([.. nonce, .. tag, .. cipher]); }
        finally { CryptographicOperations.ZeroMemory(data); }
    }

    static string Open(byte[] key, string sealedText)
    {
        var all = Convert.FromBase64String(sealedText); if (all.Length < 28) throw new CryptographicException("Повреждённый секрет в файле.");
        var plain = new byte[all.Length - 28];
        try { using var aes = new AesGcm(key, 16); aes.Decrypt(all.AsSpan(0, 12), all.AsSpan(28), all.AsSpan(12, 16), plain, Associated); return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
