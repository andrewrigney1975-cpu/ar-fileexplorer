using System.Text.Json;
using FileExplorer.Models;
using Microsoft.Data.Sqlite;

namespace FileExplorer.Services;

/// SQLite-backed store (savedsearches.db, next to virtualfolders.db/ratings.db) of the left
/// rail's Saved Searches list - both pinned path-scoped filter searches (Kind=PathFilter) and
/// Advanced Search SQL queries (Kind=Sql) live in one table so the rail shows a single list.
public static class SavedSearchService
{
    private static readonly string DbDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileExplorerApp");

    private static string DbPath => Path.Combine(DbDirectory, "savedsearches.db");
    private static string LegacyJsonPath => Path.Combine(DbDirectory, "savedsearches.json");

    private static readonly object Gate = new();
    private static bool _migrated;

    public static List<SavedSearch> Load()
    {
        var results = new List<SavedSearch>();

        try
        {
            Directory.CreateDirectory(DbDirectory);
            using var connection = OpenConnection();
            MigrateLegacyJsonLocked(connection);

            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "SELECT Id, Name, Kind, RootPath, Query, Sql, CreatedUtcTicks, LastRunUtcTicks FROM SavedSearches ORDER BY Id";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new SavedSearch(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    (SavedSearchKind)reader.GetInt32(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.GetInt64(6),
                    reader.IsDBNull(7) ? null : reader.GetInt64(7)));
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            LoggingService.LogWarning("SavedSearchService.Load", ex);
        }

        return results;
    }

    public static SavedSearch Add(string name, SavedSearchKind kind, string? rootPath, string? query, string? sql)
    {
        var createdUtcTicks = DateTimeOffset.UtcNow.UtcTicks;
        var id = 0;

        ExecuteWrite(cmd =>
        {
            cmd.CommandText =
                "INSERT INTO SavedSearches (Name, Kind, RootPath, Query, Sql, CreatedUtcTicks) " +
                "VALUES (@name, @kind, @root, @query, @sql, @created); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("@name", name);
            cmd.Parameters.AddWithValue("@kind", (int)kind);
            cmd.Parameters.AddWithValue("@root", (object?)rootPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@query", (object?)query ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@sql", (object?)sql ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@created", createdUtcTicks);
            id = Convert.ToInt32(cmd.ExecuteScalar());
        });

        return new SavedSearch(id, name, kind, rootPath, query, sql, createdUtcTicks, null);
    }

    public static void Rename(int id, string newName)
    {
        ExecuteWrite(cmd =>
        {
            cmd.CommandText = "UPDATE SavedSearches SET Name = @name WHERE Id = @id";
            cmd.Parameters.AddWithValue("@name", newName);
            cmd.Parameters.AddWithValue("@id", id);
        });
    }

    /// Updates a Sql-kind saved query's text in place (used by Advanced Search's "Save").
    public static void UpdateSql(int id, string sql)
    {
        ExecuteWrite(cmd =>
        {
            cmd.CommandText = "UPDATE SavedSearches SET Sql = @sql WHERE Id = @id";
            cmd.Parameters.AddWithValue("@sql", sql);
            cmd.Parameters.AddWithValue("@id", id);
        });
    }

    public static void TouchLastRun(int id)
    {
        ExecuteWrite(cmd =>
        {
            cmd.CommandText = "UPDATE SavedSearches SET LastRunUtcTicks = @ticks WHERE Id = @id";
            cmd.Parameters.AddWithValue("@ticks", DateTimeOffset.UtcNow.UtcTicks);
            cmd.Parameters.AddWithValue("@id", id);
        });
    }

    public static void Remove(SavedSearch search)
    {
        ExecuteWrite(cmd =>
        {
            cmd.CommandText = "DELETE FROM SavedSearches WHERE Id = @id";
            cmd.Parameters.AddWithValue("@id", search.Id);
        });
    }

    private static void MigrateLegacyJsonLocked(SqliteConnection connection)
    {
        lock (Gate)
        {
            if (_migrated)
            {
                return;
            }

            _migrated = true;

            try
            {
                if (!File.Exists(LegacyJsonPath))
                {
                    return;
                }

                using (var count = connection.CreateCommand())
                {
                    count.CommandText = "SELECT COUNT(*) FROM SavedSearches";
                    if (Convert.ToInt64(count.ExecuteScalar()) > 0)
                    {
                        return;
                    }
                }

                var json = File.ReadAllText(LegacyJsonPath);
                var legacy = JsonSerializer.Deserialize<List<LegacySavedSearch>>(json);
                if (legacy is { Count: > 0 })
                {
                    using var transaction = connection.BeginTransaction();
                    var createdUtcTicks = DateTimeOffset.UtcNow.UtcTicks;
                    foreach (var entry in legacy)
                    {
                        using var cmd = connection.CreateCommand();
                        cmd.Transaction = transaction;
                        cmd.CommandText =
                            "INSERT INTO SavedSearches (Name, Kind, RootPath, Query, Sql, CreatedUtcTicks) " +
                            "VALUES (@name, 0, @root, @query, NULL, @created)";
                        cmd.Parameters.AddWithValue("@name", entry.Name);
                        cmd.Parameters.AddWithValue("@root", entry.RootPath);
                        cmd.Parameters.AddWithValue("@query", entry.Query);
                        cmd.Parameters.AddWithValue("@created", createdUtcTicks);
                        cmd.ExecuteNonQuery();
                    }

                    transaction.Commit();
                }

                File.Move(LegacyJsonPath, LegacyJsonPath + ".migrated", overwrite: true);
            }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or JsonException)
            {
                LoggingService.LogWarning("SavedSearchService.MigrateLegacyJson", ex);
            }
        }
    }

    private sealed record LegacySavedSearch(string Name, string RootPath, string Query);

    private static SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data Source={DbPath}");
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=15000;";
        pragma.ExecuteNonQuery();

        using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS SavedSearches (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Kind INTEGER NOT NULL,
                RootPath TEXT,
                Query TEXT,
                Sql TEXT,
                CreatedUtcTicks INTEGER NOT NULL,
                LastRunUtcTicks INTEGER
            );
            """;
        schema.ExecuteNonQuery();

        return connection;
    }

    private static void ExecuteWrite(Action<SqliteCommand> configure)
    {
        try
        {
            Directory.CreateDirectory(DbDirectory);
            using var connection = OpenConnection();
            using var cmd = connection.CreateCommand();
            configure(cmd);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            LoggingService.LogWarning("SavedSearchService.ExecuteWrite", ex);
        }
    }
}
