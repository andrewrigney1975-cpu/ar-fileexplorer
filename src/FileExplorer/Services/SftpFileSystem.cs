using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace FileExplorer.Services;

/// SFTP adapter wrapping Renci.SshNet.SftpClient. Host-key verification is trust-on-first-use:
/// the fingerprint seen on a connection's first successful connect is pinned
/// (RemoteHostKeyStore); a later connect whose key doesn't match that pin is a hard failure -
/// never silently re-accepted, since that's exactly the scenario host-key verification exists to
/// catch. First connect does NOT block on an interactive per-key confirmation dialog (that would
/// require synchronously blocking SSH.NET's HostKeyReceived event on a cross-thread UI prompt) -
/// it auto-pins, and NewHostKeyTrusted tells the caller to surface a one-time "new host trusted"
/// notice afterward.
public sealed class SftpFileSystem : IRemoteFileSystem
{
    private readonly SftpClient _client;
    private string? _hostKeyMismatchError;

    public SftpFileSystem(string connectionId, string host, int port, string username, string password)
    {
        _client = new SftpClient(host, port, username, password);
        _client.HostKeyReceived += (_, e) =>
        {
            var fingerprint = e.FingerPrintSHA256;
            var pinned = RemoteHostKeyStore.GetPinnedFingerprint(connectionId);

            if (pinned is null)
            {
                RemoteHostKeyStore.Pin(connectionId, fingerprint);
                e.CanTrust = true;
                NewHostKeyTrusted = true;
                return;
            }

            if (string.Equals(pinned, fingerprint, StringComparison.Ordinal))
            {
                e.CanTrust = true;
                return;
            }

            e.CanTrust = false;
            _hostKeyMismatchError =
                $"The SSH host key for this server has changed since it was first trusted (was {pinned}, now {fingerprint}). " +
                "This can mean the server was legitimately reconfigured, or that something is intercepting the connection. " +
                "Remove and re-add this connection only if you're sure the new key is expected.";
        };
    }

    /// True after ConnectAsync if this was this connectionId's first-ever successful connect (no
    /// prior pinned key) - the caller should show a one-time "new host trusted" notice.
    public bool NewHostKeyTrusted { get; private set; }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (_hostKeyMismatchError is not null)
        {
            throw new IOException(_hostKeyMismatchError);
        }
    }

    public async Task<IReadOnlyList<RemoteEntry>> ListAsync(string remotePath, CancellationToken cancellationToken)
    {
        var entries = new List<RemoteEntry>();

        await foreach (var file in _client.ListDirectoryAsync(remotePath, cancellationToken).ConfigureAwait(false))
        {
            if (file.Name is "." or "..")
            {
                continue;
            }

            entries.Add(new RemoteEntry(file.Name, file.IsDirectory, file.IsDirectory ? 0 : file.Length, file.LastWriteTimeUtc));
        }

        return entries;
    }

    public Task<bool> ExistsAsync(string remotePath, CancellationToken cancellationToken) =>
        _client.ExistsAsync(remotePath, cancellationToken);

    public async Task<RemoteEntry> GetInfoAsync(string remotePath, CancellationToken cancellationToken)
    {
        var file = await _client.GetAsync(remotePath, cancellationToken).ConfigureAwait(false);
        return new RemoteEntry(file.Name, file.IsDirectory, file.IsDirectory ? 0 : file.Length, file.LastWriteTimeUtc);
    }

    public async Task<Stream> OpenReadAsync(string remotePath, CancellationToken cancellationToken) =>
        await _client.OpenAsync(remotePath, FileMode.Open, FileAccess.Read, cancellationToken).ConfigureAwait(false);

    public async Task DownloadAsync(string remotePath, string localDestinationPath, IProgress<long>? bytesTransferred, CancellationToken cancellationToken)
    {
        await using var output = File.Create(localDestinationPath);

        IProgress<DownloadFileProgressReport>? sshProgress = bytesTransferred is null ? null
            : new Progress<DownloadFileProgressReport>(r => bytesTransferred.Report((long)r.TotalBytesDownloaded));

        await _client.DownloadFileAsync(remotePath, output, sshProgress, cancellationToken).ConfigureAwait(false);
    }

    public async Task UploadAsync(string localSourcePath, string remotePath, IProgress<long>? bytesTransferred, CancellationToken cancellationToken)
    {
        await using var input = File.OpenRead(localSourcePath);

        IProgress<UploadFileProgressReport>? sshProgress = bytesTransferred is null ? null
            : new Progress<UploadFileProgressReport>(r => bytesTransferred.Report((long)r.TotalBytesUploaded));

        await _client.UploadFileAsync(input, remotePath, canOverride: true, sshProgress, cancellationToken).ConfigureAwait(false);
    }

    public Task CreateDirectoryAsync(string remotePath, CancellationToken cancellationToken) =>
        _client.CreateDirectoryAsync(remotePath, cancellationToken);

    public Task DeleteFileAsync(string remotePath, CancellationToken cancellationToken) =>
        _client.DeleteFileAsync(remotePath, cancellationToken);

    public Task DeleteDirectoryAsync(string remotePath, CancellationToken cancellationToken) =>
        _client.DeleteDirectoryAsync(remotePath, cancellationToken);

    public Task RenameAsync(string fromRemotePath, string toRemotePath, CancellationToken cancellationToken) =>
        _client.RenameFileAsync(fromRemotePath, toRemotePath, cancellationToken);

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
