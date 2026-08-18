namespace FileExplorer.Models;

/// A pinned recursive search: a name, the folder it scans, and the query to re-run.
public sealed record SavedSearch(string Name, string RootPath, string Query);
