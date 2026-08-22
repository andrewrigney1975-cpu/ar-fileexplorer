using System.Text.Json;

namespace FileExplorer.Services;

/// Shared load/cache/save plumbing for the app's local JSON-backed stores (favourites, saved
/// searches, sync tasks, schedules, etc.) - each store still owns its own file name, default
/// value, and domain methods (Add/Remove/Find/...); this only dedupes the read-cache-write
/// mechanics that were previously copy-pasted across ~10 near-identical services.
public sealed class JsonFileStore<T>
{
    private readonly string _filePath;
    private readonly Func<T> _createDefault;
    private T? _cache;

    public JsonFileStore(string fileName, Func<T> createDefault)
    {
        _filePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileExplorerApp", fileName);
        _createDefault = createDefault;
    }

    public T Load()
    {
        if (_cache is not null)
        {
            return _cache;
        }

        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                var loaded = JsonSerializer.Deserialize<T>(json);
                if (loaded is not null)
                {
                    _cache = loaded;
                    return _cache;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Falls back to a fresh default (e.g. an empty favourites/sync-tasks/etc list) - to
            // every caller this looks identical to "nothing saved yet", not a read failure, so this
            // is the only place that failure is ever visible at all.
            LoggingService.LogWarning($"JsonFileStore<{typeof(T).Name}>.Load: {_filePath}", ex);
        }

        _cache = _createDefault();
        return _cache;
    }

    public void Save(T value)
    {
        _cache = value;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(value));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoggingService.LogWarning($"JsonFileStore<{typeof(T).Name}>.Save: {_filePath}", ex);
        }
    }
}
