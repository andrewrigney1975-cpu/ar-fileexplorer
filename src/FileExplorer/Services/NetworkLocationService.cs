using System.Text.Json;
using FileExplorer.Models;

namespace FileExplorer.Services;

/// JSON-backed store of pinned LAN shares (UNC paths), shown in the left rail.
public static class NetworkLocationService
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileExplorerApp", "networklocations.json");

    private static List<NetworkLocation>? _cache;

    public static List<NetworkLocation> Load()
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
                var loaded = JsonSerializer.Deserialize<List<NetworkLocation>>(json);
                _cache = loaded ?? new List<NetworkLocation>();
                return _cache;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        _cache = new List<NetworkLocation>();
        return _cache;
    }

    public static void Add(NetworkLocation location)
    {
        var list = Load();
        list.Add(location);
        Save(list);
    }

    public static void Remove(NetworkLocation location)
    {
        var list = Load();
        list.Remove(location);
        Save(list);
    }

    private static void Save(List<NetworkLocation> list)
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
