using System.Text.Json;

namespace FileExplorer.Services;

/// Trust-on-first-use SSH host key pinning for SFTP connections: the fingerprint seen on a
/// connection's first successful connect is stored here, and every later connect to that same
/// connectionId must match it exactly - a mismatch is a hard failure (never a silent re-accept),
/// since that's exactly the scenario host-key verification exists to catch.
/// Kept in a file separate from remoteconnections.json so deleting and re-adding a connection to
/// the same host doesn't silently reset trust.
public static class RemoteHostKeyStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileExplorerApp", "remotehostkeys.json");

    private static Dictionary<string, string>? _cache;

    /// Returns the pinned fingerprint for connectionId, or null if this connection has never
    /// successfully connected before (first-use case).
    public static string? GetPinnedFingerprint(string connectionId) => Load().GetValueOrDefault(connectionId);

    public static void Pin(string connectionId, string fingerprint)
    {
        var map = Load();
        map[connectionId] = fingerprint;
        Save(map);
    }

    public static void Remove(string connectionId)
    {
        var map = Load();
        if (map.Remove(connectionId))
        {
            Save(map);
        }
    }

    private static Dictionary<string, string> Load()
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
                var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                _cache = loaded ?? new Dictionary<string, string>();
                return _cache;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        _cache = new Dictionary<string, string>();
        return _cache;
    }

    private static void Save(Dictionary<string, string> map)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(map));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
