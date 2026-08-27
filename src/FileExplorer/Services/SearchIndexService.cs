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

    // Commits every BatchCommitSize upserts instead of holding one transaction open for an entire
    // (potentially multi-hour, multi-million-row) root scan, so a stall or interruption doesn't lose
    // everything scanned since the walk started.
    private const int BatchCommitSize = 2000;

    // Directory/File APIs are plain blocking Win32 calls with no cancellation support - a genuinely
    // unresponsive drive (spun down, a failing USB/SATA bridge, a bad sector causing driver-level
    // retries) can block the calling thread forever with no way to interrupt it, which is exactly
    // what happened during testing on a large multi-drive DAS array: disk activity stopped, the
    // entry count froze, and IsScanning never cleared because the scan thread was permanently stuck
    // inside one blocking call. These timeouts bound that - see TryRunWithTimeout.
    private const int DirectoryEnumerationTimeoutSeconds = 60;
    private const int PerEntryStatTimeoutSeconds = 15;

    private static readonly JsonFileStore<List<string>> RootsStore = new("search-index-roots.json", () => new List<string>());

    private static readonly object WatcherLock = new();
    private static readonly Dictionary<string, FileSystemWatcher> Watchers = new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentQueue<PendingChange> PendingChanges = new();
    private static Timer? _flushTimer;
    private static CancellationTokenSource? _scanCts;
    private static bool _started;
    private static int _scanProgressCount;
    private static DateTime _lastProgressNotifyUtc = DateTime.MinValue;

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
    public static Task RebuildAsync(CancellationToken cancellationToken) => RebuildRootsAsync(RootsStore.Load(), cancellationToken);

    /// Rescans just one configured root, leaving every other root's index untouched - lets a single
    /// location be refreshed/re-tested without paying for a full multi-root rebuild. Still supersedes
    /// (cancels) any other rescan in flight, full or single-root, since only one scan runs at a time.
    public static Task RebuildRootAsync(string root, CancellationToken cancellationToken) => RebuildRootsAsync(new List<string> { root }, cancellationToken);

    private const string TraceSource = "SearchIndexService.RebuildRootsAsync";

    private static async Task RebuildRootsAsync(List<string> roots, CancellationToken cancellationToken)
    {
        if (roots.Count == 0)
        {
            return;
        }

        LoggingService.LogInfo(TraceSource, $"Starting: roots=[{string.Join(", ", roots)}]");

        _scanCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _scanCts = cts;

        IsScanning = true;
        _scanProgressCount = 0;
        _lastProgressNotifyUtc = DateTime.MinValue;
        StatusChanged?.Invoke(null, EventArgs.Empty);

        try
        {
            await Task.Run(() =>
            {
                LoggingService.LogInfo(TraceSource, "Task.Run body entered");

                var generation = DateTimeOffset.UtcNow.Ticks;
                using var connection = OpenConnection();

                foreach (var root in roots)
                {
                    LoggingService.LogInfo(TraceSource, $"Root '{root}': starting");
                    cts.Token.ThrowIfCancellationRequested();
                    if (!Directory.Exists(root))
                    {
                        LoggingService.LogInfo(TraceSource, $"Root '{root}': Directory.Exists false, skipping");
                        continue;
                    }

                    if (!TryRunWithTimeout(() => Directory.GetLastWriteTimeUtc(root), TimeSpan.FromSeconds(PerEntryStatTimeoutSeconds), out var rootModified))
                    {
                        LoggingService.LogWarning($"SearchIndexService.RebuildRootsAsync: {root} took longer than {PerEntryStatTimeoutSeconds}s to stat (drive unresponsive?) - skipping it this pass", new TimeoutException());
                        continue;
                    }

                    using (var batch = new ScanBatchWriter(connection))
                    {
                        batch.Upsert(root, root, Path.GetDirectoryName(root) ?? root, true, 0, rootModified, root, generation);
                        NotifyScanProgress();
                        ScanDirectory(root, root, generation, batch, cts.Token);
                    }
                    LoggingService.LogInfo(TraceSource, $"Root '{root}': walk + batch writer disposed (final commit done), entries so far={_scanProgressCount}");

                    using var cleanupCmd = connection.CreateCommand();
                    cleanupCmd.CommandText = "DELETE FROM Entries WHERE RootPath = @root AND ScanGeneration <> @gen";
                    cleanupCmd.Parameters.AddWithValue("@root", root);
                    cleanupCmd.Parameters.AddWithValue("@gen", generation);
                    cleanupCmd.ExecuteNonQuery();
                    LoggingService.LogInfo(TraceSource, $"Root '{root}': stale-row cleanup DELETE done");
                }

                WriteMeta(connection, "LastScanUtc", DateTimeOffset.UtcNow.Ticks.ToString());
                LoggingService.LogInfo(TraceSource, "Task.Run body: WriteMeta done, about to return (connection will Dispose)");
            }, cts.Token).ConfigureAwait(false);

            LoggingService.LogInfo(TraceSource, "await Task.Run returned successfully");
            LastScanUtc = DateTimeOffset.UtcNow;
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer rebuild request - not an error.
            LoggingService.LogInfo(TraceSource, "Cancelled (superseded by a newer rebuild request)");
        }
        catch (Exception ex)
        {
            // Was `catch (SqliteException ex)` - broadened to catch-all so an unexpected exception
            // type can never silently escape this method as an unobserved faulted Task (this method
            // is always called fire-and-forget via `_ = ...`) without IsScanning/StatusChanged below
            // ever running, which would leave the UI showing "scanning" forever with no error logged.
            LoggingService.LogWarning("SearchIndexService.RebuildRootsAsync", ex);
        }
        finally
        {
            LoggingService.LogInfo(TraceSource, "Entering finally");
            IsScanning = false;

            try
            {
                RefreshEntryCount();
            }
            catch (Exception ex)
            {
                // RefreshEntryCount already catches SqliteException internally, but guard against any
                // other exception type here too - this finally block must reach StatusChanged below
                // no matter what, or the UI never learns the scan ended.
                LoggingService.LogWarning("SearchIndexService.RebuildRootsAsync: RefreshEntryCount in finally", ex);
            }

            LoggingService.LogInfo(TraceSource, $"Finally: IsScanning={IsScanning}, EntryCount={EntryCount} - about to fire StatusChanged");
            StatusChanged?.Invoke(null, EventArgs.Empty);
            LoggingService.LogInfo(TraceSource, "Finally: StatusChanged fired, returning");
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

    /// Commits every BatchCommitSize upserts on its own transaction instead of holding one open for
    /// an entire (potentially multi-hour, multi-million-row) root scan - see BatchCommitSize.
    private sealed class ScanBatchWriter : IDisposable
    {
        private readonly SqliteConnection _connection;
        private SqliteTransaction _transaction;
        private SqliteCommand _upsertCmd;
        private int _countInBatch;

        public ScanBatchWriter(SqliteConnection connection)
        {
            _connection = connection;
            _transaction = connection.BeginTransaction();
            _upsertCmd = CreateUpsertCommand(connection, _transaction);
        }

        public void Upsert(string path, string name, string directory, bool isDirectory, long size, DateTime modifiedUtc, string root, long generation)
        {
            UpsertEntry(_upsertCmd, path, name, directory, isDirectory, size, modifiedUtc, root, generation);

            if (++_countInBatch >= BatchCommitSize)
            {
                Flush();
            }
        }

        private void Flush()
        {
            _transaction.Commit();
            _upsertCmd.Dispose();
            _transaction.Dispose();
            _transaction = _connection.BeginTransaction();
            _upsertCmd = CreateUpsertCommand(_connection, _transaction);
            _countInBatch = 0;
        }

        public void Dispose()
        {
            _transaction.Commit();
            _upsertCmd.Dispose();
            _transaction.Dispose();
        }
    }

    private static void ScanDirectory(string directory, string rootPath, long generation, ScanBatchWriter batch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        List<string> entries;
        try
        {
            if (!TryRunWithTimeout(() => Directory.EnumerateFileSystemEntries(directory).ToList(), TimeSpan.FromSeconds(DirectoryEnumerationTimeoutSeconds), out var result))
            {
                LoggingService.LogWarning($"SearchIndexService.ScanDirectory: {directory} took longer than {DirectoryEnumerationTimeoutSeconds}s to enumerate (drive unresponsive?) - skipping", new TimeoutException());
                return;
            }

            entries = result!;
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

            (FileAttributes Attrs, bool IsDirectory, long Size, DateTime Modified) stat;
            try
            {
                if (!TryRunWithTimeout(() => StatEntry(entry), TimeSpan.FromSeconds(PerEntryStatTimeoutSeconds), out var result))
                {
                    LoggingService.LogWarning($"SearchIndexService.ScanDirectory: {entry} took longer than {PerEntryStatTimeoutSeconds}s to stat (drive unresponsive?) - skipping", new TimeoutException());
                    continue;
                }

                stat = result;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (stat.Attrs.HasFlag(FileAttributes.Hidden) || stat.Attrs.HasFlag(FileAttributes.System))
            {
                continue;
            }

            batch.Upsert(entry, Path.GetFileName(entry), directory, stat.IsDirectory, stat.Size, stat.Modified, rootPath, generation);
            NotifyScanProgress();

            if (stat.IsDirectory)
            {
                ScanDirectory(entry, rootPath, generation, batch, cancellationToken);
            }
        }
    }

    private static (FileAttributes Attrs, bool IsDirectory, long Size, DateTime Modified) StatEntry(string entry)
    {
        var attrs = File.GetAttributes(entry);
        var isDirectory = attrs.HasFlag(FileAttributes.Directory);

        if (isDirectory)
        {
            return (attrs, true, 0, Directory.GetLastWriteTimeUtc(entry));
        }

        var info = new FileInfo(entry);
        return (attrs, false, info.Length, info.LastWriteTimeUtc);
    }

    /// Runs a synchronous filesystem operation with a watchdog timeout. Directory/File APIs are
    /// plain blocking Win32 calls with no cancellation support, so a genuinely unresponsive drive can
    /// block the calling thread forever with no way to interrupt it - see the remark on
    /// DirectoryEnumerationTimeoutSeconds above for the real-world case this was added for. On
    /// timeout this abandons the underlying thread-pool thread (it may stay blocked, potentially
    /// forever - a small leak bounded to genuinely stuck operations, not something that happens in
    /// normal operation) and lets the scan move on rather than hanging indefinitely. On completion
    /// within the timeout, GetAwaiter().GetResult() rethrows the operation's original exception type
    /// unwrapped (no AggregateException), so existing per-item catch blocks around call sites work
    /// exactly as if the operation had been called inline.
    ///
    /// Deliberately uses Task.WaitAny, not Task.Wait/Task.Result, for the timeout race: unlike
    /// GetAwaiter().GetResult(), both of those throw an AggregateException the instant the task
    /// *faults* - including a fault that happens well within the timeout window, not just on a real
    /// timeout - which would bypass every IOException/UnauthorizedAccessException catch block at the
    /// call sites below and crash the app outright. This was a real, confirmed bug: an access-denied
    /// stat during a watcher-triggered update reached Application-level unhandled-exception and
    /// killed the process a few seconds after launch. WaitAny only reports whether the task reached
    /// *some* terminal state in time, without touching its result/exception, so the fault is only
    /// (safely, unwrapped) observed afterward via GetAwaiter().GetResult().
    private static bool TryRunWithTimeout<T>(Func<T> operation, TimeSpan timeout, out T result)
    {
        var task = Task.Run(operation);

        if (Task.WaitAny(new Task[] { task }, timeout) == -1)
        {
            result = default!;
            return false;
        }

        result = task.GetAwaiter().GetResult();
        return true;
    }

    /// Runs on the background scan thread (Task.Run in RebuildAsync) - EntryCount is read from the
    /// UI thread via StatusChanged subscribers, which is safe here since it's only ever a
    /// monotonically-increasing int write with no compound state to tear. Throttled to avoid firing
    /// a UI update per file on a fast local scan.
    private static void NotifyScanProgress()
    {
        var count = Interlocked.Increment(ref _scanProgressCount);
        EntryCount = count;

        var now = DateTime.UtcNow;
        if ((now - _lastProgressNotifyUtc).TotalMilliseconds < 300)
        {
            return;
        }

        _lastProgressNotifyUtc = now;
        StatusChanged?.Invoke(null, EventArgs.Empty);
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
        (FileAttributes Attrs, bool IsDirectory, long Size, DateTime Modified) stat;
        try
        {
            if (!TryRunWithTimeout(() => StatEntry(path), TimeSpan.FromSeconds(PerEntryStatTimeoutSeconds), out var result))
            {
                LoggingService.LogWarning($"SearchIndexService.UpsertPathIfExists: {path} took longer than {PerEntryStatTimeoutSeconds}s to stat (drive unresponsive?) - skipping", new TimeoutException());
                return;
            }

            stat = result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Gone by the time we got to it (rapid create+delete) - fine, the next full rescan
            // reconciles anything still wrong.
            return;
        }

        if (stat.Attrs.HasFlag(FileAttributes.Hidden) || stat.Attrs.HasFlag(FileAttributes.System))
        {
            return;
        }

        var directory = Path.GetDirectoryName(path) ?? path;
        var rootPath = RootsStore.Load().FirstOrDefault(r => path.StartsWith(r, StringComparison.OrdinalIgnoreCase));
        if (rootPath is null)
        {
            return;
        }

        // -1 is a sentinel generation for watcher-driven single-row updates, distinct from any real
        // RebuildRootsAsync generation (DateTimeOffset ticks) - a full rescan's stale-row cleanup
        // deletes by "ScanGeneration <> this scan's generation", so a -1 row surviving to the next
        // rescan just gets naturally re-upserted with a real generation during that walk.
        UpsertEntry(upsertCmd, path, Path.GetFileName(path), directory, stat.IsDirectory, stat.Size, stat.Modified, rootPath, -1);
    }

    // ----- SQLite plumbing -----

    private static SqliteConnection OpenConnection()
    {
        Directory.CreateDirectory(DbDirectory);
        var connection = new SqliteConnection($"Data Source={DbPath}");
        connection.Open();

        using var pragma = connection.CreateCommand();
        // busy_timeout matters more than it looks: WAL mode allows concurrent *readers* during a
        // write, but still only one *writer* at a time - without this, a second connection trying to
        // write (e.g. the debounced FileSystemWatcher flush landing while a root rescan's own
        // connection holds the write lock) fails immediately with "database is locked" (SQLITE_BUSY)
        // instead of waiting a moment for the first writer to finish. Confirmed via app.log: repeated
        // "database is locked" warnings from FlushPendingChanges while a scan was running, silently
        // dropping whatever watcher updates arrived during that window. 10s is generous relative to
        // how long a single batch commit takes, without risking a search query feeling laggy (reads
        // don't hit this path in WAL mode - only writer-vs-writer contention does).
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=10000;";
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

    /// Combined size on disk of the SQLite database plus its WAL/shared-memory sidecar files (WAL
    /// mode keeps recently-written pages there until a checkpoint folds them back into the main
    /// file, so ignoring them would under-report actual disk usage).
    public static long DatabaseSizeBytes
    {
        get
        {
            long size = 0;
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var path = DbPath + suffix;
                if (File.Exists(path))
                {
                    size += new FileInfo(path).Length;
                }
            }
            return size;
        }
    }

    /// Entry count per configured root, for Control Centre's Search Index list. One grouped query
    /// rather than one COUNT per root.
    public static Dictionary<string, int> GetRootEntryCounts()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var connection = OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT RootPath, COUNT(*) FROM Entries GROUP BY RootPath";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                counts[reader.GetString(0)] = reader.GetInt32(1);
            }
        }
        catch (SqliteException ex)
        {
            LoggingService.LogWarning("SearchIndexService.GetRootEntryCounts", ex);
        }

        return counts;
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
