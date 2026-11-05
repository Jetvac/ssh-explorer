using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SSHExplorer.Core;

public sealed class AppStore
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private Dictionary<Guid,Guid> _secretReferences = [];
    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public string DirectoryPath { get; }
    public List<ServerProfile> Profiles { get; private set; } = [];
    public AppSettings Settings { get; private set; } = new();
    public HostTrustStore Trust { get; }
    public string? LastProfileCleanupWarning { get; private set; }
    public AppStore(string? directory = null)
    {
        DirectoryPath = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SSHExplorer");
        Directory.CreateDirectory(DirectoryPath);
        var path = Path.Combine(DirectoryPath, "settings.json");
        if (File.Exists(path))
        {
            try
            {
                var data = JsonSerializer.Deserialize<StoreDocument>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException("Пустой файл настроек.");
                if (data.SchemaVersion != 1) throw new InvalidDataException($"Неизвестная версия настроек {data.SchemaVersion}.");
                Profiles = data.Profiles; Settings = data.Settings; _secretReferences = data.SecretReferences;
            }
            catch (JsonException ex) { throw new InvalidDataException($"Повреждены настройки {path}. Сохраните копию файла и восстановите его из резервной копии .bak.", ex); }
        }
        Trust = new HostTrustStore(Path.Combine(DirectoryPath, "hostkeys.json"));
    }
    public async Task SaveAsync(CancellationToken ct = default)
    {
        await _saveGate.WaitAsync(ct).ConfigureAwait(false);
        try { await WriteSettingsAsync(Profiles,_secretReferences,ct).ConfigureAwait(false); }
        finally { _saveGate.Release(); }
    }
    private Task WriteSettingsAsync(List<ServerProfile> profiles,Dictionary<Guid,Guid> references,CancellationToken ct) =>
        AtomicWriteAsync(Path.Combine(DirectoryPath,"settings.json"),JsonSerializer.Serialize(new StoreDocument{Profiles=profiles,Settings=Settings,SecretReferences=references},JsonOptions),ct);
    /// <summary>
    /// Commits a profile and its optional encrypted secret as one visible transaction.
    /// A new secret is staged under an immutable random reference; settings.json is the
    /// commit point. Until it succeeds neither Profiles nor the old secret reference changes.
    /// </summary>
    public async Task<ProfileCommitResult> SaveProfileAsync(ServerProfile profile,string? secret,CancellationToken ct=default,bool deferSecretUntilAuthentication=false)
    {
        profile.Validate();
        if(profile.TimeoutSeconds is <3 or >120)throw new ArgumentException("Таймаут подключения должен быть от 3 до 120 секунд.");
        await _saveGate.WaitAsync(ct).ConfigureAwait(false);
        string? stagedPath=null;
        bool committed=false;
        try
        {
            var profiles=Profiles.ToList();int index=profiles.FindIndex(x=>x.Id==profile.Id);
            if(index<0)profiles.Add(profile);else profiles[index]=profile;
            var references=new Dictionary<Guid,Guid>(_secretReferences);
            string oldSecretPath=SecretPath(profile.Id);
            if(profile.SaveSecret)
            {
                if(secret is null)
                {
                    if(!File.Exists(oldSecretPath)&&!deferSecretUntilAuthentication)throw new InvalidOperationException("Введите секрет перед включением его сохранения.");
                }
                else
                {
                    Guid reference=Guid.NewGuid();stagedPath=SecretFilePath(reference);
                    string protectedSecret=ProtectSecret(profile.Id,secret);
                    await AtomicWriteAsync(stagedPath,protectedSecret,ct).ConfigureAwait(false);
                    references[profile.Id]=reference;
                }
            }
            else references.Remove(profile.Id);
            await WriteSettingsAsync(profiles,references,ct).ConfigureAwait(false);
            Profiles=profiles;_secretReferences=references;
            committed=true;LastProfileCleanupWarning=null;
            string? cleanupWarning=null;
            string? activePath=profile.SaveSecret?SecretPath(profile.Id):null;
            if(oldSecretPath!=activePath&&File.Exists(oldSecretPath))
            {
                try{File.Delete(oldSecretPath);if(File.Exists(oldSecretPath+".bak"))File.Delete(oldSecretPath+".bak");}
                catch(Exception ex)when(ex is IOException or UnauthorizedAccessException)
                {cleanupWarning="Профиль сохранён, но не удалось удалить прежнюю защищённую копию секрета. Новые подключения её не используют. Проверьте права папки настроек.";}
            }
            LastProfileCleanupWarning=cleanupWarning;
            return new(profile,cleanupWarning);
        }
        catch
        {
            // Only our unreferenced staged secret may be removed. The committed settings
            // and old encrypted secret remain intact on encryption/write/replace failure.
            if(!committed&&stagedPath is not null&&File.Exists(stagedPath))
                try{File.Delete(stagedPath);}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){}
            throw;
        }
        finally{_saveGate.Release();}
    }
    internal static async Task AtomicWriteAsync(string path, string text, CancellationToken ct)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var data = Encoding.UTF8.GetBytes(text);
                await stream.WriteAsync(data, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(true);
            }
            ct.ThrowIfCancellationRequested();
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", true);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private string SecretFilePath(Guid reference) => Path.Combine(DirectoryPath, $"secret-{reference:N}.dpapi");
    private string SecretPath(Guid profileId) => SecretFilePath(_secretReferences.GetValueOrDefault(profileId,profileId));
    private static string ProtectSecret(Guid profileId,string secret)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Хранилище секретов использует Windows DPAPI CurrentUser.");
        byte[] raw=Encoding.UTF8.GetBytes(secret);
        try{return Convert.ToBase64String(ProtectedData.Protect(raw,profileId.ToByteArray(),DataProtectionScope.CurrentUser));}
        finally{CryptographicOperations.ZeroMemory(raw);}
    }
    public async Task SaveSecretAsync(Guid profileId, string secret, CancellationToken ct = default)
    {
        string encrypted=ProtectSecret(profileId,secret);
        await _saveGate.WaitAsync(ct).ConfigureAwait(false);
        try{await AtomicWriteAsync(SecretPath(profileId),encrypted,ct).ConfigureAwait(false);}
        finally{_saveGate.Release();}
    }
    /// <summary>Commits credentials only after a successful attempt and only to its unchanged, opted-in profile.</summary>
    public async Task<bool> SaveAuthenticatedSecretAsync(ServerProfile authenticatedProfile,string secret,CancellationToken ct=default)
    {
        await _saveGate.WaitAsync(ct).ConfigureAwait(false);
        string? staged=null;bool committed=false;
        try
        {
            var current=Profiles.FirstOrDefault(p=>p.Id==authenticatedProfile.Id);
            if(current is null||!current.SaveSecret||!SameAuthenticationTarget(current,authenticatedProfile))return false;
            string oldPath=SecretPath(current.Id);Guid reference=Guid.NewGuid();staged=SecretFilePath(reference);
            await AtomicWriteAsync(staged,ProtectSecret(current.Id,secret),ct).ConfigureAwait(false);
            var references=new Dictionary<Guid,Guid>(_secretReferences){[current.Id]=reference};
            await WriteSettingsAsync(Profiles,references,ct).ConfigureAwait(false);
            _secretReferences=references;committed=true;LastProfileCleanupWarning=null;
            try{if(File.Exists(oldPath))File.Delete(oldPath);if(File.Exists(oldPath+".bak"))File.Delete(oldPath+".bak");}
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException)
            {LastProfileCleanupWarning="Новый секрет сохранён, но не удалось удалить прежнюю защищённую копию. Новые подключения её не используют.";}
            return true;
        }
        finally
        {
            if(!committed&&staged is not null&&File.Exists(staged))try{File.Delete(staged);}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){}
            _saveGate.Release();
        }
    }
    private static bool SameAuthenticationTarget(ServerProfile a,ServerProfile b)=>
        HostTrustStore.NormalizeHost(a.Host)==HostTrustStore.NormalizeHost(b.Host)&&a.Port==b.Port&&a.Username==b.Username&&a.Authentication==b.Authentication&&
        (a.Authentication!=AuthenticationKind.PrivateKey||string.Equals(a.PrivateKeyPath,b.PrivateKeyPath,StringComparison.OrdinalIgnoreCase));
    public async Task<string?> ReadSecretAsync(Guid profileId, CancellationToken ct = default)
    {
        await _saveGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if(Profiles.FirstOrDefault(x=>x.Id==profileId) is {SaveSecret:false})return null;
            string path=SecretPath(profileId);if(!File.Exists(path))return null;
            if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Хранилище секретов использует Windows DPAPI CurrentUser.");
            byte[] encrypted=Convert.FromBase64String(await File.ReadAllTextAsync(path,ct).ConfigureAwait(false));
            byte[] raw=ProtectedData.Unprotect(encrypted,profileId.ToByteArray(),DataProtectionScope.CurrentUser);
            try{return Encoding.UTF8.GetString(raw);}finally{CryptographicOperations.ZeroMemory(raw);}
        }
        finally{_saveGate.Release();}
    }
    public async Task DeleteProfileAsync(Guid profileId, CancellationToken ct = default)
    {
        await _saveGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var profiles=Profiles.Where(x=>x.Id!=profileId).ToList();
            var references=new Dictionary<Guid,Guid>(_secretReferences);references.Remove(profileId);
            string secret=SecretPath(profileId);
            await WriteSettingsAsync(profiles,references,ct).ConfigureAwait(false);
            Profiles=profiles;_secretReferences=references;
            LastProfileCleanupWarning=null;
            try{if(File.Exists(secret))File.Delete(secret);if(File.Exists(secret+".bak"))File.Delete(secret+".bak");}
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException)
            {LastProfileCleanupWarning="Профиль удалён, но не удалось удалить защищённую копию секрета. Проверьте права папки настроек.";}
        }
        finally{_saveGate.Release();}
    }
    public void DeleteSecret(Guid profileId) { if (File.Exists(SecretPath(profileId))) File.Delete(SecretPath(profileId)); }
    private sealed class StoreDocument
    {
        public int SchemaVersion { get; set; } = 1;
        public List<ServerProfile> Profiles { get; set; } = [];
        public AppSettings Settings { get; set; } = new();
        public Dictionary<Guid,Guid> SecretReferences { get; set; } = [];
    }
}

