using SSHExplorer.Core;

namespace SSHExplorer.App;

public interface IProfilePersistence
{
    Task<string?> ReadSecretAsync(Guid profileId,CancellationToken ct=default);
    Task<ProfileCommitResult> SaveAsync(ServerProfile profile,string? secret,CancellationToken ct=default);
    Task<ProfileCommitResult> SaveForConnectionAsync(ServerProfile profile,CancellationToken ct=default)=>SaveAsync(profile,null,ct);
}

public sealed class ProfilePersistence(AppStore store,Guid? operationId=null):IProfilePersistence
{
    private readonly Guid _operationId=operationId??Guid.NewGuid();
    public Task<string?> ReadSecretAsync(Guid profileId,CancellationToken ct=default)=>store.ReadSecretAsync(profileId,ct);
    public Task<ProfileCommitResult> SaveAsync(ServerProfile profile,string? secret,CancellationToken ct=default)=>CommitAsync(profile,secret,false,ct);
    public Task<ProfileCommitResult> SaveForConnectionAsync(ServerProfile profile,CancellationToken ct=default)=>CommitAsync(profile,null,true,ct);
    private async Task<ProfileCommitResult> CommitAsync(ServerProfile profile,string? secret,bool defer,CancellationToken ct)
    {
        string stage=defer?"ProfileSaveForConnection":"ProfileSave";
        await LocalDiagnostics.RecordOperationAsync(_operationId,stage,"start");
        try
        {
            var result=await store.SaveProfileAsync(profile,secret,ct,deferSecretUntilAuthentication:defer);
            await LocalDiagnostics.RecordOperationAsync(_operationId,stage,"success");return result;
        }
        catch(Exception ex)
        {
            await LocalDiagnostics.RecordOperationAsync(_operationId,stage,ex is OperationCanceledException?"cancelled":"fail",ex);throw;
        }
    }
}

public sealed record ProfileSubmission(ServerProfile Profile,bool ConnectNow,SessionCredentials? Credentials)
{
    public string? CleanupWarning {get;init;}
    public override string ToString()=>$"ProfileSubmission {{ ProfileId = {Profile.Id}, ConnectNow = {ConnectNow} }}";
}
