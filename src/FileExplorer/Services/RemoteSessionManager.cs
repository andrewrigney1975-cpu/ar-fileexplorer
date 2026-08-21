using System.Collections.Concurrent;
using FileExplorer.Models;

namespace FileExplorer.Services;

public sealed record RemoteConnectResult(IRemoteFileSystem FileSystem, bool NewHostKeyTrusted);

/// Holds live connected sessions keyed by RemoteConnection.Id, reused while browsing - one
/// session per connection, deliberately never pooled. Neither SftpClient nor AsyncFtpClient is
/// safe for concurrent operations against a single connection, which is also why
/// FileOperationQueueService forces single-threaded transfer for any job touching a remote path.
public static class RemoteSessionManager
{
    private static readonly ConcurrentDictionary<string, IRemoteFileSystem> Sessions = new();

    // Guards the check-then-connect-then-add sequence in GetOrConnectAsync so two callers racing
    // to open the same connectionId can't both end up connecting (and leaking one live session).
    private static readonly SemaphoreSlim ConnectLock = new(1, 1);

    /// passwordPrompt is only invoked when there's no already-open session - it should show the
    /// UI's password dialog and return null if the user cancels.
    public static async Task<RemoteConnectResult> GetOrConnectAsync(
        string connectionId,
        Func<Task<string?>> passwordPrompt,
        CancellationToken cancellationToken)
    {
        if (Sessions.TryGetValue(connectionId, out var existing))
        {
            return new RemoteConnectResult(existing, false);
        }

        await ConnectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Sessions.TryGetValue(connectionId, out existing))
            {
                return new RemoteConnectResult(existing, false);
            }

            var connection = RemoteConnectionService.Find(connectionId)
                ?? throw new InvalidOperationException("This connection no longer exists.");

            var password = await passwordPrompt().ConfigureAwait(false);
            if (password is null)
            {
                throw new OperationCanceledException();
            }

            if (connection.Protocol == RemoteProtocol.Sftp)
            {
                var sftp = new SftpFileSystem(connectionId, connection.Host, connection.Port, connection.Username, password);
                await sftp.ConnectAsync(cancellationToken).ConfigureAwait(false);
                Sessions[connectionId] = sftp;
                return new RemoteConnectResult(sftp, sftp.NewHostKeyTrusted);
            }

            var ftp = new FtpFileSystem(connection.Host, connection.Port, connection.Username, password, connection.Protocol == RemoteProtocol.Ftps);
            await ftp.ConnectAsync(cancellationToken).ConfigureAwait(false);
            Sessions[connectionId] = ftp;
            return new RemoteConnectResult(ftp, false);
        }
        finally
        {
            ConnectLock.Release();
        }
    }

    public static IRemoteFileSystem? TryGetSession(string connectionId) =>
        Sessions.TryGetValue(connectionId, out var session) ? session : null;

    public static void Disconnect(string connectionId)
    {
        if (Sessions.TryRemove(connectionId, out var session))
        {
            _ = session.DisposeAsync().AsTask();
        }
    }
}
