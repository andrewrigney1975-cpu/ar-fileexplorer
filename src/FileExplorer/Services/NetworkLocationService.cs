using FileExplorer.Models;

namespace FileExplorer.Services;

/// JSON-backed store of pinned LAN shares (UNC paths), shown in the left rail.
public static class NetworkLocationService
{
    private static readonly JsonFileStore<List<NetworkLocation>> Store = new("networklocations.json", () => new List<NetworkLocation>());

    public static List<NetworkLocation> Load() => Store.Load();

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

    private static void Save(List<NetworkLocation> list) => Store.Save(list);
}