public sealed record ProfileCommitResult(ServerProfile Profile,string? CleanupWarning=null);

public sealed class TrustedHostKey
{
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string Algorithm { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public DateTime TrustedAtUtc { get; set; }
}
public sealed class HostTrustStore
{
    private readonly string _path;
    private readonly List<TrustedHostKey> _records;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public IReadOnlyList<TrustedHostKey> Records { get { lock (_records) return _records.ToArray(); } }
    internal HostTrustStore(string path)
    {
        _path = path;
        _records = File.Exists(path) ? JsonSerializer.Deserialize<List<TrustedHostKey>>(File.ReadAllText(path), AppStore.JsonOptions) ?? [] : [];
    }
    public static string NormalizeHost(string host) => host.Trim().Trim('[', ']').TrimEnd('.').ToLowerInvariant();
    public static string Fingerprint(byte[] publicKey) => "SHA256:" + Convert.ToBase64String(SHA256.HashData(publicKey)).TrimEnd('=');
    internal async Task<bool> VerifyAsync(string host, int port, string algorithm, byte[] publicKey, HashSet<string> sessionTrusted, Func<HostKeyPrompt, Task<TrustDecision>> prompt, CancellationToken ct)
    {
        host = NormalizeHost(host);
        string key = Convert.ToBase64String(publicKey);
        string identity = $"{host}\0{port}\0{algorithm}\0{key}";
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var prior = _records.Where(x => x.Host == host && x.Port == port).ToArray();
            var exact = prior.FirstOrDefault(x => x.Algorithm == algorithm);
            if (exact is not null && exact.PublicKey == key) return true;
            string? temporaryPrior;
            lock (sessionTrusted)
            {
                if(prior.Length==0&&sessionTrusted.Contains(identity))return true;
                temporaryPrior=sessionTrusted.FirstOrDefault(x=>x.StartsWith($"{host}\0{port}\0",StringComparison.Ordinal));
            }
            string? temporaryFingerprint=temporaryPrior is null?null:Fingerprint(Convert.FromBase64String(temporaryPrior.Split('\0')[3]));
            var info = new HostKeyPrompt(host, port, algorithm, Fingerprint(publicKey), exact?.Fingerprint ?? prior.FirstOrDefault()?.Fingerprint ?? temporaryFingerprint, prior.Length > 0||temporaryPrior is not null);
            if (info.Changed) throw new HostKeyChangedException(info);
            var decision = await prompt(info).WaitAsync(ct).ConfigureAwait(false);
            if (decision == TrustDecision.Reject) return false;
            if (decision == TrustDecision.Permanent)
            {
                var record = new TrustedHostKey { Host = host, Port = port, Algorithm = algorithm, PublicKey = key, Fingerprint = info.Fingerprint, TrustedAtUtc = DateTime.UtcNow };
                lock (_records) _records.Add(record);
                try { await PersistAsync(ct).ConfigureAwait(false); }
                catch { lock (_records) _records.Remove(record); throw; }
            }
            lock (sessionTrusted) sessionTrusted.Add(identity);
            return true;
        }
        finally { _gate.Release(); }
    }
    private Task PersistAsync(CancellationToken ct) => AppStore.AtomicWriteAsync(_path, JsonSerializer.Serialize(Records, AppStore.JsonOptions), ct);
    /// <summary>
    /// Adds trusted keys from an export file. A key never replaces a different one already trusted for the same
    /// host, port and algorithm: such records are returned as conflicts and the stored key stays.
    /// </summary>
    public async Task<(int Added, IReadOnlyList<TrustedHostKey> Conflicts)> ImportAsync(IEnumerable<TrustedHostKey> incoming, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var added = new List<TrustedHostKey>(); var conflicts = new List<TrustedHostKey>();
            lock (_records)
                foreach (var r in incoming)
                {
                    if (r.PublicKey.Length == 0 || r.Algorithm.Length == 0 || r.Port is < 1 or > 65535) continue;
                    var host = NormalizeHost(r.Host);
                    byte[] raw; try { raw = Convert.FromBase64String(r.PublicKey); } catch (FormatException) { continue; }
                    var same = _records.FirstOrDefault(x => x.Host == host && x.Port == r.Port && x.Algorithm == r.Algorithm);
                    if (same is not null) { if (same.PublicKey != r.PublicKey) conflicts.Add(r); continue; }
                    var record = new TrustedHostKey { Host = host, Port = r.Port, Algorithm = r.Algorithm, PublicKey = r.PublicKey, Fingerprint = Fingerprint(raw), TrustedAtUtc = DateTime.UtcNow };
                    _records.Add(record); added.Add(record);
                }
            if (added.Count > 0)
                try { await PersistAsync(ct).ConfigureAwait(false); }
                catch { lock (_records) _records.RemoveAll(added.Contains); throw; }
            return (added.Count, conflicts);
        }
        finally { _gate.Release(); }
    }
    // Calling this is an explicit trust reset. UI must show fingerprint and require a separate confirmation.
    public async Task ForgetAsync(string host, int port, string algorithm, CancellationToken ct = default)
    {
        host = NormalizeHost(host);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            TrustedHostKey[] removed;
            lock(_records){removed=_records.Where(x=>x.Host==host&&x.Port==port&&x.Algorithm==algorithm).ToArray();_records.RemoveAll(x=>removed.Contains(x));}
            try{await PersistAsync(ct).ConfigureAwait(false);}
            catch{lock(_records)_records.AddRange(removed);throw;}
        }
        finally { _gate.Release(); }
    }
}
