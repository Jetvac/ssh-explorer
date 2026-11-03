using SSHExplorer.Core;

namespace SSHExplorer.App;

// The browser's interaction boundary is shared by production SFTP and controlled CI fixtures.
internal interface IFileBrowserOperations
{
    bool Available { get; }
    Task<string> ResolvePathAsync(string path, CancellationToken ct);
    Task<IReadOnlyList<RemoteEntry>> ListAsync(string path, CancellationToken ct);
    Task<string> CreateUniqueAsync(string parent, bool directory, CancellationToken ct);
    Task RenameAsync(string source, string destination, CancellationToken ct);
}

internal sealed class SftpBrowserOperations(RemoteFileService files) : IFileBrowserOperations
{
    public bool Available => files.Available;
    public Task<string> ResolvePathAsync(string path, CancellationToken ct) => files.ResolvePathAsync(path, ct);
    public Task<IReadOnlyList<RemoteEntry>> ListAsync(string path, CancellationToken ct) => files.ListAsync(path, ct);
    public Task<string> CreateUniqueAsync(string parent, bool directory, CancellationToken ct) => files.CreateUniqueAsync(parent, directory, ct);
    public Task RenameAsync(string source, string destination, CancellationToken ct) => files.RenameAsync(source, destination, ct);
}
