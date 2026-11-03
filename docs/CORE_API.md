# Core API (stable contract)

Namespace `SSHExplorer.Core`. All async methods have optional final `CancellationToken ct = default` unless specified. File/session methods run network work outside UI thread. Types are mutable public properties unless records.

```csharp
enum AuthenticationKind { Password, PrivateKey, KeyboardInteractive }
class ServerProfile { Guid Id; string Name, Host, Username, PrivateKeyPath, InitialPath; int Port=22, TimeoutSeconds=20; AuthenticationKind Authentication; bool SaveSecret; List<string> Bookmarks; }
class AppSettings { string Theme="System", ViewMode="Details"; double TreeWidth=244; int PollIntervalSeconds=2, TerminalFontSize=14; bool ShowHidden, ShowExtensions=true, DetailsPane, PreviewPane; List<ColumnSetting> Columns; }
class ColumnSetting { string Key; double Width; int Order; bool Visible; }
class AppStore {
  AppStore(string? directory=null); string DirectoryPath; List<ServerProfile> Profiles; AppSettings Settings; HostTrustStore Trust;
  Task SaveAsync(CancellationToken ct=default);
  Task SaveSecretAsync(Guid profileId,string secret,CancellationToken ct=default);
  Task<string?> ReadSecretAsync(Guid profileId,CancellationToken ct=default);
  Task DeleteProfileAsync(Guid profileId,CancellationToken ct=default);
}
record InteractivePrompt(string Instruction,string Prompt,bool Echo);
record SessionCredentials(string Secret,Func<InteractivePrompt,Task<string?>>? Prompt=null); // password or key passphrase; not serialized
enum TrustDecision { Reject, Session, Permanent }
record HostKeyPrompt(string Host,int Port,string Algorithm,string Fingerprint,string? PreviousFingerprint,bool Changed);
enum SessionState { Connecting, AwaitingTrust, Connected, Disconnected, Closing, Closed }
record CommandResult(string Output,string Error,int ExitStatus);
class ServerSession : IAsyncDisposable {
  ServerSession(ServerProfile profile,AppStore store);
  Guid Id; ServerProfile Profile; SessionState State; string? Error; bool ShellAvailable,SftpAvailable;
  RemoteFileService Files; CancellationToken Lifetime; event EventHandler? StateChanged;
  Task ConnectAsync(SessionCredentials credentials,Func<HostKeyPrompt,Task<TrustDecision>> prompt,CancellationToken ct=default);
  Task<CommandResult> ExecuteAsync(string command,CancellationToken ct=default);
  Task<Renci.SshNet.ShellStream> CreateShellAsync(uint columns=100,uint rows=30,string? directory=null,CancellationToken ct=default);
  ValueTask DisposeAsync();
}
class RemoteEntry {
  string Name,FullPath; bool IsDirectory,IsSymbolicLink,IsRegularFile; long Length; DateTime LastWriteTimeUtc,LastAccessTimeUtc;
  int UserId,GroupId; short Permissions; string? LinkTarget;
  string TypeName; string SizeText; string PermissionsText;
}
class RemoteFileService {
  string HomeDirectory;
  Task<IReadOnlyList<RemoteEntry>> ListAsync(string path,CancellationToken ct=default);
  Task<RemoteEntry> StatAsync(string path,CancellationToken ct=default);
  Task<string> ResolvePathAsync(string path,CancellationToken ct=default);
  Task<RemoteEntry> FollowLinkAsync(string path,CancellationToken ct=default); // explicit user-selected target
  Task CreateDirectoryAsync(string path,CancellationToken ct=default);
  Task CreateFileAsync(string path,CancellationToken ct=default);
  Task RenameAsync(string source,string destination,CancellationToken ct=default);
  Task DeleteAsync(string path,CancellationToken ct=default); // recursive, never follows links
  Task SetPermissionsAsync(string path,short mode,bool recursive=false,CancellationToken ct=default);
  Task SetOwnerAsync(string path,int uid,int gid,bool recursive=false,CancellationToken ct=default);
  Task<byte[]> ReadBytesAsync(string path,long maxBytes,CancellationToken ct=default);
  Task<long> CalculateSizeAsync(string path,CancellationToken ct=default);
  Task UndoAsync(CancellationToken ct=default); bool CanUndo; string? LastUndoUnavailableReason;
}
enum ConflictDecision { Replace, Skip, KeepBoth, Cancel }
record TransferConflict(string Source,string Destination,bool SourceIsDirectory,bool DestinationIsDirectory,long? SourceLength,long? DestinationLength,string? Reason=null,Guid? JobId=null,int Attempt=1);
enum TransferState { Queued, Running, Completed, Cancelled, Failed }
class TransferJob : INotifyPropertyChanged {
 Guid Id; Guid SessionId; string Name,Source,Destination; long BytesTransferred; long? TotalBytes; double BytesPerSecond; TimeSpan? Remaining; double? Progress;
 TransferState State; string? Error; void Cancel(); Task RetryAsync();
}
class TransferQueue {
 TransferQueue(); IReadOnlyList<TransferJob> Jobs; event EventHandler? Changed;
 Func<TransferConflict,Task<ConflictDecision>>? ResolveConflict;
 TransferJob Upload(ServerSession target,IEnumerable<string> localPaths,string remoteDirectory);
 TransferJob Download(ServerSession source,IEnumerable<string> remotePaths,string localDirectory);
 TransferJob Copy(ServerSession source,IEnumerable<string> remotePaths,ServerSession target,string remoteDirectory,bool move=false);
 Task WaitAsync(TransferJob job,CancellationToken ct=default);
 bool HasActive(Guid sessionId); void CancelSession(Guid sessionId);
}
static class PosixPath { string Normalize(string path,string home="/"); string Combine(string parent,string name); string Parent(string path); string Name(string path); string Quote(string value); bool IsSameOrDescendant(string path,string parent); void ValidateName(string name); }
static class ErrorMessages { string Describe(Exception exception); }
```

Host key change always throws `HostKeyChangedException` with `HostKeyPrompt Prompt`, without accepting the ordinary trust dialog. AppStore.Trust exposes `Records` and `ForgetAsync(host,port,algorithm)` for separate explicit replacement action. ShellStream ownership remains with session; view may dispose it sooner. `Files` exists but throws descriptive unavailable if SFTP failed. No fake data.
