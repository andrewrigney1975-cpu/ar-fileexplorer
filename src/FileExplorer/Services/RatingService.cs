using Microsoft.Data.Sqlite;

namespace FileExplorer.Services;

/// App-local 0-5 star ratings, keyed by full path and stored in SQLite (ratings.db, next to the
/// search index). Like [[TagService]], a moved/renamed item loses its explicit rating.
///
/// Effective rating resolution (see <see cref="GetEffective"/>):
///  1. an explicit rating on the item itself, or
///  2. for a folder with no explicit rating: the average of its direct children's effective ratings, or
///  3. the direct parent's rating (explicit or child-average), inherited.
/// Only case 1 is "explicit"; 2 and 3 are shown at reduced opacity by the UI.
public static class RatingService
{
    public const int MaxStars = 5;

    // Downward child-average recursion is bounded so a pathological tree can't stall a listing.
    private const int MaxAverageDepth = 16;

    private static readonly string DbDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileExplorerApp");

    private static string DbPath => Path.Combine(DbDirectory, "ratings.db");

    private static readonly object Gate = new();
    private static Dictionary<string, int>? _cache;

    /// Raised after any explicit rating is set or cleared, so open panes can re-resolve.
    public static event EventHandler? Changed;

    public static int? GetExplicit(string path) =>
        Load().TryGetValue(path, out var v) ? v : null;

    public static void SetRating(string path, int? stars)
    {
        lock (Gate)
        {
            var cache = LoadLocked();
            if (stars is null or 0)
            {
                cache.Remove(path);
                ExecuteWrite("DELETE FROM Ratings WHERE Path = @p", cmd => cmd.Parameters.AddWithValue("@p", path));
            }
            else
            {
                var clamped = Math.Clamp(stars.Value, 1, MaxStars);
                cache[path] = clamped;
                ExecuteWrite(
                    "INSERT INTO Ratings (Path, Stars) VALUES (@p, @s) ON CONFLICT(Path) DO UPDATE SET Stars = excluded.Stars",
                    cmd =>
                    {
                        cmd.Parameters.AddWithValue("@p", path);
                        cmd.Parameters.AddWithValue("@s", clamped);
                    });
            }
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// The rating to display for <paramref name="path"/>, or null if nothing applies. When
    /// <c>IsExplicit</c> is false the value is derived (child average or inherited) and the UI
    /// renders it at half opacity.
    public static (double Value, bool IsExplicit)? GetEffective(string path, bool isDirectory)
    {
        var cache = Load();

        if (cache.TryGetValue(path, out var own))
        {
            return (own, true);
        }

        if (isDirectory && TryChildAverage(path, cache, 0, out var average))
        {
            return (average, false);
        }

        var parent = Path.GetDirectoryName(path);
        if (parent is not null)
        {
            if (cache.TryGetValue(parent, out var parentOwn))
            {
                return (parentOwn, false);
            }

            if (TryChildAverage(parent, cache, 0, out var parentAverage))
            {
                return (parentAverage, false);
            }
        }

        return null;
    }

    /// Resolves effective ratings for a whole folder listing in one pass: the parent's own
    /// effective rating is computed once and reused as the inheritance fallback for every unrated
    /// file, instead of re-deriving it per item.
    public static void ResolveListing(string parentPath, IReadOnlyList<Models.FileSystemItem> items)
    {
        var cache = Load();

        (double Value, bool IsExplicit)? parentEffective = null;
        if (cache.TryGetValue(parentPath, out var parentOwn))
        {
            parentEffective = (parentOwn, true);
        }
        else if (TryChildAverage(parentPath, cache, 0, out var parentAverage))
        {
            parentEffective = (parentAverage, false);
        }

        foreach (var item in items)
        {
            if (cache.TryGetValue(item.FullPath, out var own))
            {
                item.RatingValue = own;
                item.RatingIsCalculated = false;
            }
            else if (item.IsDirectory && TryChildAverage(item.FullPath, cache, 0, out var average))
            {
                item.RatingValue = average;
                item.RatingIsCalculated = true;
            }
            else if (parentEffective is { } pe)
            {
                item.RatingValue = pe.Value;
                item.RatingIsCalculated = true;
            }
            else
            {
                item.RatingValue = null;
                item.RatingIsCalculated = false;
            }
        }
    }

    private static bool TryChildAverage(string directory, Dictionary<string, int> cache, int depth, out double average)
    {
        average = 0;

        if (depth > MaxAverageDepth)
        {
            return false;
        }

        // Cheap gate: skip the directory enumeration entirely unless some explicit rating exists
        // somewhere beneath this folder.
        var prefix = directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!cache.Keys.Any(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        List<string> children;
        try
        {
            children = Directory.EnumerateFileSystemEntries(directory).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        var values = new List<double>();
        foreach (var child in children)
        {
            if (cache.TryGetValue(child, out var explicitChild))
            {
                values.Add(explicitChild);
                continue;
            }

            bool childIsDirectory;
            try
            {
                childIsDirectory = File.GetAttributes(child).HasFlag(FileAttributes.Directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (childIsDirectory && TryChildAverage(child, cache, depth + 1, out var childAverage))
            {
                values.Add(childAverage);
            }
        }

        if (values.Count == 0)
        {
            return false;
        }

        average = values.Average();
        return true;
    }

    private static Dictionary<string, int> Load()
    {
        lock (Gate)
        {
            return LoadLocked();
        }
    }

    private static Dictionary<string, int> LoadLocked()
    {
        if (_cache is not null)
        {
            return _cache;
        }

        _cache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        try
        {
            Directory.CreateDirectory(DbDirectory);
            using var connection = new SqliteConnection($"Data Source={DbPath}");
            connection.Open();

            using (var schema = connection.CreateCommand())
            {
                schema.CommandText = "CREATE TABLE IF NOT EXISTS Ratings (Path TEXT PRIMARY KEY, Stars INTEGER NOT NULL);";
                schema.ExecuteNonQuery();
            }

            using var read = connection.CreateCommand();
            read.CommandText = "SELECT Path, Stars FROM Ratings";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                _cache[reader.GetString(0)] = (int)reader.GetInt64(1);
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            LoggingService.LogWarning("RatingService.Load", ex);
        }

        return _cache;
    }

    private static void ExecuteWrite(string sql, Action<SqliteCommand> bind)
    {
        try
        {
            Directory.CreateDirectory(DbDirectory);
            using var connection = new SqliteConnection($"Data Source={DbPath}");
            connection.Open();

            using (var schema = connection.CreateCommand())
            {
                schema.CommandText = "CREATE TABLE IF NOT EXISTS Ratings (Path TEXT PRIMARY KEY, Stars INTEGER NOT NULL);";
                schema.ExecuteNonQuery();
            }

            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            bind(cmd);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            LoggingService.LogWarning("RatingService.ExecuteWrite", ex);
        }
    }
}
