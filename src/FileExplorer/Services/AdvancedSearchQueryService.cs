using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace FileExplorer.Services;

public sealed record SqlValidationResult(bool IsValid, string? ErrorMessage);

public sealed record SqlQueryResult(DataTable Table, TimeSpan Elapsed);

/// Runs Advanced Search's raw SQL against search-index.db, read-only. The connection is opened
/// with Mode=ReadOnly - SQLite itself rejects any write statement against it, which is the real
/// enforcement boundary (not the leading-keyword check below, which only exists to produce a
/// friendlier error before that happens). Entirely separate from SearchIndexService's writer
/// thread/queue - reads here never contend with an in-progress scan.
public static class AdvancedSearchQueryService
{
    private static readonly Regex LeadingKeyword = new(@"^\s*(--[^\n]*\n\s*)*(SELECT|EXPLAIN|WITH)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// Prepares (but does not execute) the statement to surface syntax errors without touching
    /// data. Called on a debounce timer as the user types.
    public static SqlValidationResult Validate(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return new SqlValidationResult(true, null);
        }

        if (!LeadingKeyword.IsMatch(sql))
        {
            return new SqlValidationResult(false, "Only SELECT, WITH, or EXPLAIN statements are allowed (Advanced Search is read-only).");
        }

        try
        {
            using var connection = OpenReadOnlyConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.Prepare();
            return new SqlValidationResult(true, null);
        }
        catch (SqliteException ex)
        {
            return new SqlValidationResult(false, ex.Message);
        }
    }

    public static async Task<SqlQueryResult> RunAsync(string sql, CancellationToken cancellationToken)
    {
        var validation = Validate(sql);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(validation.ErrorMessage);
        }

        return await Task.Run(() =>
        {
            var startedUtc = DateTime.UtcNow;

            using var connection = OpenReadOnlyConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cancellationToken.ThrowIfCancellationRequested();

            using var reader = cmd.ExecuteReader();
            var table = new DataTable();
            table.Load(reader);

            return new SqlQueryResult(table, DateTime.UtcNow - startedUtc);
        }, cancellationToken);
    }

    private static SqliteConnection OpenReadOnlyConnection()
    {
        var connection = new SqliteConnection($"Data Source={SearchIndexService.IndexDbPath};Mode=ReadOnly");
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=15000;";
        pragma.ExecuteNonQuery();

        return connection;
    }

    /// Known tables/columns from SearchIndexService's schema, for the SQL editor's autocomplete.
    /// Not read from sqlite_master at runtime because the index db might not exist yet (e.g.
    /// before the first scan) - this list mirrors SearchIndexService.EnsureSchema's CREATE TABLEs.
    public static readonly IReadOnlyDictionary<string, string[]> SchemaTablesAndColumns = new Dictionary<string, string[]>
    {
        ["Entries"] = new[] { "Path", "Name", "DirectoryPath", "IsDirectory", "SizeBytes", "ModifiedTicks", "RootPath", "ScanGeneration", "Md5Hash" },
        ["EntriesFts"] = new[] { "Name" },
        ["FileHashes"] = new[] { "DirectoryPath", "Name", "SizeBytes", "ModifiedTicks", "Md5Hash", "HashedUtcTicks" },
        ["Meta"] = new[] { "Key", "Value" },
        ["ExcludedPaths"] = new[] { "Path", "AddedUtcTicks" },
    };

    public static readonly string[] Keywords =
    {
        "SELECT", "FROM", "WHERE", "ORDER BY", "GROUP BY", "HAVING", "LIMIT", "OFFSET",
        "JOIN", "LEFT JOIN", "INNER JOIN", "ON", "AS", "AND", "OR", "NOT", "NULL", "IS",
        "IN", "LIKE", "MATCH", "GLOB", "BETWEEN", "DISTINCT", "COUNT", "SUM", "AVG", "MIN",
        "MAX", "CASE", "WHEN", "THEN", "ELSE", "END", "UNION", "UNION ALL", "WITH", "EXPLAIN",
        "ASC", "DESC",
    };
}
