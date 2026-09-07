using System.Collections.Concurrent;
using System.Security.Cryptography;
using FileExplorer.Helpers;
using Microsoft.Data.Sqlite;

namespace FileExplorer.Services;

public sealed record SearchIndexEntry(string Path, string Name, string DirectoryPath, bool IsDirectory, long SizeBytes, DateTimeOffset Modified, double? Rating = null);

/// A file row from the index reduced to what duplicate detection needs: its path, size in bytes and
/// (when the indexer managed to compute it) MD5 hash. Md5Hash is null when hashing was skipped or
/// failed during indexing - callers fall back to hashing that file from disk.
public sealed record IndexedFile(string Path, long SizeBytes, string? Md5Hash);

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

    // Hashing reads the whole file, so it gets a far more generous watchdog than a plain stat - a
    // multi-GB file on a spinning disk can legitimately take minutes. On timeout the file is still
    // indexed (name/size/modified), just with a null hash, and duplicate detection hashes it from
    // disk on demand instead.
    private const int PerEntryHashTimeoutSeconds = 300;

    private static readonly JsonFileStore<List<string>> RootsStore = new("search-index-roots.json", () => new List<string>());

    private static readonly object WatcherLock = new();
    private static readonly Dictionary<string, FileSystemWatcher> Watchers = new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentQueue<PendingChange> PendingChanges = new();
    private static Timer? _flushTimer;
    private static CancellationTokenSource? _scanCts;
    private static bool _started;
    private static int _scanProgressCount;
    private static DateTime _lastProgressNotifyUtc = DateTime.MinValue;

    // True once the trigram FTS index has been fully populated (a Meta flag persists this across
    // launches). Until then SearchAsync uses the slower LIKE scan - the FTS table exists and its
    // triggers keep it current, but a MATCH against a half-built index would miss rows.
    private static volatile bool _ftsReady;

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
        _ftsReady = ReadMeta("FtsBuilt") == "1";

        LastScanUtc = ReadMeta("LastScanUtc") is { } raw && long.TryParse(raw, out var ticks)
            ? new DateTimeOffset(ticks, TimeSpan.Zero)
            : null;

        foreach (var root in RootsStore.Load())
        {
            StartWatcher(root);
        }

        _ = StartupBackgroundWorkAsync();
    }

    /// The one-time trigram-FTS population runs before the periodic rescan loop so the big single
    /// write it does isn't fighting a full filesystem walk for the lone WAL writer slot. Once the
    /// FtsBuilt flag is set this returns immediately and only the rescan loop keeps running.
    private static async Task StartupBackgroundWorkAsync()
    {
        await EnsureFtsPopulatedAsync().ConfigureAwait(false);
        _ = HashBackfillLoopAsync();
        await PeriodicRescanLoopAsync().ConfigureAwait(false);
    }

    private const int HashBackfillBatchSize = 250;

    // Written into Md5Hash for a file that was picked for hashing but couldn't be read (locked,
    // permission, timed out). Distinguishes "tried, unavailable" from "not tried yet" (NULL) so the
    // backfill loop doesn't pick the same unreadable file every pass forever. Not a valid 32-char
    // hex digest, so duplicate detection treats it exactly like a missing hash.
    private const string HashUnavailable = "";

    /// Fills in Md5Hash, after the walk, for indexed files that still have none AND share their exact
    /// byte size with another indexed file - the only files whose hash duplicate detection can ever
    /// need (a size-unique file can't have a duplicate). Runs forever at low priority: a small batch,
    /// a short pause, a long sleep when there's nothing to do, and never while a rescan is running
    /// (they'd fight over the single WAL writer). This is what lets an index-backed duplicate scan
    /// skip reading files from disk without the walk itself ever having to.
    private static async Task HashBackfillLoopAsync()
    {
        while (true)
        {
            try
            {
                if (IsScanning || RootsStore.Load().Count == 0)
                {
                    await Task.Delay(TimeSpan.FromMinutes(2)).ConfigureAwait(false);
                    continue;
                }

                var candidates = GetHashBackfillCandidates(HashBackfillBatchSize);
                if (candidates.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromMinutes(10)).ConfigureAwait(false);
                    continue;
                }

                var results = new List<(string Path, string Hash)>();
                foreach (var path in candidates)
                {
                    if (IsScanning)
                    {
                        break;
                    }

                    results.Add(TryRunWithTimeout(() => TryComputeMd5(path), TimeSpan.FromSeconds(PerEntryHashTimeoutSeconds), out var hash) && hash is { Length: 32 }
                        ? (path, hash)
                        : (path, HashUnavailable));
                }

                if (results.Count > 0)
                {
                    WriteBackfilledHashes(results);
                }

                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LoggingService.LogWarning("SearchIndexService.HashBackfillLoopAsync", ex);
                await Task.Delay(TimeSpan.FromMinutes(5)).ConfigureAwait(false);
            }
        }
    }

    private static List<string> GetHashBackfillCandidates(int limit)
    {
        var candidates = new List<string>();

        try
        {
            using var connection = OpenConnection();
            using var cmd = connection.CreateCommand();
            // Smallest colliding files first: cheapest to hash and by far the most common source of
            // real duplicates. The size-collision subquery rides IX_Entries_SizeHash.
            cmd.CommandText = """
                SELECT Path FROM Entries
                WHERE Md5Hash IS NULL AND IsDirectory = 0 AND SizeBytes > 0
                  AND SizeBytes IN (
                      SELECT SizeBytes FROM Entries
                      WHERE IsDirectory = 0 AND SizeBytes > 0
                      GROUP BY SizeBytes HAVING COUNT(*) > 1
                  )
                ORDER BY SizeBytes
                LIMIT @limit
                """;
            cmd.Parameters.AddWithValue("@limit", limit);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                candidates.Add(reader.GetString(0));
            }
        }
        catch (SqliteException ex)
        {
            LoggingService.LogWarning("SearchIndexService.GetHashBackfillCandidates", ex);
        }

        return candidates;
    }

    private static void WriteBackfilledHashes(List<(string Path, string Hash)> hashes)
    {
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "UPDATE Entries SET Md5Hash = @h WHERE Path = @p AND Md5Hash IS NULL";
            var ph = cmd.Parameters.Add("@h", SqliteType.Text);
            var pp = cmd.Parameters.Add("@p", SqliteType.Text);

            foreach (var (path, hash) in hashes)
            {
                ph.Value = hash;
                pp.Value = path;
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
        {
            LoggingService.LogWarning("SearchIndexService.WriteBackfilledHashes", ex);
        }
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
        // Reset the visible counter too - while IsScanning it reads as "N entries so far", so it must
        // count up from this scan's progress, not linger on the previous scan's final total.
        EntryCount = 0;
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
                    cts.Token.ThrowIfCancellationRequested();

                    // Per-root guard: a single unreadable/disconnected drive, or a "database is
                    // locked" on this root's stale-row cleanup, must not abort the whole multi-root
                    // scan and (worse) skip the LastScanUtc write + every other root's cleanup below,
                    // which is exactly how the index ended up carrying rows from several old scan
                    // generations at once. OperationCanceledException still propagates - that's a
                    // deliberate supersede, not a failure.
                    try
                    {
                        LoggingService.LogInfo(TraceSource, $"Root '{root}': starting");
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
                            batch.Upsert(root, root, Path.GetDirectoryName(root) ?? root, true, 0, rootModified, null, root, generation);
                            NotifyScanProgress();
                            ScanDirectory(root, root, generation, batch, cts.Token);
                        }
                        LoggingService.LogInfo(TraceSource, $"Root '{root}': walk + batch writer disposed (final commit done), entries so far={_scanProgressCount}");

                        // Delete stale rows in bounded chunks rather than one big statement: the
                        // per-row FTS delete-trigger makes a multi-thousand-row delete a long single
                        // write that loses the busy_timeout race with a watcher flush and throws
                        // "database is locked", which used to leave old scan generations behind.
                        var removed = 0;
                        while (true)
                        {
                            cts.Token.ThrowIfCancellationRequested();
                            using var cleanupCmd = connection.CreateCommand();
                            cleanupCmd.CommandText = "DELETE FROM Entries WHERE rowid IN (SELECT rowid FROM Entries WHERE RootPath = @root AND ScanGeneration <> @gen LIMIT 5000)";
                            cleanupCmd.Parameters.AddWithValue("@root", root);
                            cleanupCmd.Parameters.AddWithValue("@gen", generation);
                            var chunk = cleanupCmd.ExecuteNonQuery();
                            removed += chunk;
                            if (chunk < 5000)
                            {
                                break;
                            }
                        }
                        LoggingService.LogInfo(TraceSource, $"Root '{root}': stale-row cleanup DELETE done ({removed} rows)");
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        LoggingService.LogWarning($"SearchIndexService.RebuildRootsAsync: root '{root}' failed - continuing with the remaining roots", ex);
                    }
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
    /// <param name="minRating">When &gt; 0, only results whose effective rating is at least this
    /// many stars are returned, and results are re-ordered highest-rating-first (fuzzy score breaks
    /// ties). 0 leaves ranking purely by name match.</param>
    public static async Task<List<SearchIndexEntry>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken, int minRating = 0)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new List<SearchIndexEntry>();
        }

        return await Task.Run(() =>
        {
            var candidates = new List<SearchIndexEntry>();
            var trimmed = query.Trim();

            static SearchIndexEntry ReadEntry(SqliteDataReader r) => new(
                r.GetString(0), r.GetString(1), r.GetString(2),
                r.GetInt64(3) != 0, r.GetInt64(4),
                new DateTimeOffset(r.GetInt64(5), TimeSpan.Zero));

            using (var connection = OpenConnection())
            {
                void RunLikeScan()
                {
                    candidates.Clear();
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = "SELECT Path, Name, DirectoryPath, IsDirectory, SizeBytes, ModifiedTicks FROM Entries WHERE Name LIKE @pattern ESCAPE '\\' LIMIT @limit";
                    cmd.Parameters.AddWithValue("@pattern", "%" + EscapeLike(query) + "%");
                    cmd.Parameters.AddWithValue("@limit", SqlCandidateLimit);

                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        candidates.Add(ReadEntry(reader));
                    }
                }

                // The trigram tokenizer indexes 3-character windows, so a MATCH needs at least 3
                // characters - shorter queries still go through the LIKE scan.
                if (_ftsReady && trimmed.Length >= 3)
                {
                    try
                    {
                        using var cmd = connection.CreateCommand();
                        cmd.CommandText = "SELECT e.Path, e.Name, e.DirectoryPath, e.IsDirectory, e.SizeBytes, e.ModifiedTicks " +
                                          "FROM EntriesFts f JOIN Entries e ON e.rowid = f.rowid " +
                                          "WHERE f.Name MATCH @q LIMIT @limit";
                        // Wrap as an FTS5 phrase literal (doubling any embedded quote) so punctuation
                        // and spaces in the query are matched verbatim as a contiguous substring
                        // rather than parsed as FTS query operators.
                        cmd.Parameters.AddWithValue("@q", "\"" + trimmed.Replace("\"", "\"\"") + "\"");
                        cmd.Parameters.AddWithValue("@limit", SqlCandidateLimit);

                        using var reader = cmd.ExecuteReader();
                        while (reader.Read())
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            candidates.Add(ReadEntry(reader));
                        }
                    }
                    catch (SqliteException ex)
                    {
                        LoggingService.LogWarning("SearchIndexService.SearchAsync: trigram FTS query failed, falling back to LIKE", ex);
                        RunLikeScan();
                    }
                }
                else
                {
                    RunLikeScan();
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

            var ranked = scored.OrderByDescending(s => s.Score).Select(s => s.Entry);

            if (minRating > 0)
            {
                // A rating filter is active: resolve every candidate's effective rating, keep those
                // at/above the threshold, and float higher ratings to the top (OrderBy is stable, so
                // the fuzzy-score order is preserved within each rating band).
                return ranked
                    .Select(e => e with { Rating = RatingService.GetEffective(e.Path, e.IsDirectory)?.Value })
                    .Where(e => e.Rating is { } r && r >= minRating - 0.0001)
                    .OrderByDescending(e => e.Rating)
                    .Take(maxResults)
                    .ToList();
            }

            return ranked
                .Take(maxResults)
                .Select(e => e with { Rating = RatingService.GetEffective(e.Path, e.IsDirectory)?.Value })
                .ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task PeriodicRescanLoopAsync()
    {
        // Short initial delay rather than a full hour: if the index is stale or was left incomplete
        // by a previous run (e.g. every scan aborting before its cleanup), this is what heals it, and
        // waiting an hour to start just leaves the user looking at wrong counts and an old scan date.
        await Task.Delay(TimeSpan.FromSeconds(20)).ConfigureAwait(false);

        while (true)
        {
            if (RootsStore.Load().Count > 0 &&
                (LastScanUtc is null || DateTimeOffset.UtcNow - LastScanUtc > TimeSpan.FromHours(RescanIntervalHours)))
            {
                await RebuildAsync(CancellationToken.None).ConfigureAwait(false);
            }

            await Task.Delay(TimeSpan.FromHours(1)).ConfigureAwait(false);
        }
    }

    // ----- Filesystem walk -----

    /// Buffers up to BatchCommitSize rows in memory, then writes them in one short transaction and
    /// clears the buffer. Deliberately holds NO transaction between flushes: the walk enumerates
    /// directories and (since the MD5 column was added) hashes whole files between Upsert calls, which
    /// can take seconds to minutes per file - an open write transaction spanning that would starve
    /// the debounced FileSystemWatcher flush (a separate connection) of the single WAL writer slot
    /// until its busy_timeout expired, silently dropping live updates. It also made the batch
    /// writer's own state unrecoverable if BeginTransaction/Commit ever hit SQLITE_BUSY mid-scan,
    /// which aborted the entire scan (and skipped the stale-row cleanup) via an
    /// InvalidOperationException from Dispose re-committing a rolled-back transaction. A failed flush
    /// now just drops that one batch and logs it - the next full rescan reconciles whatever was lost.
    private sealed class ScanBatchWriter : IDisposable
    {
        private readonly SqliteConnection _connection;

        private readonly record struct Row(
            string Path, string Name, string Directory, bool IsDirectory,
            long Size, DateTime ModifiedUtc, string? Md5, string Root, long Generation);

        private readonly List<Row> _buffer = new();

        public ScanBatchWriter(SqliteConnection connection) => _connection = connection;

        public void Upsert(string path, string name, string directory, bool isDirectory, long size, DateTime modifiedUtc, string? md5Hash, string root, long generation)
        {
            _buffer.Add(new Row(path, name, directory, isDirectory, size, modifiedUtc, md5Hash, root, generation));

            if (_buffer.Count >= BatchCommitSize)
            {
                Flush();
            }
        }

        private void Flush()
        {
            if (_buffer.Count == 0)
            {
                return;
            }

            try
            {
                using var transaction = _connection.BeginTransaction();
                using var upsertCmd = CreateUpsertCommand(_connection, transaction);

                foreach (var row in _buffer)
                {
                    UpsertEntry(upsertCmd, row.Path, row.Name, row.Directory, row.IsDirectory, row.Size, row.ModifiedUtc, row.Md5, row.Root, row.Generation);
                }

                transaction.Commit();
            }
            catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
            {
                LoggingService.LogWarning($"SearchIndexService.ScanBatchWriter.Flush: dropping a batch of {_buffer.Count} entries", ex);
            }
            finally
            {
                _buffer.Clear();
            }
        }

        public void Dispose() => Flush();
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

            // Never index the app's own state directory or the folder it runs from - doing so put
            // search-index.db / app.log into the index AND, because C:\ is a watched root, made
            // every write to them fire a watcher change that triggered another write: a feedback
            // loop that pinned the WAL writer and starved the scan's own flushes.
            if (IsAppOwnedPath(entry))
            {
                continue;
            }

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

            // Pass null for the hash on purpose - reading every file end to end during the walk
            // turned a minutes-long pass into a multi-day one and kept the filename index (what
            // search needs) perpetually stale. Md5Hash is filled in afterwards by
            // HashBackfillLoopAsync, and only for files whose size collides with another file (the
            // only ones duplicate detection could ever care about). The upsert keeps any hash a
            // previous pass computed as long as the size is unchanged (see CreateUpsertCommand).
            batch.Upsert(entry, Path.GetFileName(entry), directory, stat.IsDirectory, stat.Size, stat.Modified, null, rootPath, generation);
            NotifyScanProgress();

            if (stat.IsDirectory)
            {
                ScanDirectory(entry, rootPath, generation, batch, cancellationToken);
            }
        }
    }

    private static string? TryComputeMd5(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(MD5.HashData(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
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
        // Drop changes to the app's own files (search-index.db, app.log, config json, ...). Because
        // C:\ is typically a watched root, indexing writes to that directory would otherwise fire
        // watcher events that trigger more indexing writes - a self-sustaining loop that floods the
        // log with "database is locked" and holds the WAL writer against the scan itself.
        if (IsAppOwnedPath(change.Path) && (change.OldPath is null || IsAppOwnedPath(change.OldPath)))
        {
            return;
        }

        PendingChanges.Enqueue(change);

        lock (WatcherLock)
        {
            _flushTimer ??= new Timer(_ => FlushPendingChanges(), null, WatcherFlushDelayMs, Timeout.Infinite);
            _flushTimer.Change(WatcherFlushDelayMs, Timeout.Infinite);
        }
    }

    private static readonly string AppStateDir = TrailingSlash(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileExplorerApp"));
    private static readonly string AppExeDir = TrailingSlash(AppContext.BaseDirectory);

    private static string TrailingSlash(string path) => path.TrimEnd('\\', '/') + "\\";

    /// True for anything inside the app's state directory (%LOCALAPPDATA%\FileExplorerApp) or the
    /// directory the executable runs from (which holds app.log / crash.log). Neither belongs in the
    /// index, and neither should ever drive a watcher-triggered update.
    private static bool IsAppOwnedPath(string path)
    {
        var normalized = TrailingSlash(path);
        return normalized.StartsWith(AppStateDir, StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(AppExeDir, StringComparison.OrdinalIgnoreCase);
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
        // Null hash - same rationale as the walk. The upsert keeps a still-valid stored hash and the
        // backfill loop computes any that are missing.
        UpsertEntry(upsertCmd, path, Path.GetFileName(path), directory, stat.IsDirectory, stat.Size, stat.Modified, null, rootPath, -1);
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
        // don't hit this path in WAL mode - only writer-vs-writer contention does). Raised from 10s
        // to 30s: a full rescan now streams many small buffered-batch commits back to back, so the
        // debounced watcher flush needs to be willing to wait longer for a gap rather than give up
        // and drop updates (which then wait for the next full rescan to be reconciled).
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=30000;";
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
                ScanGeneration INTEGER NOT NULL,
                Md5Hash TEXT
            );
            CREATE INDEX IF NOT EXISTS IX_Entries_Name ON Entries(Name);
            CREATE INDEX IF NOT EXISTS IX_Entries_RootPath ON Entries(RootPath);
            CREATE TABLE IF NOT EXISTS Meta (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();

        // Migration for databases created before the Md5Hash column existed. SQLite has no
        // "ADD COLUMN IF NOT EXISTS", so this just runs and swallows the "duplicate column name"
        // error on an already-migrated database. MUST run before the IX_Entries_SizeHash index below,
        // which references Md5Hash and would otherwise throw "no such column" on a pre-migration DB.
        try
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE Entries ADD COLUMN Md5Hash TEXT";
            alter.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // Column already present - expected on every launch after the first migrated one.
        }

        using var indexCmd = connection.CreateCommand();
        indexCmd.CommandText = "CREATE INDEX IF NOT EXISTS IX_Entries_SizeHash ON Entries(SizeBytes, Md5Hash);";
        indexCmd.ExecuteNonQuery();

        EnsureFtsSchema(connection);
    }

    /// Creates the external-content FTS5 table over Entries.Name with the trigram tokenizer (turns a
    /// substring search from a full O(rows) `LIKE '%x%'` scan into an index probe) plus the three
    /// triggers that keep it in sync with every INSERT/UPDATE/DELETE on Entries. DDL only - fast, and
    /// safe to run every launch. The one-time population of existing rows is EnsureFtsPopulatedAsync.
    /// Wrapped so a SQLite build without FTS5/trigram can't take down app launch: on failure the
    /// table just won't exist, _ftsReady stays false, and SearchAsync keeps using the LIKE scan.
    private static void EnsureFtsSchema(SqliteConnection connection)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            // Triggers are dropped and recreated every launch (cheap, DDL only) so a change to their
            // bodies actually takes effect - CREATE TRIGGER IF NOT EXISTS would silently keep an old
            // definition. The UPDATE trigger only touches the FTS index when the name actually
            // changed, so a rescan re-upserting millions of unchanged rows costs nothing here.
            cmd.CommandText = """
                CREATE VIRTUAL TABLE IF NOT EXISTS EntriesFts USING fts5(
                    Name,
                    content='Entries',
                    content_rowid='rowid',
                    tokenize='trigram'
                );
                DROP TRIGGER IF EXISTS Entries_fts_ai;
                DROP TRIGGER IF EXISTS Entries_fts_ad;
                DROP TRIGGER IF EXISTS Entries_fts_au;
                CREATE TRIGGER Entries_fts_ai AFTER INSERT ON Entries BEGIN
                    INSERT INTO EntriesFts(rowid, Name) VALUES (new.rowid, new.Name);
                END;
                CREATE TRIGGER Entries_fts_ad AFTER DELETE ON Entries BEGIN
                    INSERT INTO EntriesFts(EntriesFts, rowid, Name) VALUES ('delete', old.rowid, old.Name);
                END;
                CREATE TRIGGER Entries_fts_au AFTER UPDATE ON Entries WHEN old.Name IS NOT new.Name BEGIN
                    INSERT INTO EntriesFts(EntriesFts, rowid, Name) VALUES ('delete', old.rowid, old.Name);
                    INSERT INTO EntriesFts(rowid, Name) VALUES (new.rowid, new.Name);
                END;
                """;
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            LoggingService.LogWarning("SearchIndexService.EnsureFtsSchema", ex);
        }
    }

    /// One-time backfill of the trigram index from whatever is already in Entries. Runs on a
    /// background thread before the periodic rescan loop starts (so its single large write isn't
    /// contending with a filesystem walk), sets the persistent FtsBuilt flag, and flips _ftsReady so
    /// SearchAsync switches from the LIKE scan to MATCH. If it fails (locked, interrupted) the flag
    /// stays unset and it retries on the next launch.
    private static async Task EnsureFtsPopulatedAsync()
    {
        if (_ftsReady || ReadMeta("FtsBuilt") == "1")
        {
            _ftsReady = true;
            return;
        }

        await Task.Yield();

        try
        {
            var start = DateTime.UtcNow;
            LoggingService.LogInfo("SearchIndexService.EnsureFtsPopulatedAsync", "Building trigram FTS index (one-time backfill)...");

            using var connection = OpenConnection();
            using (var rebuild = connection.CreateCommand())
            {
                rebuild.CommandText = "INSERT INTO EntriesFts(EntriesFts) VALUES ('rebuild')";
                rebuild.CommandTimeout = 0;
                rebuild.ExecuteNonQuery();
            }

            WriteMeta(connection, "FtsBuilt", "1");
            _ftsReady = true;
            LoggingService.LogInfo("SearchIndexService.EnsureFtsPopulatedAsync", $"Trigram FTS index built in {(DateTime.UtcNow - start).TotalSeconds:F0}s");
            StatusChanged?.Invoke(null, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
        {
            LoggingService.LogWarning("SearchIndexService.EnsureFtsPopulatedAsync", ex);
        }
    }

    private static SqliteCommand CreateUpsertCommand(SqliteConnection connection, SqliteTransaction transaction)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO Entries (Path, Name, DirectoryPath, IsDirectory, SizeBytes, ModifiedTicks, RootPath, ScanGeneration, Md5Hash)
            VALUES (@path, @name, @dir, @isDir, @size, @modified, @root, @gen, @md5)
            ON CONFLICT(Path) DO UPDATE SET
                Name = excluded.Name, DirectoryPath = excluded.DirectoryPath, IsDirectory = excluded.IsDirectory,
                SizeBytes = excluded.SizeBytes, ModifiedTicks = excluded.ModifiedTicks, RootPath = excluded.RootPath,
                ScanGeneration = excluded.ScanGeneration,
                -- A caller-supplied hash (the backfiller) always wins. Otherwise keep the stored hash
                -- only while the size is unchanged; a resized file's old hash is stale, so clear it
                -- and let the backfiller recompute if the new size still collides with something.
                Md5Hash = CASE
                    WHEN excluded.Md5Hash IS NOT NULL THEN excluded.Md5Hash
                    WHEN Entries.SizeBytes = excluded.SizeBytes THEN Entries.Md5Hash
                    ELSE NULL
                END;
            """;
        cmd.Parameters.Add("@path", SqliteType.Text);
        cmd.Parameters.Add("@name", SqliteType.Text);
        cmd.Parameters.Add("@dir", SqliteType.Text);
        cmd.Parameters.Add("@isDir", SqliteType.Integer);
        cmd.Parameters.Add("@size", SqliteType.Integer);
        cmd.Parameters.Add("@modified", SqliteType.Integer);
        cmd.Parameters.Add("@root", SqliteType.Text);
        cmd.Parameters.Add("@gen", SqliteType.Integer);
        cmd.Parameters.Add("@md5", SqliteType.Text);
        return cmd;
    }

    private static void UpsertEntry(SqliteCommand cmd, string path, string name, string directory, bool isDirectory, long size, DateTime modifiedUtc, string? md5Hash, string root, long generation)
    {
        cmd.Parameters["@path"].Value = path;
        cmd.Parameters["@name"].Value = name;
        cmd.Parameters["@dir"].Value = directory;
        cmd.Parameters["@isDir"].Value = isDirectory ? 1 : 0;
        cmd.Parameters["@size"].Value = size;
        cmd.Parameters["@modified"].Value = modifiedUtc.Ticks;
        cmd.Parameters["@root"].Value = root;
        cmd.Parameters["@gen"].Value = generation;
        cmd.Parameters["@md5"].Value = (object?)md5Hash ?? DBNull.Value;
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

    /// True when <paramref name="path"/> is one of the configured index roots or sits underneath one,
    /// i.e. the index already holds (or is in the process of building) rows for everything in it.
    /// Duplicate detection uses this to decide whether it can lean on the index's stored sizes/hashes
    /// instead of walking and re-hashing the tree from disk.
    public static bool IsPathIndexed(string path)
    {
        var normalized = path.TrimEnd('\\');
        foreach (var root in RootsStore.Load())
        {
            var normalizedRoot = root.TrimEnd('\\');
            if (normalized.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith(normalizedRoot + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// Every indexed file at or below <paramref name="path"/>, as (path, size, MD5) rows - the raw
    /// material for index-backed duplicate detection. Directories are excluded. A null Md5Hash means
    /// the indexer couldn't hash that file (skipped, too slow, unreadable at the time); the caller
    /// hashes those from disk.
    public static List<IndexedFile> GetIndexedFilesUnder(string path)
    {
        var result = new List<IndexedFile>();

        try
        {
            using var connection = OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Path, SizeBytes, Md5Hash FROM Entries WHERE IsDirectory = 0 AND (Path = @p OR Path LIKE @prefix ESCAPE '\\')";
            cmd.Parameters.AddWithValue("@p", path.TrimEnd('\\'));
            cmd.Parameters.AddWithValue("@prefix", EscapeLike(path.TrimEnd('\\')) + "\\%");

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new IndexedFile(
                    reader.GetString(0),
                    reader.GetInt64(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }
        catch (SqliteException ex)
        {
            LoggingService.LogWarning("SearchIndexService.GetIndexedFilesUnder", ex);
        }

        return result;
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
