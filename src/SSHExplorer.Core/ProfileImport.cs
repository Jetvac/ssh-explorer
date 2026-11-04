namespace SSHExplorer.Core;

/// <summary>A profile proposed by an import, with what the user should know before saving it.</summary>
public sealed record ImportCandidate(ServerProfile Profile, IReadOnlyList<string> Notes, string? DuplicateOf);

public static class ProfileImport
{
    /// <summary>
    /// Maps resolved ssh_config hosts to profiles. Nothing is saved here; passwords are never part of an import.
    /// A candidate whose host, port and user match an existing profile is marked as a duplicate.
    /// </summary>
    public static IReadOnlyList<ImportCandidate> FromSshConfig(SshConfig config, string localUser, string homeDirectory,
        IReadOnlyCollection<ServerProfile> existing, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        var result = new List<ImportCandidate>();
        foreach (var alias in config.Aliases)
        {
            var host = config.Resolve(alias, localUser, homeDirectory);
            var notes = new List<string>();
            var profile = new ServerProfile
            {
                Name = alias, Host = host.HostName, Port = host.Port ?? 22, Username = host.User ?? localUser,
                TimeoutSeconds = Math.Clamp(host.ConnectTimeoutSeconds ?? 20, 3, 120), ProxyJump = host.ProxyJump ?? "",
            };
            if (host.User is null) notes.Add($"User не указан: используется имя Windows «{localUser}».");
            var key = host.IdentityFiles.FirstOrDefault(fileExists);
            if (key is not null) { profile.Authentication = AuthenticationKind.PrivateKey; profile.PrivateKeyPath = key; }
            else
            {
                profile.Authentication = AuthenticationKind.Password;
                if (host.IdentityFiles.Count > 0) notes.Add("IdentityFile не найден: " + string.Join(", ", host.IdentityFiles) + ". Выбран вход по паролю.");
            }
            if (host.IdentityFiles.Count > 1 && key is not null) notes.Add("Используется первый существующий ключ; остальные IdentityFile не переносятся.");
            if (host.ProxyJump is { Length: > 0 }) notes.Add("Подключение через " + host.ProxyJump + ".");
            if (host.Ignored.Count > 0) notes.Add("Не переносятся: " + string.Join(", ", host.Ignored) + ".");
            if (host.ConnectTimeoutSeconds is { } t && (t < 3 || t > 120)) notes.Add($"ConnectTimeout {t} с ограничен до {profile.TimeoutSeconds} с.");
            string? duplicate = existing.FirstOrDefault(p => string.Equals(p.Host, profile.Host, StringComparison.OrdinalIgnoreCase) && p.Port == profile.Port && p.Username == profile.Username)?.Name;
            try { profile.Validate(); }
            catch (Exception ex) when (ex is ArgumentException or FileNotFoundException) { notes.Add("Требует исправления: " + ex.Message); }
            result.Add(new ImportCandidate(profile, notes, duplicate));
        }
        return result;
    }
}
