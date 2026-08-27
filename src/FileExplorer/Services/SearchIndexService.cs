using System.Collections.Concurrent;
using FileExplorer.Helpers;
using Microsoft.Data.Sqlite;

namespace FileExplorer.Services;

public sealed record SearchIndexEntry(string Path, string Name, string DirectoryPath, bool IsDirectory, long SizeBytes, DateTimeOffset Modified);

/// Background, opt-in, persistent filename index powering "Search Everywhere" (command palette
/// entry + standalone dialog) - a substring search across every file/folder under whichever roots
/// the user has added via Control Centre > Search Index, backed by SQLite instead of a live
/// per-search filesystem walk (see FileSystemService.SearchRecursive for that older, still-existing
/// per-pane recursive search - this is a separate, opt-in, whole-index feature).
///
/// Deliberately NOT built on the USN journal (the Everything/voidtools approach) - reading it needs
/// a raw volume handle (FSCTL_QUERY_USN_JOURNAL), which requires administrator rights, and this app
/// is unpackaged and pitched as "just run the exe," never asking for elevation. Also deliberately
/// NOT built on the OS's own Windows Search indexer - it only covers the user profile/Libraries by
/// default, so a data drive or NAS mount would silently return zero results rather than "not
/// indexed," which is worse than no feature at all.
///
/// Indexing is opt-in per root (nothing is scanned until the user explicitly adds a folder/drive in
/// Control Centre > Search Index) - there is no "index everything" default. Freshness comes from a
/// recursive FileSystemWatcher per root for near-real-time updates, backstopped by a periodic full
/// rescan (every RescanIntervalHours) for whatever a watcher missed (buffer overflow on a very busy
/// root, or the app not running when a change happened).
public static class SearchIndexService
{
    private const int RescanIntervalHours = 24;
    private const int WatcherFlushDelayMs = 1000;
    private const int SqlCandidateLimit = 2000;

    private static readonly JsonFileStore<List<string>> RootsStore = new("search-index-roots.json", () => new List<string>());

    private static readonly object WatcherLock = new();
    private static readonly Dictionary<string, FileSystemWatcher> Watchers = new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentQueue<PendingChange> PendingChanges = new();
    private static Timer? _flushTimer;
    private static CancellationTokenSource? _scanCts;
    private static bool _started;

    private sealed record PendingChange(string Path, string? OldPath, WatcherChangeTypes ChangeType);

    /// Raised whenever scan progress, root list, or entry count changes, so Control Centre's Search
    /// Index section can refresh its status text without polling.
    public static event EventHandler? StatusChanged;

    public static bool IsScanning { get; private set; }
    public static int EntryCount { get; private set; }
    public static DateTimeOffset? LastScanUtc { get; private set; }
    public static IReadOnlyList<string> Roots => RootsStore.Load();

    private static string DbDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileExplorerApp");
    private static string DbPath => Path.Combine(DbDirectory, "search-index.db");

    /// Safe to call more than once (e.g. re-enabling the feature mid-session in Preferences after
    /// it was off at launch) - only the first call does anything.
    public static void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;

        EnsureSchema();
        RefreshEntryCount();

        LastScanUtc = ReadMeta("LastScanUtc") is { } raw && long.TryParse(raw, out var ticks)
            ? new DateTimeOffset(ticks, TimeSpan.Zero)
            : null;

        foreach (var root in RootsStore.Load())
        {
            StartWatcher(root);
        }

