using SSHExplorer.Core;

namespace SSHExplorer.App;

public interface IProfilePersistence
{
    Task<string?> ReadSecretAsync(Guid profileId,CancellationToken ct=default);
    Task<ProfileCommitResult> SaveAsync(ServerProfile profile,string? secret,CancellationToken ct=default);
}

public sealed class ProfilePersistence(AppStore store):IProfilePersistence
{
    public Task<string?> ReadSecretAsync(Guid profileId,CancellationToken ct=default)=>store.ReadSecretAsync(profileId,ct);
    public Task<ProfileCommitResult> SaveAsync(ServerProfile profile,string? secret,CancellationToken ct=default)=>store.SaveProfileAsync(profile,secret,ct);
}
