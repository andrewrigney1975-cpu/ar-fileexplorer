using FileExplorer.Models;

namespace FileExplorer.Services;

/// JSON-backed store of pinned recursive searches, shown in the left rail.
public static class SavedSearchService
{
    private static readonly JsonFileStore<List<SavedSearch>> Store = new("savedsearches.json", () => new List<SavedSearch>());

    public static List<SavedSearch> Load() => Store.Load();

    public static void Add(SavedSearch search)
    {
        var list = Load();
        list.Add(search);
        Save(list);
    }

    public static void Remove(SavedSearch search)
    {
        var list = Load();
        list.Remove(search);
        Save(list);
    }

    private static void Save(List<SavedSearch> list) => Store.Save(list);
}