        _ = PeriodicRescanLoopAsync();
    }

    public static void AddRoot(string path)
    {
        var normalized = NormalizeRoot(path);
        var roots = RootsStore.Load();
        if (roots.Any(r => string.Equals(r, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        roots.Add(normalized);
        RootsStore.Save(roots);
        StartWatcher(normalized);
        StatusChanged?.Invoke(null, EventArgs.Empty);
        _ = RebuildAsync(CancellationToken.None);
    }

    public static void RemoveRoot(string path)
    {
        var roots = RootsStore.Load();
        roots.RemoveAll(r => string.Equals(r, path, StringComparison.OrdinalIgnoreCase));
        RootsStore.Save(roots);

        StopWatcher(path);

        try
        {
            using var connection = OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM Entries WHERE RootPath = @root";
            cmd.Parameters.AddWithValue("@root", path);
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            LoggingService.LogWarning("SearchIndexService.RemoveRoot", ex);
        }

        RefreshEntryCount();
        StatusChanged?.Invoke(null, EventArgs.Empty);
    }

    /// Full rescan of every configured root, replacing anything that's changed and dropping rows for
    /// anything no longer on disk. Supersedes (cancels) any rescan already in flight - AddRoot and a
    /// manual "Rebuild now" both call this, so a rapid sequence of either only pays for one full walk.
    public static async Task RebuildAsync(CancellationToken cancellationToken)
    {
        var roots = RootsStore.Load();
        if (roots.Count == 0)
        {
            return;
        }

        _scanCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _scanCts = cts;

        IsScanning = true;
        StatusChanged?.Invoke(null, EventArgs.Empty);

        try
        {
            await Task.Run(() =>
            {
                var generation = DateTimeOffset.UtcNow.Ticks;
                using var connection = OpenConnection();

                foreach (var root in roots)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    if (!Directory.Exists(root))
                    {
                        continue;
                    }

                    using (var transaction = connection.BeginTransaction())
                    {
                        using var upsertCmd = CreateUpsertCommand(connection, transaction);
                        UpsertEntry(upsertCmd, root, root, Path.GetDirectoryName(root) ?? root, true, 0, Directory.GetLastWriteTimeUtc(root), root, generation);
                        ScanDirectory(root, root, generation, upsertCmd, cts.Token);
                        transaction.Commit();
                    }

                    using var cleanupCmd = connection.CreateCommand();
                    cleanupCmd.CommandText = "DELETE FROM Entries WHERE RootPath = @root AND ScanGeneration <> @gen";
                    cleanupCmd.Parameters.AddWithValue("@root", root);
                    cleanupCmd.Parameters.AddWithValue("@gen", generation);
                    cleanupCmd.ExecuteNonQuery();
                }

                WriteMeta(connection, "LastScanUtc", DateTimeOffset.UtcNow.Ticks.ToString());
            }, cts.Token).ConfigureAwait(false);

            LastScanUtc = DateTimeOffset.UtcNow;
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer rebuild request - not an error.
        }
        catch (SqliteException ex)
        {
            LoggingService.LogWarning("SearchIndexService.RebuildAsync", ex);
        }
        finally
        {
            IsScanning = false;
            RefreshEntryCount();
            StatusChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    /// Substring match on filename (SQL-side, index-backed - the only thing that scales to millions
    /// of rows per keystroke), then ranked with the same typo-tolerant FuzzyMatcher the per-pane
    /// search uses, for a consistent feel between the two search features.
    public static async Task<List<SearchIndexEntry>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new List<SearchIndexEntry>();
        }

        return await Task.Run(() =>
        {
            var candidates = new List<SearchIndexEntry>();

            using (var connection = OpenConnection())
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT Path, Name, DirectoryPath, IsDirectory, SizeBytes, ModifiedTicks FROM Entries WHERE Name LIKE @pattern ESCAPE '\\' LIMIT @limit";
                cmd.Parameters.AddWithValue("@pattern", "%" + EscapeLike(query) + "%");
                cmd.Parameters.AddWithValue("@limit", SqlCandidateLimit);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    candidates.Add(new SearchIndexEntry(
                        reader.GetString(0), reader.GetString(1), reader.GetString(2),
                        reader.GetInt64(3) != 0, reader.GetInt64(4),
                        new DateTimeOffset(reader.GetInt64(5), TimeSpan.Zero)));
                }
            }

            var scored = new List<(SearchIndexEntry Entry, int Score)>();
            foreach (var candidate in candidates)
            {
                if (FuzzyMatcher.TryScore(candidate.Name, query, out var score))
                {
                    scored.Add((candidate, score));
                }
            }

            return scored
                .OrderByDescending(s => s.Score)
                .Take(maxResults)
                .Select(s => s.Entry)
                .ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task PeriodicRescanLoopAsync()
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromHours(1)).ConfigureAwait(false);

            if (RootsStore.Load().Count > 0 &&
                (LastScanUtc is null || DateTimeOffset.UtcNow - LastScanUtc > TimeSpan.FromHours(RescanIntervalHours)))
            {
                await RebuildAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    // ----- Filesystem walk -----

    private static void ScanDirectory(string directory, string rootPath, long generation, SqliteCommand upsertCmd, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(directory);
        }
        // Caught per-directory (not once for the whole walk) so one access-denied folder deep in a
        // root doesn't abort indexing everything else under it.
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return;
        }

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            FileAttributes attrs;
            try
            {
                attrs = File.GetAttributes(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (attrs.HasFlag(FileAttributes.Hidden) || attrs.HasFlag(FileAttributes.System))
            {
                continue;
            }

            var isDirectory = attrs.HasFlag(FileAttributes.Directory);
            long size = 0;
            DateTime modified;

            try
            {
                if (isDirectory)
                {
                    modified = Directory.GetLastWriteTimeUtc(entry);
                }
                else
                {
                    var info = new FileInfo(entry);
                    size = info.Length;
                    modified = info.LastWriteTimeUtc;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            UpsertEntry(upsertCmd, entry, Path.GetFileName(entry), directory, isDirectory, size, modified, rootPath, generation);

            if (isDirectory)
            {
                ScanDirectory(entry, rootPath, generation, upsertCmd, cancellationToken);
            }
        }
    }

    // ----- Live watcher-driven updates -----

    private static void StartWatcher(string root)
    {
        lock (WatcherLock)
        {
            if (Watchers.ContainsKey(root) || !Directory.Exists(root))
            {
                return;
            }

            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    // A busy root (e.g. a large batch copy landing all at once) can overflow the
                    // default 8KB OS notification buffer and silently drop events - the periodic
                    // rescan is the backstop for whatever this still misses.
                    InternalBufferSize = 65536,
                };

                watcher.Created += (_, e) => EnqueueChange(new PendingChange(e.FullPath, null, WatcherChangeTypes.Created));
                watcher.Changed += (_, e) => EnqueueChange(new PendingChange(e.FullPath, null, WatcherChangeTypes.Changed));
                watcher.Deleted += (_, e) => EnqueueChange(new PendingChange(e.FullPath, null, WatcherChangeTypes.Deleted));
                watcher.Renamed += (_, e) => EnqueueChange(new PendingChange(e.FullPath, e.OldFullPath, WatcherChangeTypes.Renamed));
                watcher.EnableRaisingEvents = true;

                Watchers[root] = watcher;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                LoggingService.LogWarning($"SearchIndexService.StartWatcher: {root}", ex);
            }
        }
    }

    private static void StopWatcher(string root)
    {
        lock (WatcherLock)
        {
            if (Watchers.Remove(root, out var watcher))
            {
                watcher.Dispose();
            }
        }
    }

    private static void EnqueueChange(PendingChange change)
    {
        PendingChanges.Enqueue(change);

        lock (WatcherLock)
        {
            _flushTimer ??= new Timer(_ => FlushPendingChanges(), null, WatcherFlushDelayMs, Timeout.Infinite);
            _flushTimer.Change(WatcherFlushDelayMs, Timeout.Infinite);
        }
    }

    private static void FlushPendingChanges()
    {
        var changes = new List<PendingChange>();
        while (PendingChanges.TryDequeue(out var change))
        {
            changes.Add(change);
        }

        if (changes.Count == 0)
        {
            return;
        }

        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var upsertCmd = CreateUpsertCommand(connection, transaction);
            using var deleteCmd = connection.CreateCommand();
            deleteCmd.Transaction = transaction;
            // Also removes anything under a deleted/renamed-away directory - Windows fires one
            // Deleted/Renamed event for the top of a removed tree, not one per descendant.
            deleteCmd.CommandText = "DELETE FROM Entries WHERE Path = @p OR Path LIKE @prefix ESCAPE '\\'";
            deleteCmd.Parameters.Add("@p", SqliteType.Text);
            deleteCmd.Parameters.Add("@prefix", SqliteType.Text);

            foreach (var change in changes)
            {
                var removedPath = change.ChangeType == WatcherChangeTypes.Deleted
                    ? change.Path
                    : change.OldPath;

                if (removedPath is not null)
                {
                    deleteCmd.Parameters["@p"].Value = removedPath;
                    deleteCmd.Parameters["@prefix"].Value = EscapeLike(removedPath) + "\\%";
                    deleteCmd.ExecuteNonQuery();
                }

                if (change.ChangeType != WatcherChangeTypes.Deleted)
                {
                    UpsertPathIfExists(upsertCmd, change.Path);
                }
            }

            transaction.Commit();
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            LoggingService.LogWarning("SearchIndexService.FlushPendingChanges", ex);
        }

        RefreshEntryCount();
        StatusChanged?.Invoke(null, EventArgs.Empty);
    }

    private static void UpsertPathIfExists(SqliteCommand upsertCmd, string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            if (attrs.HasFlag(FileAttributes.Hidden) || attrs.HasFlag(FileAttributes.System))
            {
                return;
            }

            var isDirectory = attrs.HasFlag(FileAttributes.Directory);
            var directory = Path.GetDirectoryName(path) ?? path;
            var rootPath = RootsStore.Load().FirstOrDefault(r => path.StartsWith(r, StringComparison.OrdinalIgnoreCase));
            if (rootPath is null)
            {
                return;
            }

            long size = 0;
            DateTime modified;
            if (isDirectory)
            {
                modified = Directory.GetLastWriteTimeUtc(path);
            }
            else
            {
                var info = new FileInfo(path);
                size = info.Length;
                modified = info.LastWriteTimeUtc;
            }

            // -1 is a sentinel generation for watcher-driven single-row updates, distinct from any
            // real RebuildAsync generation (DateTimeOffset ticks) - a full rescan's stale-row cleanup
            // deletes by "ScanGeneration <> this scan's generation", so a -1 row surviving to the next
            // rescan just gets naturally re-upserted with a real generation during that walk.
            UpsertEntry(upsertCmd, path, Path.GetFileName(path), directory, isDirectory, size, modified, rootPath, -1);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Gone by the time we got to it (rapid create+delete) - fine, the next full rescan
            // reconciles anything still wrong.
        }
    }

    // ----- SQLite plumbing -----

    private static SqliteConnection OpenConnection()
    {
        Directory.CreateDirectory(DbDirectory);
        var connection = new SqliteConnection($"Data Source={DbPath}");
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        pragma.ExecuteNonQuery();

        return connection;
    }

    private static void EnsureSchema()
    {
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Entries (
                Path TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                DirectoryPath TEXT NOT NULL,
                IsDirectory INTEGER NOT NULL,
                SizeBytes INTEGER NOT NULL,
                ModifiedTicks INTEGER NOT NULL,
                RootPath TEXT NOT NULL,
                ScanGeneration INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Entries_Name ON Entries(Name);
            CREATE INDEX IF NOT EXISTS IX_Entries_RootPath ON Entries(RootPath);
            CREATE TABLE IF NOT EXISTS Meta (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
    }

    private static SqliteCommand CreateUpsertCommand(SqliteConnection connection, SqliteTransaction transaction)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO Entries (Path, Name, DirectoryPath, IsDirectory, SizeBytes, ModifiedTicks, RootPath, ScanGeneration)
            VALUES (@path, @name, @dir, @isDir, @size, @modified, @root, @gen)
            ON CONFLICT(Path) DO UPDATE SET
                Name = excluded.Name, DirectoryPath = excluded.DirectoryPath, IsDirectory = excluded.IsDirectory,
                SizeBytes = excluded.SizeBytes, ModifiedTicks = excluded.ModifiedTicks, RootPath = excluded.RootPath,
                ScanGeneration = excluded.ScanGeneration;
            """;
        cmd.Parameters.Add("@path", SqliteType.Text);
        cmd.Parameters.Add("@name", SqliteType.Text);
        cmd.Parameters.Add("@dir", SqliteType.Text);
        cmd.Parameters.Add("@isDir", SqliteType.Integer);
        cmd.Parameters.Add("@size", SqliteType.Integer);
        cmd.Parameters.Add("@modified", SqliteType.Integer);
        cmd.Parameters.Add("@root", SqliteType.Text);
        cmd.Parameters.Add("@gen", SqliteType.Integer);
        return cmd;
    }

    private static void UpsertEntry(SqliteCommand cmd, string path, string name, string directory, bool isDirectory, long size, DateTime modifiedUtc, string root, long generation)
    {
        cmd.Parameters["@path"].Value = path;
        cmd.Parameters["@name"].Value = name;
        cmd.Parameters["@dir"].Value = directory;
        cmd.Parameters["@isDir"].Value = isDirectory ? 1 : 0;
        cmd.Parameters["@size"].Value = size;
        cmd.Parameters["@modified"].Value = modifiedUtc.Ticks;
        cmd.Parameters["@root"].Value = root;
        cmd.Parameters["@gen"].Value = generation;
        cmd.ExecuteNonQuery();
    }

    private static void RefreshEntryCount()
    {
        try
        {
            using var connection = OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Entries";
            EntryCount = Convert.ToInt32(cmd.ExecuteScalar());
        }
        catch (SqliteException ex)
        {
            LoggingService.LogWarning("SearchIndexService.RefreshEntryCount", ex);
        }
    }

    private static void WriteMeta(SqliteConnection connection, string key, string value)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO Meta (Key, Value) VALUES (@k, @v) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value";
        cmd.Parameters.AddWithValue("@k", key);
        cmd.Parameters.AddWithValue("@v", value);
        cmd.ExecuteNonQuery();
    }

    private static string? ReadMeta(string key)
    {
        try
        {
            using var connection = OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Value FROM Meta WHERE Key = @k";
            cmd.Parameters.AddWithValue("@k", key);
            return cmd.ExecuteScalar() as string;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    private static string NormalizeRoot(string path)
    {
        var trimmed = path.TrimEnd('\\');
        return trimmed.Length == 2 && trimmed[1] == ':' ? trimmed + "\\" : trimmed;
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
