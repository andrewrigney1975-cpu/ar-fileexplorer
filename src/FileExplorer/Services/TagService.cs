using System.Text.Json;

namespace FileExplorer.Services;

/// App-local color-label store, keyed by full path (NTFS has no portable, cross-tool tag API).
/// A moved or renamed file loses its tag - a known trade-off for staying dependency-free.
public static class TagService
{
    public static readonly string[] ColorNames = { "Red", "Orange", "Yellow", "Green", "Blue", "Purple" };

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileExplorerApp", "tags.json");

    private static Dictionary<string, string>? _cache;

    public static string? GetColor(string path)
    {
        var map = LoadCache();
        return map.TryGetValue(path, out var color) ? color : null;
    }

    public static void SetColor(string path, string? color)
    {
        var map = LoadCache();

        if (color is null)
        {
            map.Remove(path);
        }
        else
        {
            map[path] = color;
        }

        Save(map);
    }

    private static Dictionary<string, string> LoadCache()
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
                _cache = loaded is null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase);
                return _cache;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LoggingService.LogWarning("TagService.LoadCache", ex);
        }

        _cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
            LoggingService.LogWarning("TagService.Save", ex);
        }
    }
}
