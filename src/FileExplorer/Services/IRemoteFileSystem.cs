namespace FileExplorer.Services;

/// A directory/file entry as reported by a remote provider - deliberately simpler than
/// FileSystemItem (no FileAttributes, no local-only concepts). The caller (FileSystemService)
/// maps these to full FileSystemItems, combining Name with the caller's own remote path via
/// RemotePathService - the provider itself never needs to know about the app's
/// "scheme://connectionId/path" convention.
public sealed record RemoteEntry(string Name, bool IsDirectory, long Size, DateTimeOffset Modified);

/// One live connected session against an FTP/FTPS/SFTP server. All paths passed to/from this
/// interface are server-relative remote paths (e.g. "/pub/dir"), never the app's
/// "scheme://connectionId/..." strings - RemoteSessionManager/FileSystemService handle that
/// translation at the boundary.
public interface IRemoteFileSystem : IAsyncDisposable
{
    Task<IReadOnlyList<RemoteEntry>> ListAsync(string remotePath, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(string remotePath, CancellationToken cancellationToken);

    /// Info (including whether it's a directory, and size if not) for a single remote path -
    /// used to classify a top-level path passed into a copy/move job without a full
    /// parent-directory listing round trip.
    Task<RemoteEntry> GetInfoAsync(string remotePath, CancellationToken cancellationToken);

    /// For reading a remote file's bytes without downloading to disk first (checksum/hash use).
    Task<Stream> OpenReadAsync(string remotePath, CancellationToken cancellationToken);

    Task DownloadAsync(string remotePath, string localDestinationPath, IProgress<long>? bytesTransferred, CancellationToken cancellationToken);

    Task UploadAsync(string localSourcePath, string remotePath, IProgress<long>? bytesTransferred, CancellationToken cancellationToken);

    Task CreateDirectoryAsync(string remotePath, CancellationToken cancellationToken);

    Task DeleteFileAsync(string remotePath, CancellationToken cancellationToken);

    Task DeleteDirectoryAsync(string remotePath, CancellationToken cancellationToken);

    Task RenameAsync(string fromRemotePath, string toRemotePath, CancellationToken cancellationToken);
}
