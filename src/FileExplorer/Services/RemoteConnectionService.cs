using FileExplorer.Models;

namespace FileExplorer.Services;

public interface IRemoteConnectionService
{
    List<RemoteConnection> Load();
    RemoteConnection? Find(string id);
    void Add(RemoteConnection connection);
    void Remove(RemoteConnection connection);
}

/// JSON-backed store of saved FTP/SFTP connection profiles, shown in the left rail's "Remote
/// Connections" section. Mirrors NetworkLocationService exactly. Never stores a password - see
/// RemoteConnection's doc comment.
public sealed class RemoteConnectionService : IRemoteConnectionService
{
    private readonly JsonFileStore<List<RemoteConnection>> _store = new("remoteconnections.json", () => new List<RemoteConnection>());

    public List<RemoteConnection> Load() => _store.Load();

    public RemoteConnection? Find(string id) => Load().FirstOrDefault(c => c.Id == id);

    public void Add(RemoteConnection connection)
    {
        var list = Load();
        list.Add(connection);
        Save(list);
    }

    public void Remove(RemoteConnection connection)
    {
        var list = Load();
        list.Remove(connection);
        Save(list);
        RemoteSessionManager.Disconnect(connection.Id);
        RemoteHostKeyStore.Remove(connection.Id);
    }

    private void Save(List<RemoteConnection> list) => _store.Save(list);
}
