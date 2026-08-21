using FluentFTP;

namespace FileExplorer.Services;

/// FTP/FTPS adapter wrapping FluentFTP.AsyncFtpClient. Certificate validation for FTPS uses
/// FluentFTP's own default (system trust store) - no certificate pinning here, unlike the SFTP
/// host-key TOFU pinning, since that wasn't called for in scope; a failed/untrusted cert simply
/// surfaces as a connect exception rather than being silently bypassed.
public sealed class FtpFileSystem : IRemoteFileSystem
{
    private readonly AsyncFtpClient _client;

    public FtpFileSystem(string host, int port, string username, string password, bool useExplicitTls)
    {
        var config = new FtpConfig
        {
            EncryptionMode = useExplicitTls ? FtpEncryptionMode.Explicit : FtpEncryptionMode.None,
        };

        _client = new AsyncFtpClient(host, username, password, port, config, logger: null);
    }

    public Task ConnectAsync(CancellationToken cancellationToken) => _client.Connect(cancellationToken);

    public async Task<IReadOnlyList<RemoteEntry>> ListAsync(string remotePath, CancellationToken cancellationToken)
    {
        var listing = await _client.GetListing(remotePath, cancellationToken).ConfigureAwait(false);

        return listing
            .Where(item => item.Type is FtpObjectType.File or FtpObjectType.Directory)
            .Select(item => new RemoteEntry(item.Name, item.Type == FtpObjectType.Directory, Math.Max(0, item.Size), item.Modified))
            .ToList();
    }

    public async Task<bool> ExistsAsync(string remotePath, CancellationToken cancellationToken) =>
        await _client.FileExists(remotePath, cancellationToken).ConfigureAwait(false) ||
        await _client.DirectoryExists(remotePath, cancellationToken).ConfigureAwait(false);

    public async Task<RemoteEntry> GetInfoAsync(string remotePath, CancellationToken cancellationToken)
    {
        var info = await _client.GetObjectInfo(remotePath, false, cancellationToken).ConfigureAwait(false)
            ?? throw new IOException($"Not found: {remotePath}");
        return new RemoteEntry(info.Name, info.Type == FtpObjectType.Directory, Math.Max(0, info.Size), info.Modified);
    }

    public async Task<Stream> OpenReadAsync(string remotePath, CancellationToken cancellationToken) =>
        await _client.OpenRead(remotePath, FtpDataType.Binary, restart: 0, checkIfFileExists: true, cancellationToken).ConfigureAwait(false);

    public async Task DownloadAsync(string remotePath, string localDestinationPath, IProgress<long>? bytesTransferred, CancellationToken cancellationToken)
    {
        IProgress<FtpProgress>? ftpProgress = bytesTransferred is null ? null
            : new Progress<FtpProgress>(p => bytesTransferred.Report(p.TransferredBytes));

        await _client.DownloadFile(localDestinationPath, remotePath, FtpLocalExists.Overwrite, FtpVerify.None, ftpProgress, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task UploadAsync(string localSourcePath, string remotePath, IProgress<long>? bytesTransferred, CancellationToken cancellationToken)
    {
        IProgress<FtpProgress>? ftpProgress = bytesTransferred is null ? null
            : new Progress<FtpProgress>(p => bytesTransferred.Report(p.TransferredBytes));

        await _client.UploadFile(localSourcePath, remotePath, FtpRemoteExists.Overwrite, createRemoteDir: true, FtpVerify.None, ftpProgress, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task CreateDirectoryAsync(string remotePath, CancellationToken cancellationToken) =>
        await _client.CreateDirectory(remotePath, cancellationToken).ConfigureAwait(false);

    public Task DeleteFileAsync(string remotePath, CancellationToken cancellationToken) =>
        _client.DeleteFile(remotePath, cancellationToken);

    public Task DeleteDirectoryAsync(string remotePath, CancellationToken cancellationToken) =>
        _client.DeleteDirectory(remotePath, cancellationToken);

    public Task RenameAsync(string fromRemotePath, string toRemotePath, CancellationToken cancellationToken) =>
        _client.Rename(fromRemotePath, toRemotePath, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _client.Disconnect(CancellationToken.None).ConfigureAwait(false);
        _client.Dispose();
    }
}
