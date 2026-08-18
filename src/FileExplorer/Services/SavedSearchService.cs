using System.Text.Json;
using FileExplorer.Models;

namespace FileExplorer.Services;

/// JSON-backed store of pinned recursive searches, shown in the left rail.
public static class SavedSearchService
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileExplorerApp", "savedsearches.json");

    private static List<SavedSearch>? _cache;

    public static List<SavedSearch> Load()
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
                var loaded = JsonSerializer.Deserialize<List<SavedSearch>>(json);
                _cache = loaded ?? new List<SavedSearch>();
                return _cache;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        _cache = new List<SavedSearch>();
        return _cache;
    }

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

    private static void Save(List<SavedSearch> list)
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
