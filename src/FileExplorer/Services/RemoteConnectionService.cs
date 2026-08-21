using System.Text.Json;
using FileExplorer.Models;

namespace FileExplorer.Services;

/// JSON-backed store of saved FTP/SFTP connection profiles, shown in the left rail's "Remote
/// Connections" section. Mirrors NetworkLocationService exactly. Never stores a password - see
/// RemoteConnection's doc comment.
public static class RemoteConnectionService
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileExplorerApp", "remoteconnections.json");

    private static List<RemoteConnection>? _cache;

    public static List<RemoteConnection> Load()
    {
        if (_cache is not null)
        {
            return _cache;
        }

        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<List<RemoteConnection>>(json);
                _cache = loaded ?? new List<RemoteConnection>();
                return _cache;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        _cache = new List<RemoteConnection>();
        return _cache;
    }

    public static RemoteConnection? Find(string id) => Load().FirstOrDefault(c => c.Id == id);

    public static void Add(RemoteConnection connection)
    {
        var list = Load();
        list.Add(connection);
        Save(list);
    }

    public static void Remove(RemoteConnection connection)
    {
        var list = Load();
        list.Remove(connection);
        Save(list);
        RemoteSessionManager.Disconnect(connection.Id);
        RemoteHostKeyStore.Remove(connection.Id);
    }

    private static void Save(List<RemoteConnection> list)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(list));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
