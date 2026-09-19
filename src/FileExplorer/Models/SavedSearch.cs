namespace FileExplorer.Models;

/// PathFilter: a pinned recursive search (RootPath + a filter Query, re-run via IsRecursiveSearch).
/// Sql: an Advanced Search query (raw read-only SQL against search-index.db, in Sql).
public enum SavedSearchKind
{
    PathFilter = 0,
    Sql = 1,
}

/// A saved search shown in the left rail - either a pinned path-scoped filter search or an
/// Advanced Search SQL query. RootPath/Query apply to PathFilter; Sql applies to Sql.
public sealed record SavedSearch(
    int Id,
    string Name,
    SavedSearchKind Kind,
    string? RootPath,
    string? Query,
    string? Sql,
    long CreatedUtcTicks,
    long? LastRunUtcTicks);
