using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SSHExplorer.Core;

public sealed class AppStore
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public string DirectoryPath { get; }
    public List<ServerProfile> Profiles { get; private set; } = [];
    public AppSettings Settings { get; private set; } = new();
    public HostTrustStore Trust { get; }
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
                Profiles = data.Profiles; Settings = data.Settings;
            }
            catch (JsonException ex) { throw new InvalidDataException($"Повреждены настройки {path}. Сохраните копию файла и восстановите его из резервной копии .bak.", ex); }
        }
        Trust = new HostTrustStore(Path.Combine(DirectoryPath, "hostkeys.json"));
    }
    public async Task SaveAsync(CancellationToken ct = default)
    {
        await _saveGate.WaitAsync(ct).ConfigureAwait(false);
        try { await AtomicWriteAsync(Path.Combine(DirectoryPath, "settings.json"), JsonSerializer.Serialize(new StoreDocument { Profiles = Profiles, Settings = Settings }, JsonOptions), ct).ConfigureAwait(false); }
        finally { _saveGate.Release(); }
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
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", true);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private string SecretPath(Guid profileId) => Path.Combine(DirectoryPath, $"secret-{profileId:N}.dpapi");
    public async Task SaveSecretAsync(Guid profileId, string secret, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Хранилище секретов использует Windows DPAPI CurrentUser.");
        byte[] raw = Encoding.UTF8.GetBytes(secret);
        try
        {
            byte[] encrypted = ProtectedData.Protect(raw, profileId.ToByteArray(), DataProtectionScope.CurrentUser);
            await AtomicWriteAsync(SecretPath(profileId), Convert.ToBase64String(encrypted), ct).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(raw); }
    }
    public async Task<string?> ReadSecretAsync(Guid profileId, CancellationToken ct = default)
    {
        if (!File.Exists(SecretPath(profileId))) return null;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Хранилище секретов использует Windows DPAPI CurrentUser.");
        byte[] encrypted = Convert.FromBase64String(await File.ReadAllTextAsync(SecretPath(profileId), ct).ConfigureAwait(false));
        byte[] raw = ProtectedData.Unprotect(encrypted, profileId.ToByteArray(), DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(raw); }
        finally { CryptographicOperations.ZeroMemory(raw); }
    }
    public async Task DeleteProfileAsync(Guid profileId, CancellationToken ct = default)
    {
        Profiles.RemoveAll(x => x.Id == profileId);
        await SaveAsync(ct).ConfigureAwait(false);
        if (File.Exists(SecretPath(profileId))) File.Delete(SecretPath(profileId));
    }
    public void DeleteSecret(Guid profileId) { if (File.Exists(SecretPath(profileId))) File.Delete(SecretPath(profileId)); }
    private sealed class StoreDocument
    {
        public int SchemaVersion { get; set; } = 1;
        public List<ServerProfile> Profiles { get; set; } = [];
        public AppSettings Settings { get; set; } = new();
    }
}

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
