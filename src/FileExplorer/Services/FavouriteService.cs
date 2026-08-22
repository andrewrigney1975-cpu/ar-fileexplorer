using FileExplorer.Models;

namespace FileExplorer.Services;

/// JSON-backed store of pinned folders ("Favourites"), shown in the left rail.
public static class FavouriteService
{
    private static readonly JsonFileStore<List<FavouriteLocation>> Store = new("favourites.json", () => new List<FavouriteLocation>());

    /// Raised whenever a favourite is added or removed, from anywhere (the rail's own +/remove
    /// buttons, or "Add to Favourites" on a folder's context menu), so the rail can refresh either way.
    public static event EventHandler? Changed;

    public static List<FavouriteLocation> Load() => Store.Load();

    public static bool IsFavourite(string path) =>
        Load().Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));

    public static void Add(FavouriteLocation location)
    {
        var list = Load();
        if (list.Any(f => string.Equals(f.Path, location.Path, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        list.Add(location);
        Save(list);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void Remove(FavouriteLocation location)
    {
        var list = Load();
        list.Remove(location);
        Save(list);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static void Save(List<FavouriteLocation> list) => Store.Save(list);
}
