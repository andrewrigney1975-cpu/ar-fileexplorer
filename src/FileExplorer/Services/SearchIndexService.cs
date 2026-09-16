using System.Collections.Concurrent;
using System.Security.Cryptography;
using FileExplorer.Helpers;
using Microsoft.Data.Sqlite;

namespace FileExplorer.Services;

public sealed record SearchIndexEntry(string Path, string Name, string DirectoryPath, bool IsDirectory, long SizeBytes, DateTimeOffset Modified, double? Rating = null);

/// A file row from the index reduced to what duplicate detection needs: its path, size in bytes and
/// (when the backfill has got to it) MD5 hash. A Md5Hash that isn't 32 hex chars - null (not hashed
/// yet) or "" (hashing was attempted and the file was unreadable) - means "hash it from disk".
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
/// recursive FileSystemWatcher per root, backstopped by a periodic full rescan (every
/// RescanIntervalHours) for whatever a watcher missed (buffer overflow on a very busy root, or the
/// app not running when a change happened).
///
/// Concurrency model: there is exactly ONE writer. A single dedicated background thread (WriterLoop)
/// owns the only write connection and drains a queue of WriteJobs; the filesystem walk, the watcher
/// flush and the hash backfill never touch SQLite directly, they post jobs. File hashing (which can
/// take minutes for a large file) happens on its own thread and only the finished hashes are posted.
/// Reads - SearchAsync, the Control Centre status queries - open their own short-lived connections
/// and run concurrently with the writer (WAL). This replaced an arrangement where three threads
/// opened write connections independently and permanently starved each other of the single WAL
/// writer slot.
public static class SearchIndexService
{
    private const int RescanIntervalHours = 24;
    private const int WatcherFlushIntervalMs = 5000;
    private const int SqlCandidateLimit = 2000;

    // Rows accumulated in the walk before a batch is posted to the writer. Bigger batches mean fewer,
    // larger transactions - each transaction commit has fixed overhead (WAL frame flush, index
    // maintenance setup) that a bigger batch amortizes across more rows. 50000 rows/batch x
    // MaxQueuedWriteJobs=40 batches is ~2M rows in flight at the memory-bound worst case (backpressure
    // fully engaged) - at the ~500 bytes/row EntryRow was measured at, that's the ~1GB ceiling this was
    // sized against, not a number pulled from nowhere.
    private const int ScanBatchSize = 50_000;

    // If the walk gets this far ahead of the writer, it pauses - bounds the memory a burst of queued
    // batches can hold. Left as-is: raising ScanBatchSize already grew the in-flight-rows ceiling by
    // 25x without touching this.
    private const int MaxQueuedWriteJobs = 40;

    // How often the writer checkpoints the WAL back into the main db file while jobs are still
    // flowing, so a long scan can't let the WAL grow unboundedly - see WriterLoop. Passive, not
    // TRUNCATE: never blocks on readers/writers, just flushes whatever it safely can.
    private static readonly TimeSpan WriterCheckpointInterval = TimeSpan.FromMinutes(2);

    // Watcher changes coalesced per flush, and a hard cap on the pending queue (a whole system drive
    // as a root can still produce a burst faster than we apply it - past the cap we drop and let the
    // next full rescan reconcile).
    private const int MaxChangesPerFlush = 3000;
    private const int MaxPendingChanges = 20000;

    // Hash backfill paging, write batching, and how many files to hash at once. The read+open of a
    // file on a network/removable root is round-trip-latency bound, so several in flight at once is
    // several times the throughput; it also means one huge file ties up only one of the slots
    // instead of the whole sweep. Hashing is I/O bound - this is not CPU parallelism.
    private const int HashBackfillPageSize = 4000;
    private const int HashBackfillWriteBatch = 200;
    private const int HashBackfillParallelism = 8;

    // Directory/File APIs are plain blocking Win32 calls with no cancellation support - a genuinely
    // unresponsive drive (spun down, a failing USB/SATA bridge, a bad sector causing driver-level
    // retries) can block the calling thread forever. These bound the filesystem walk - see
    // TryRunWithTimeout.
    private const int DirectoryEnumerationTimeoutSeconds = 60;
    private const int PerEntryStatTimeoutSeconds = 15;

    // Written into Md5Hash for a file that was picked for hashing but couldn't be read (locked,
    // permission). Distinguishes "tried, unavailable" from "not tried yet" (NULL) so the backfill
    // doesn't pick the same unreadable file every sweep. Not a valid 32-char hex digest, so
    // duplicate detection treats it exactly like a missing hash.
    private const string HashUnavailable = "";

    private static readonly JsonFileStore<List<string>> RootsStore = new("search-index-roots.json", () => new List<string>());

    private static readonly object WatcherLock = new();
    private static readonly Dictionary<string, FileSystemWatcher> Watchers = new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentQueue<PendingChange> PendingChanges = new();
    private static readonly BlockingCollection<WriteJob> WriteQueue = new();

    private static Timer? _flushTimer;
    private static int _resolveInProgress;
    private static CancellationTokenSource? _scanCts;
    private static bool _started;
    private static int _scanProgressCount;
    private static DateTime _lastProgressNotifyUtc = DateTime.MinValue;

    // True once the trigram FTS index has been fully populated (a Meta flag persists this across
    // launches). Until then SearchAsync uses the slower LIKE scan.
    private static volatile bool _ftsReady;

    private sealed record PendingChange(string Path, string? OldPath, WatcherChangeTypes ChangeType);

    /// A resolved row ready to write - the stat has already happened, off the writer thread.
    private readonly record struct EntryRow(
        string Path, string Name, string Directory, bool IsDirectory,
        long Size, DateTime ModifiedUtc, string? Md5, string RootPath, long Generation);

    /// Raised whenever scan progress, root list, or entry count changes, so Control Centre's Search
    /// Index section can refresh its status text without polling.
    public static event EventHandler? StatusChanged;

    public static bool IsScanning { get; private set; }

    /// The directory the walk is enumerating right now, or null when not scanning - the walk is
    /// single-threaded and strictly depth-first (see ScanDirectory), so a plain field is enough;
    /// only ever read by the UI's polling/event-driven refresh, which tolerates a stale read by a
    /// few hundred ms the same way EntryCount already does.
    public static string? CurrentScanPath { get; private set; }

    /// True only while the backfill thread is actively hashing a page of files right now (between
    /// starting that page's Parallel.ForEach and finishing its write) - never true at the same time
    /// as IsScanning, since BackfillLoop pauses itself entirely whenever a scan is running. Lets the
    /// UI show "Hashing..." distinctly from "Indexing..." instead of one ambiguous "working" state.
    public static bool IsHashing { get; private set; }

    /// How many files the backfill has hashed so far in its current sweep - resets to 0 each time a
    /// full sweep (every collision-sized file) completes. Purely a UI progress number.
    public static long HashedThisSweepCount { get; private set; }

    public static int EntryCount { get; private set; }

    /// Write jobs waiting for the single writer thread to get to them - mostly of interest while
    /// IsScanning, when a deep backlog here means the walk is paused on backpressure (see ScanSink.
    /// Flush) even though CurrentScanPath/EntryCount aren't moving. BlockingCollection.Count is O(1).
    public static int PendingWriteJobs => WriteQueue.Count;

    public static DateTimeOffset? LastScanUtc { get; private set; }
    public static IReadOnlyList<string> Roots => RootsStore.Load();

    private static string DbDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileExplorerApp");
    private static string DbPath => Path.Combine(DbDirectory, "search-index.db");
    private static string HashPausePath => Path.Combine(DbDirectory, "hash-pause-until.txt");

    /// When this returns a value, the MD5 hash backfill idles until then (the walk, watcher and
    /// search are unaffected). File-backed so a pause survives an app restart - the backfill thread
    /// otherwise starts unconditionally with the process and has no other off switch.
    public static DateTime? BackfillPausedUntilUtc
    {
        get
        {
            try
            {
                if (File.Exists(HashPausePath)
                    && DateTime.TryParse(File.ReadAllText(HashPausePath).Trim(), null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var until)
                    && until.ToUniversalTime() > DateTime.UtcNow)
                {
                    return until.ToUniversalTime();
                }
            }
            catch (Exception ex)
            {
                LoggingService.LogWarning("SearchIndexService.BackfillPausedUntilUtc", ex);
            }

            return null;
        }
    }

    /// Pause the hash backfill for the given duration; TimeSpan.Zero (or less) resumes immediately.
    public static void PauseBackfill(TimeSpan duration)
    {
        try
        {
            Directory.CreateDirectory(DbDirectory);
            if (duration <= TimeSpan.Zero)
            {
                if (File.Exists(HashPausePath))
                {
                    File.Delete(HashPausePath);
                }
            }
            else
            {
                File.WriteAllText(HashPausePath, DateTime.UtcNow.Add(duration).ToString("o"));
            }
        }
        catch (Exception ex)
        {
            LoggingService.LogWarning("SearchIndexService.PauseBackfill", ex);
        }

        StatusChanged?.Invoke(null, EventArgs.Empty);
    }

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
        RefreshExcludedPathsCache();
        _ftsReady = ReadMeta("FtsBuilt") == "1";

        LastScanUtc = ReadMeta("LastScanUtc") is { } raw && long.TryParse(raw, out var ticks)
            ? new DateTimeOffset(ticks, TimeSpan.Zero)
            : null;

        var writerThread = new Thread(WriterLoop) { IsBackground = true, Name = "SearchIndexWriter" };
        writerThread.Start();

        RefreshEntryCount();

        foreach (var root in RootsStore.Load())
        {
            StartWatcher(root);
        }

        _flushTimer = new Timer(_ => ResolveAndEnqueueChanges(), null, WatcherFlushIntervalMs, WatcherFlushIntervalMs);

        if (!_ftsReady)
        {
            Enqueue(new RebuildFtsJob());
        }

        var backfillThread = new Thread(BackfillLoop) { IsBackground = true, Name = "SearchIndexBackfill", Priority = ThreadPriority.BelowNormal };
        backfillThread.Start();

        _ = PeriodicRescanLoopAsync();
    }

    // ================================================================= the single writer

    private static void Enqueue(WriteJob job) => WriteQueue.Add(job);

    private static void EnqueueAndWait(WriteJob job, CancellationToken cancellationToken)
    {
        job.Completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        WriteQueue.Add(job);
        job.Completion.Task.Wait(cancellationToken);
    }

    /// Same as EnqueueAndWait but a real async wait rather than a blocking Task.Wait - EnqueueAndWait
    /// is only ever safe to call from a background thread (every existing use is inside a Task.Run),
    /// since .Wait() on the UI thread freezes the whole app's message loop until the writer works
    /// through however much of the queue is already ahead of this job (which can be a long backlog
    /// during an active scan or hash sweep). Anything reachable from a UI-thread event handler -
    /// AddExcludedPathAsync/RemoveExcludedPathAsync - must use this instead.
    private static Task EnqueueAndWaitAsync(WriteJob job)
    {
        job.Completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        WriteQueue.Add(job);
        return job.Completion.Task;
    }

    private abstract class WriteJob
    {
        public TaskCompletionSource? Completion { get; set; }
        public abstract void Run(SqliteConnection connection);
    }

    /// The only thread that ever writes to the database. Owns one connection for its whole life,
    /// blocks (no spin) when the queue is empty, and refreshes the entry count opportunistically
    /// between jobs while no full scan is running.
    private static void WriterLoop()
    {
        SqliteConnection connection;
        try
        {
            connection = OpenConnection();
        }
        catch (Exception ex)
        {
            LoggingService.LogWarning("SearchIndexService.WriterLoop: could not open the write connection", ex);
            return;
        }

        using (connection)
        {
            var jobsSinceCount = 0;
            var lastCountUtc = DateTime.MinValue;
            var lastCheckpointUtc = DateTime.UtcNow;

            foreach (var job in WriteQueue.GetConsumingEnumerable())
            {
                try
                {
                    job.Run(connection);
                }
                catch (Exception ex)
                {
                    LoggingService.LogWarning($"SearchIndexService.WriterLoop: {job.GetType().Name} failed", ex);
                }
                finally
                {
                    job.Completion?.TrySetResult();
                }

                if (!IsScanning && ++jobsSinceCount >= 25 && (DateTime.UtcNow - lastCountUtc).TotalSeconds >= 15)
                {
                    jobsSinceCount = 0;
                    lastCountUtc = DateTime.UtcNow;
                    try
                    {
                        using var cmd = connection.CreateCommand();
                        cmd.CommandText = "SELECT COUNT(*) FROM Entries";
                        EntryCount = Convert.ToInt32(cmd.ExecuteScalar());
                        StatusChanged?.Invoke(null, EventArgs.Empty);
                    }
                    catch (SqliteException)
                    {
                    }
                }

                // Runs regardless of IsScanning - unlike the count refresh above, this is exactly
                // for the case a scan is long enough to matter: CheckpointJob (enqueued only in
                // RebuildRootsAsync's finally block) never runs until the whole rebuild finishes, so
                // without this the WAL grows unboundedly for the entire scan (confirmed: 407MB and
                // still climbing during one multi-hour single-root re-index). PASSIVE never blocks on
                // readers/writers - it just flushes whatever it safely can right now.
                if (DateTime.UtcNow - lastCheckpointUtc >= WriterCheckpointInterval)
                {
                    lastCheckpointUtc = DateTime.UtcNow;
                    try
                    {
                        using var cmd = connection.CreateCommand();
                        cmd.CommandText = "PRAGMA wal_checkpoint(PASSIVE)";
                        cmd.ExecuteNonQuery();
                    }
                    catch (SqliteException ex)
                    {
                        LoggingService.LogWarning("SearchIndexService.WriterLoop: periodic checkpoint failed", ex);
                    }
                }
            }
        }
    }

    private sealed class UpsertBatchJob(IReadOnlyList<EntryRow> rows) : WriteJob
    {
        public override void Run(SqliteConnection connection)
        {
            using var transaction = connection.BeginTransaction();
            using var cmd = CreateUpsertCommand(connection, transaction);
            foreach (var r in rows)
            {
                UpsertEntry(cmd, r.Path, r.Name, r.Directory, r.IsDirectory, r.Size, r.ModifiedUtc, r.Md5, r.RootPath, r.Generation);
            }

            transaction.Commit();
        }
    }

    private sealed class ApplyWatcherJob(IReadOnlyList<string> deletes, IReadOnlyList<EntryRow> upserts) : WriteJob
    {
        public override void Run(SqliteConnection connection)
        {
            using var transaction = connection.BeginTransaction();

            using (var deleteCmd = connection.CreateCommand())
            {
                deleteCmd.Transaction = transaction;
                // Also removes anything under a deleted/renamed-away directory - Windows fires one
                // Deleted/Renamed event for the top of a removed tree, not one per descendant.
                deleteCmd.CommandText = "DELETE FROM Entries WHERE Path = @p OR Path LIKE @prefix ESCAPE '\\'";
                deleteCmd.Parameters.Add("@p", SqliteType.Text);
                deleteCmd.Parameters.Add("@prefix", SqliteType.Text);

                foreach (var path in deletes)
                {
                    deleteCmd.Parameters["@p"].Value = path;
                    deleteCmd.Parameters["@prefix"].Value = EscapeLike(path) + "\\\\%";
                    deleteCmd.ExecuteNonQuery();
                }
            }

            using (var upsertCmd = CreateUpsertCommand(connection, transaction))
            {
                foreach (var r in upserts)
                {
                    // Generation -1 is the sentinel for watcher-driven single-row updates - a full
                    // rescan's stale-row cleanup deletes by generation, so a -1 row just gets
                    // re-upserted with a real generation on the next walk.
                    UpsertEntry(upsertCmd, r.Path, r.Name, r.Directory, r.IsDirectory, r.Size, r.ModifiedUtc, null, r.RootPath, -1);
                }
            }

            transaction.Commit();
        }
    }

    private sealed class CleanupRootJob(string root, long generation) : WriteJob
    {
        public override void Run(SqliteConnection connection)
        {
            // "< @gen", not "<> @gen": generations are monotonic (DateTimeOffset.UtcNow.Ticks), so
            // this deletes only rows from OLDER scans (and watcher rows, generation -1) and can never
            // delete rows a newer, superseding scan has just written. Bounded chunks, not one big
            // DELETE: the per-row FTS delete-trigger makes a multi-thousand-row delete a long
            // single statement.
            var removed = 0;
            while (true)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "DELETE FROM Entries WHERE rowid IN (SELECT rowid FROM Entries WHERE RootPath = @root AND ScanGeneration < @gen LIMIT 5000)";
                cmd.Parameters.AddWithValue("@root", root);
                cmd.Parameters.AddWithValue("@gen", generation);
                var chunk = cmd.ExecuteNonQuery();
                removed += chunk;
                if (chunk < 5000)
                {
                    break;
                }
            }

            LoggingService.LogInfo("SearchIndexService.CleanupRootJob", $"{root}: removed {removed} stale rows");
        }
    }

    private sealed class WriteHashesJob(IReadOnlyList<(string Path, string Hash)> hashes) : WriteJob
    {
        public override void Run(SqliteConnection connection)
        {
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
    }

    private sealed class SetMetaJob(string key, string value) : WriteJob
    {
        public override void Run(SqliteConnection connection) => WriteMeta(connection, key, value);
    }

    private sealed class RemoveRootJob(string root) : WriteJob
    {
        public override void Run(SqliteConnection connection)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM Entries WHERE RootPath = @root";
            cmd.Parameters.AddWithValue("@root", root);
            cmd.ExecuteNonQuery();
        }
    }

    private sealed class CheckpointJob : WriteJob
    {
        public override void Run(SqliteConnection connection)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            cmd.ExecuteNonQuery();
        }
    }

    /// One-time population of the trigram index from whatever is already in Entries. Runs on the
    /// writer thread as a single job (~a minute for a few million rows); everything else just queues
    /// behind it that once. The FtsBuilt flag makes it a no-op on every later launch.
    private sealed class RebuildFtsJob : WriteJob
    {
        public override void Run(SqliteConnection connection)
        {
            var start = DateTime.UtcNow;
            LoggingService.LogInfo("SearchIndexService.RebuildFtsJob", "Building trigram FTS index (one-time)...");

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO EntriesFts(EntriesFts) VALUES ('rebuild')";
                cmd.CommandTimeout = 0;
                cmd.ExecuteNonQuery();
            }

            WriteMeta(connection, "FtsBuilt", "1");
            _ftsReady = true;
            LoggingService.LogInfo("SearchIndexService.RebuildFtsJob", $"Done in {(DateTime.UtcNow - start).TotalSeconds:F0}s");
            StatusChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    // ================================================================= roots

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
        Enqueue(new RemoveRootJob(path));
        Enqueue(new CheckpointJob());

        RefreshEntryCount();
        StatusChanged?.Invoke(null, EventArgs.Empty);
    }

    // ================================================================= full rescan

    /// Full rescan of every configured root, replacing anything that's changed and dropping rows for
    /// anything no longer on disk. Supersedes (cancels) any rescan already in flight. Wipes the
    /// entire index first (see wipeAllFirst on RebuildRootsAsync) - a full rebuild is the guarantee
    /// that a folder excluded after it was indexed (Control Centre > "Exclude from indexing and
    /// hashing") can never survive as a stale row, even though AddExcludedPath already deletes its
    /// rows immediately too.
    public static Task RebuildAsync(CancellationToken cancellationToken) => RebuildRootsAsync(RootsStore.Load(), wipeAllFirst: true, cancellationToken);

    /// Rescans just one configured root, leaving every other root's index untouched - deliberately
    /// does NOT wipe the whole database first (that would nuke every other root's data for a
    /// single-folder re-index). Still gets a from-scratch walk for its own rows: RebuildRootsAsync
    /// deletes this root's existing rows (a single indexed-by-RootPath delete) right before scanning
    /// it, so every row the walk finds is a plain INSERT rather than an UPDATE via ON CONFLICT. A
    /// re-index of an already-indexed root was measured taking 20x longer than the initial scan of
    /// the same root (81 minutes vs under 4) purely from SQLite's ON CONFLICT DO UPDATE path being
    /// far more expensive than a fresh INSERT at this row count - and the walker fully stalls (no
    /// progress-bar movement at all) while the writer works through that backlog, since the walker
    /// blocks on write-queue backpressure and nothing else advances CurrentScanPath/EntryCount while
    /// it's blocked. Same MD5-hash-loss tradeoff RebuildAsync's full wipe already has: any hash this
    /// root's files had is gone, and the backfill re-hashes collision-sized files under it from disk.
    public static Task RebuildRootAsync(string root, CancellationToken cancellationToken) => RebuildRootsAsync(new List<string> { root }, wipeAllFirst: false, cancellationToken);

    /// Rescans one folder - any folder, not necessarily a configured root - and everything nested
    /// under it, leaving the rest of the index untouched. "Index From Here..." on the folder context
    /// menu. Same delete-then-insert approach RebuildRootAsync uses for a whole root: deletes every
    /// row at or under this folder first (DeleteFolderEntriesJob, one indexed-by-Path delete rather
    /// than per-row), so the walk that follows is all fresh INSERTs. New rows are tagged with
    /// whichever configured root actually contains this folder (longest matching prefix in the roots
    /// list) so they stay grouped with that root exactly like a root-level scan's rows do
    /// (GetRootEntryCounts, the exclusion cache, CleanupRootJob's generation tracking on a later
    /// root-level rescan) - or, if the folder isn't under any configured root at all, tagged with the
    /// folder's own path. That second case is a one-off: nothing watches or periodically rescans it,
    /// and it won't show up in Control Centre's per-root list, and the next full "Rebuild now" wipes
    /// it along with everything else and never recreates it, since it was never added to RootsStore.
    /// Supersedes (cancels) any rescan already in flight, same as RebuildAsync/RebuildRootAsync -
    /// there is only ever one scan running at a time, sharing the single writer connection.
    public static Task RebuildFolderAsync(string folder, CancellationToken cancellationToken)
    {
        var normalizedFolder = folder.TrimEnd('\\', '/');

        var owningRoot = RootsStore.Load().FirstOrDefault(r =>
        {
            var normalizedRoot = r.TrimEnd('\\', '/');
            return string.Equals(normalizedRoot, normalizedFolder, StringComparison.OrdinalIgnoreCase) ||
                   normalizedFolder.StartsWith(normalizedRoot + "\\", StringComparison.OrdinalIgnoreCase);
        }) ?? normalizedFolder;

        return RebuildFolderCoreAsync(normalizedFolder, owningRoot, cancellationToken);
    }

    private const string FolderTraceSource = "SearchIndexService.RebuildFolderAsync";

    private static async Task RebuildFolderCoreAsync(string folder, string rootTag, CancellationToken cancellationToken)
    {
        LoggingService.LogInfo(FolderTraceSource, $"Starting: folder='{folder}', rootTag='{rootTag}'");

        _scanCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _scanCts = cts;

        IsScanning = true;
        _scanProgressCount = 0;
        EntryCount = 0;
        CurrentScanPath = null;
        _lastProgressNotifyUtc = DateTime.MinValue;
        StatusChanged?.Invoke(null, EventArgs.Empty);

        try
        {
            await Task.Run(() =>
            {
                if (!Directory.Exists(folder))
                {
                    LoggingService.LogWarning(FolderTraceSource, new DirectoryNotFoundException(folder));
                    return;
                }

                if (!TryRunWithTimeout(() => Directory.GetLastWriteTimeUtc(folder), TimeSpan.FromSeconds(PerEntryStatTimeoutSeconds), out var folderModified))
                {
                    LoggingService.LogWarning($"SearchIndexService.RebuildFolderAsync: {folder} took longer than {PerEntryStatTimeoutSeconds}s to stat - aborting", new TimeoutException());
                    return;
                }

                EnqueueAndWait(new DeleteFolderEntriesJob(folder), cts.Token);

                var generation = DateTimeOffset.UtcNow.Ticks;
                var sink = new ScanSink();
                sink.Add(new EntryRow(folder, Path.GetFileName(folder), Path.GetDirectoryName(folder) ?? folder, true, 0, folderModified, null, rootTag, generation));
                ScanDirectory(folder, rootTag, generation, sink, cts.Token);
                sink.Flush();

                LoggingService.LogInfo(FolderTraceSource, $"Done: {_scanProgressCount} entries so far");
            }, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            LoggingService.LogInfo(FolderTraceSource, "Cancelled (superseded by a newer rebuild request)");
        }
        catch (Exception ex)
        {
            LoggingService.LogWarning(FolderTraceSource, ex);
        }
        finally
        {
            IsScanning = false;
            CurrentScanPath = null;

            try
            {
                RefreshEntryCount();
            }
            catch (Exception ex)
            {
                LoggingService.LogWarning("SearchIndexService.RebuildFolderAsync: RefreshEntryCount in finally", ex);
            }

            Enqueue(new CheckpointJob());
            StatusChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    private const string TraceSource = "SearchIndexService.RebuildRootsAsync";

    private static async Task RebuildRootsAsync(List<string> roots, bool wipeAllFirst, CancellationToken cancellationToken)
    {
        if (roots.Count == 0)
        {
            return;
        }

        LoggingService.LogInfo(TraceSource, $"Starting: roots=[{string.Join(", ", roots)}], wipeAllFirst={wipeAllFirst}");

        _scanCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _scanCts = cts;

        IsScanning = true;
        _scanProgressCount = 0;
        EntryCount = 0;
        CurrentScanPath = null;
        _lastProgressNotifyUtc = DateTime.MinValue;
        StatusChanged?.Invoke(null, EventArgs.Empty);

        try
        {
            await Task.Run(() =>
            {
                if (wipeAllFirst)
                {
                    EnqueueAndWait(new ClearAllEntriesJob(), cts.Token);
                }

                var generation = DateTimeOffset.UtcNow.Ticks;

                foreach (var root in roots)
                {
                    cts.Token.ThrowIfCancellationRequested();

                    // Per-root guard: one unreadable/disconnected drive must not abort the whole
                    // multi-root scan or skip every other root's cleanup and the LastScanUtc write.
                    try
                    {
                        LoggingService.LogInfo(TraceSource, $"Root '{root}': starting");
                        if (!Directory.Exists(root))
                        {
                            continue;
                        }

                        if (!TryRunWithTimeout(() => Directory.GetLastWriteTimeUtc(root), TimeSpan.FromSeconds(PerEntryStatTimeoutSeconds), out var rootModified))
                        {
                            LoggingService.LogWarning($"SearchIndexService.RebuildRootsAsync: {root} took longer than {PerEntryStatTimeoutSeconds}s to stat - skipping it this pass", new TimeoutException());
                            continue;
                        }

                        // wipeAllFirst already emptied the whole table, so this root has nothing to
                        // delete yet - skip the redundant round trip. For a standalone per-root
                        // re-index this is what turns every row the walk finds back into a plain
                        // INSERT instead of an UPDATE via ON CONFLICT (see RebuildRootAsync).
                        if (!wipeAllFirst)
                        {
                            EnqueueAndWait(new DeleteRootEntriesJob(root), cts.Token);
                        }

                        var sink = new ScanSink();
                        sink.Add(new EntryRow(root, root, Path.GetDirectoryName(root) ?? root, true, 0, rootModified, null, root, generation));
                        ScanDirectory(root, root, generation, sink, cts.Token);
                        sink.Flush();

                        EnqueueAndWait(new CleanupRootJob(root, generation), cts.Token);
                        LoggingService.LogInfo(TraceSource, $"Root '{root}': done, {_scanProgressCount} entries so far");
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        LoggingService.LogWarning($"SearchIndexService.RebuildRootsAsync: root '{root}' failed - continuing with the rest", ex);
                    }
                }

                EnqueueAndWait(new SetMetaJob("LastScanUtc", DateTimeOffset.UtcNow.Ticks.ToString()), cts.Token);
            }, cts.Token).ConfigureAwait(false);

            LastScanUtc = DateTimeOffset.UtcNow;
        }
        catch (OperationCanceledException)
        {
            LoggingService.LogInfo(TraceSource, "Cancelled (superseded by a newer rebuild request)");
        }
        catch (Exception ex)
        {
            LoggingService.LogWarning("SearchIndexService.RebuildRootsAsync", ex);
        }
        finally
        {
            IsScanning = false;
            CurrentScanPath = null;

            try
            {
                RefreshEntryCount();
            }
            catch (Exception ex)
            {
                LoggingService.LogWarning("SearchIndexService.RebuildRootsAsync: RefreshEntryCount in finally", ex);
            }

            Enqueue(new CheckpointJob());
            StatusChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    // ----- filesystem walk -----

    /// Accumulates rows from the walk and posts a batch to the writer every ScanBatchSize. Applies
    /// back-pressure if the writer falls far behind, so a fast local walk can't pile the whole tree
    /// into memory as queued jobs.
    private sealed class ScanSink
    {
        private readonly List<EntryRow> _buffer = new(ScanBatchSize);

        public void Add(EntryRow row)
        {
            _buffer.Add(row);
            NotifyScanProgress();

            if (_buffer.Count >= ScanBatchSize)
            {
                Flush();
            }
        }

        public void Flush()
        {
            if (_buffer.Count == 0)
            {
                return;
            }

            var rows = _buffer.ToArray();
            _buffer.Clear();

            // The walker (and therefore EntryCount/CurrentScanPath - both only ever touched from this
            // thread) is fully paused for as long as this loop spins. Without a heartbeat here, a
            // writer that falls behind (a slow drive, a burst of ON CONFLICT updates) makes the UI
            // look completely frozen for however long the backlog takes to drain, even though the
            // writer is actively working through it the whole time.
            var lastHeartbeatUtc = DateTime.MinValue;
            while (WriteQueue.Count > MaxQueuedWriteJobs)
            {
                var now = DateTime.UtcNow;
                if ((now - lastHeartbeatUtc).TotalMilliseconds >= 300)
                {
                    lastHeartbeatUtc = now;
                    StatusChanged?.Invoke(null, EventArgs.Empty);
                }

                Thread.Sleep(25);
            }

            Enqueue(new UpsertBatchJob(rows));
        }
    }

    private static void ScanDirectory(string directory, string rootPath, long generation, ScanSink sink, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CurrentScanPath = directory;

        List<string> entries;
        try
        {
            if (!TryRunWithTimeout(() => Directory.EnumerateFileSystemEntries(directory).ToList(), TimeSpan.FromSeconds(DirectoryEnumerationTimeoutSeconds), out var result))
            {
                LoggingService.LogWarning($"SearchIndexService.ScanDirectory: {directory} took longer than {DirectoryEnumerationTimeoutSeconds}s to enumerate - skipping", new TimeoutException());
                return;
            }

            entries = result!;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return;
        }

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsExcludedFromIndex(entry))
            {
                continue;
            }

            (FileAttributes Attrs, bool IsDirectory, long Size, DateTime Modified) stat;
            try
            {
                if (!TryRunWithTimeout(() => StatEntry(entry), TimeSpan.FromSeconds(PerEntryStatTimeoutSeconds), out var result))
                {
                    LoggingService.LogWarning($"SearchIndexService.ScanDirectory: {entry} took longer than {PerEntryStatTimeoutSeconds}s to stat - skipping", new TimeoutException());
                    continue;
                }

                stat = result;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            // System / hidden files are OS bookkeeping, not something a file search wants.
            if (stat.Attrs.HasFlag(FileAttributes.Hidden) || stat.Attrs.HasFlag(FileAttributes.System))
            {
                continue;
            }

            // Null hash: the walk never reads file contents. The backfill fills Md5Hash in afterwards
            // and only for files whose size collides with another file. The upsert keeps any hash a
            // previous pass computed while the size is unchanged (see CreateUpsertCommand).
            sink.Add(new EntryRow(entry, Path.GetFileName(entry), directory, stat.IsDirectory, stat.Size, stat.Modified, null, rootPath, generation));

            // The junction/symlink itself still gets indexed above (so it's findable by name,
            // consistent with copy/move/sync never excluding a link either) - but never descend into
            // one. Same reasoning copy/move/sync already use: descending would either duplicate
            // whatever the link points to (if it targets a location under a DIFFERENT configured
            // root - e.g. this app's own S:\ root containing junctions into T:/U:/V:/W:/X:, each also
            // separately configured, so every file under a junction was walked and written twice) or
            // recurse forever (a self-referential or circular junction - no cycle guard existed here).
            if (stat.IsDirectory && !stat.Attrs.HasFlag(FileAttributes.ReparsePoint))
            {
                ScanDirectory(entry, rootPath, generation, sink, cancellationToken);
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

    /// Runs a synchronous filesystem call with a watchdog timeout, for the walk (Directory/File
    /// APIs have no cancellation). On timeout the underlying thread-pool thread is abandoned (a
    /// bounded leak, only for genuinely stuck operations) and the walk moves on. WaitAny, not
    /// Wait/Result: those throw AggregateException the instant the task *faults* - including a fault
    /// well within the timeout - which would bypass the IOException/UnauthorizedAccessException
    /// catch blocks at the call sites and crash the app (a real, confirmed bug).
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

    private static async Task PeriodicRescanLoopAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(20)).ConfigureAwait(false);

        while (true)
        {
            if (RootsStore.Load().Count > 0 &&
                (LastScanUtc is null || DateTimeOffset.UtcNow - LastScanUtc > TimeSpan.FromHours(RescanIntervalHours)))
            {
                LoggingService.LogInfo("SearchIndexService.PeriodicRescanLoopAsync", $"Index stale (LastScanUtc={LastScanUtc:o}) - full rescan");
                await RebuildAsync(CancellationToken.None).ConfigureAwait(false);
            }

            await Task.Delay(TimeSpan.FromHours(1)).ConfigureAwait(false);
        }
    }

    // ================================================================= hash backfill

    /// Fills in Md5Hash, after the walk, for files that have none AND share their exact byte size
    /// with another indexed file - the only files whose hash duplicate detection can ever need (a
    /// size-unique file can't have a duplicate). Runs on its own orchestrator thread and hashes each
    /// page's files with a small Parallel.ForEach (file open/read on a network root is round-trip
    /// bound, so N at once is N times the throughput); only the finished hash batches are posted to
    /// the single DB writer. No file-size limit. Pauses while a full scan is running.
    private static void BackfillLoop()
    {
        Thread.Sleep(TimeSpan.FromSeconds(15));

        // Keyset cursor over (SizeBytes, rowid): smallest files first, so the millions of small
        // collision candidates are done quickly and the handful of huge files (still hashed in full)
        // fall at the end of the sweep instead of stalling early visible progress.
        long cursorSize = 0;
        long cursorRowid = 0;
        HashSet<long>? collidingSizes = null;
        var writtenThisSweep = 0L;
        var loggedPause = false;

        while (true)
        {
            try
            {
                IsHashing = false;

                if (RootsStore.Load().Count == 0)
                {
                    Thread.Sleep(TimeSpan.FromMinutes(5));
                    continue;
                }

                if (IsScanning)
                {
                    Thread.Sleep(TimeSpan.FromSeconds(10));
                    continue;
                }

                if (BackfillPausedUntilUtc is { } pausedUntil)
                {
                    if (!loggedPause)
                    {
                        LoggingService.LogInfo("SearchIndexService.BackfillLoop", $"Hash backfill paused until {pausedUntil:o} (UTC)");
                        loggedPause = true;
                    }

                    var wait = pausedUntil - DateTime.UtcNow;
                    Thread.Sleep(wait < TimeSpan.FromSeconds(30) ? wait : TimeSpan.FromSeconds(30));
                    continue;
                }

                loggedPause = false;

                collidingSizes ??= LoadCollidingSizes();
                if (collidingSizes.Count == 0)
                {
                    collidingSizes = null;
                    Thread.Sleep(TimeSpan.FromMinutes(30));
                    continue;
                }

                List<(long Rowid, string Path, long Size)> page;
                using (var connection = OpenConnection())
                {
                    page = ReadUnhashedPage(connection, cursorSize, cursorRowid, HashBackfillPageSize);
                }

                if (page.Count == 0)
                {
                    if (writtenThisSweep > 0)
                    {
                        LoggingService.LogInfo("SearchIndexService.BackfillLoop", $"Sweep complete: {writtenThisSweep} hashes");
                        StatusChanged?.Invoke(null, EventArgs.Empty);
                    }

                    Enqueue(new CheckpointJob());
                    cursorSize = 0;
                    cursorRowid = 0;
                    collidingSizes = null;
                    writtenThisSweep = 0;
                    HashedThisSweepCount = 0;
                    Thread.Sleep(TimeSpan.FromMinutes(30));
                    continue;
                }

                cursorSize = page[^1].Size;
                cursorRowid = page[^1].Rowid;

                var toHash = page
                    .Where(p => collidingSizes.Contains(p.Size) && !IsExcludedFromIndex(p.Path))
                    .Select(p => p.Path)
                    .ToList();

                if (toHash.Count == 0)
                {
                    continue;
                }

                IsHashing = true;
                StatusChanged?.Invoke(null, EventArgs.Empty);
                var hashed = new ConcurrentBag<(string Path, string Hash)>();
                Parallel.ForEach(
                    toHash,
                    new ParallelOptions { MaxDegreeOfParallelism = HashBackfillParallelism },
                    path =>
                    {
                        if (IsScanning || BackfillPausedUntilUtc is not null)
                        {
                            return;
                        }

                        var hash = TryComputeMd5(path);
                        hashed.Add((path, hash is { Length: 32 } ? hash : HashUnavailable));
                    });

                foreach (var chunk in hashed.Chunk(HashBackfillWriteBatch))
                {
                    Enqueue(new WriteHashesJob(chunk));
                    writtenThisSweep += chunk.Length;
                }

                HashedThisSweepCount = writtenThisSweep;
                IsHashing = false;
                StatusChanged?.Invoke(null, EventArgs.Empty);
                Thread.Sleep(50);
            }
            catch (Exception ex)
            {
                LoggingService.LogWarning("SearchIndexService.BackfillLoop", ex);
                Thread.Sleep(TimeSpan.FromMinutes(5));
            }
        }
    }

    /// Synchronous, no timeout, no size cap - called from the backfill's Parallel.ForEach workers,
    /// which are separate from the single DB writer, so a slow or huge file blocks only a hash
    /// worker. A truly hung read (a dead network mount) parks that one worker until the next launch;
    /// the sweep keeps flowing on the other workers.
    private static string? TryComputeMd5(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                1 << 20, FileOptions.SequentialScan);
            return Convert.ToHexString(MD5.HashData(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// The set of byte sizes shared by 2+ files - derived once per sweep. No "IsDirectory = 0":
    /// that column isn't indexed and would force a per-row main-table lookup; directory rows always
    /// have SizeBytes = 0, so "SizeBytes > 0" excludes them and this is a covering-index scan.
    private static HashSet<long> LoadCollidingSizes()
    {
        var sizes = new HashSet<long>();

        try
        {
            using var connection = OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT SizeBytes FROM Entries WHERE SizeBytes > 0 GROUP BY SizeBytes HAVING COUNT(*) > 1";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                sizes.Add(reader.GetInt64(0));
            }
        }
        catch (SqliteException ex)
        {
            LoggingService.LogWarning("SearchIndexService.LoadCollidingSizes", ex);
        }

        return sizes;
    }

    private static List<(long Rowid, string Path, long Size)> ReadUnhashedPage(SqliteConnection connection, long afterSize, long afterRowid, int limit)
    {
        var page = new List<(long, string, long)>();

        using var cmd = connection.CreateCommand();
        // Keyset pagination over (SizeBytes, rowid) - smallest files first, each row visited once
        // per sweep. Rides IX_Entries_SizeHash (SizeBytes leading).
        cmd.CommandText = """
            SELECT rowid, Path, SizeBytes FROM Entries
            WHERE Md5Hash IS NULL AND SizeBytes > 0
              AND (SizeBytes > @sz OR (SizeBytes = @sz AND rowid > @rid))
            ORDER BY SizeBytes, rowid
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@sz", afterSize);
        cmd.Parameters.AddWithValue("@rid", afterRowid);
        cmd.Parameters.AddWithValue("@limit", limit);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            page.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2)));
        }

        return page;
    }

    // ================================================================= live watcher updates

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
        if (IsExcludedFromIndex(change.Path) && (change.OldPath is null || IsExcludedFromIndex(change.OldPath)))
        {
            return;
        }

        if (PendingChanges.Count >= MaxPendingChanges)
        {
            return;
        }

        PendingChanges.Enqueue(change);
    }

    /// Runs off a periodic timer. Drains a bounded slice of the pending queue, does the (potentially
    /// slow) stat work here - NOT on the writer thread - then posts one ApplyWatcherJob. Guarded
    /// against re-entry so a slow slice doesn't stack up parallel resolvers.
    private static void ResolveAndEnqueueChanges()
    {
        if (Interlocked.Exchange(ref _resolveInProgress, 1) == 1)
        {
            return;
        }

        try
        {
            var changes = new List<PendingChange>();
            while (changes.Count < MaxChangesPerFlush && PendingChanges.TryDequeue(out var change))
            {
                changes.Add(change);
            }

            if (changes.Count == 0)
            {
                return;
            }

            var deletes = new List<string>();
            var upserts = new List<EntryRow>();

            foreach (var change in changes)
            {
                var removedPath = change.ChangeType == WatcherChangeTypes.Deleted ? change.Path : change.OldPath;
                if (removedPath is not null)
                {
                    deletes.Add(removedPath);
                }

                if (change.ChangeType != WatcherChangeTypes.Deleted && ResolveWatcherRow(change.Path) is { } row)
                {
                    upserts.Add(row);
                }
            }

            if (deletes.Count > 0 || upserts.Count > 0)
            {
                Enqueue(new ApplyWatcherJob(deletes, upserts));
            }
        }
        catch (Exception ex)
        {
            LoggingService.LogWarning("SearchIndexService.ResolveAndEnqueueChanges", ex);
        }
        finally
        {
            Volatile.Write(ref _resolveInProgress, 0);
        }
    }

    private static EntryRow? ResolveWatcherRow(string path)
    {
        if (IsExcludedFromIndex(path))
        {
            return null;
        }

        (FileAttributes Attrs, bool IsDirectory, long Size, DateTime Modified) stat;
        try
        {
            if (!TryRunWithTimeout(() => StatEntry(path), TimeSpan.FromSeconds(PerEntryStatTimeoutSeconds), out var result))
            {
                return null;
            }

            stat = result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (stat.Attrs.HasFlag(FileAttributes.Hidden) || stat.Attrs.HasFlag(FileAttributes.System))
        {
            return null;
        }

        var rootPath = RootsStore.Load().FirstOrDefault(r => path.StartsWith(r, StringComparison.OrdinalIgnoreCase));
        if (rootPath is null)
        {
            return null;
        }

        return new EntryRow(path, Path.GetFileName(path), Path.GetDirectoryName(path) ?? path, stat.IsDirectory, stat.Size, stat.Modified, null, rootPath, -1);
    }

    // ================================================================= exclusions

    private static readonly string AppStateDir = TrailingSlash(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileExplorerApp"));
    private static readonly string AppExeDir = TrailingSlash(AppContext.BaseDirectory);

    private static string TrailingSlash(string path) => path.TrimEnd('\\', '/') + "\\";

    private static bool IsAppOwnedPath(string path)
    {
        var normalized = TrailingSlash(path);
        return normalized.StartsWith(AppStateDir, StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(AppExeDir, StringComparison.OrdinalIgnoreCase);
    }

    // OS and tooling folders: pure write churn (a watcher-event firehose that otherwise saturates
    // the writer) holding nothing worth finding in a search or deduplicating. Excluded from the
    // walk, from watcher updates, and from the hash backfill. Matched as a whole path segment.
    private static readonly HashSet<string> ExcludedPathSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "$Recycle.Bin", "$RECYCLE.BIN", "System Volume Information", "$WinREAgent",
        "$SysReset", "Recovery", "PerfLogs", "ProgramData", "Temp", "tmp",
        "Temporary Internet Files", "node_modules", "__pycache__", ".git",
    };

    private static bool IsExcludedFromIndex(string path)
    {
        if (IsAppOwnedPath(path))
        {
            return true;
        }

        if (path.Contains(@"\AppData\Local\Temp\", StringComparison.OrdinalIgnoreCase) ||
            path.Contains(@"\AppData\Local\Packages\", StringComparison.OrdinalIgnoreCase) ||
            path.Contains(@"\AppData\Local\Microsoft\Windows\INetCache\", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var segment in path.Split('\\', '/'))
        {
            if (segment.Length > 0 && ExcludedPathSegments.Contains(segment))
            {
                return true;
            }
        }

        return IsUnderUserExcludedPath(path);
    }

    // ----- user-configured excluded paths (Control Centre "Exclude from indexing and hashing") -----

    /// Read from ExcludedPaths (a SQLite table, not JsonFileStore like Roots - the user explicitly
    /// wanted these tracked in the database) into memory once at Start() and refreshed after every
    /// Add/RemoveExcludedPath, since IsExcludedFromIndex is called for every single file/folder the
    /// walk and watcher touch - a per-file SQL query there would be far too hot a path.
    private static volatile string[] _excludedPathsCache = Array.Empty<string>();

    public static IReadOnlyList<string> ExcludedPaths => _excludedPathsCache;

    private static bool IsUnderUserExcludedPath(string path)
    {
        foreach (var excluded in _excludedPathsCache)
        {
            if (path.Equals(excluded, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(excluded + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void RefreshExcludedPathsCache()
    {
        try
        {
            using var connection = OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Path FROM ExcludedPaths ORDER BY Path";
            using var reader = cmd.ExecuteReader();

            var list = new List<string>();
            while (reader.Read())
            {
                list.Add(reader.GetString(0));
            }

            _excludedPathsCache = list.ToArray();
        }
        catch (SqliteException ex)
        {
            LoggingService.LogWarning("SearchIndexService.RefreshExcludedPathsCache", ex);
        }
    }

    /// Excludes a folder (and everything under it - IsUnderUserExcludedPath is a prefix match) from
    /// future indexing, watching, and hash backfill, and immediately deletes any rows already in the
    /// index under it so it disappears from search right away rather than only after a rebuild.
    /// A full "Rebuild now" wipes and re-walks from scratch anyway (see RebuildAsync), which is the
    /// belt-and-suspenders guarantee that a stale entry can never survive indefinitely. Async and
    /// safe to call from a UI-thread click handler - see EnqueueAndWaitAsync.
    public static async Task AddExcludedPathAsync(string path)
    {
        var normalized = path.TrimEnd('\\', '/');
        await EnqueueAndWaitAsync(new AddExcludedPathJob(normalized)).ConfigureAwait(true);
        RefreshExcludedPathsCache();
        RefreshEntryCount();
        StatusChanged?.Invoke(null, EventArgs.Empty);
    }

    public static async Task RemoveExcludedPathAsync(string path)
    {
        var normalized = path.TrimEnd('\\', '/');
        await EnqueueAndWaitAsync(new RemoveExcludedPathJob(normalized)).ConfigureAwait(true);
        RefreshExcludedPathsCache();
        StatusChanged?.Invoke(null, EventArgs.Empty);
    }

    private sealed class AddExcludedPathJob(string path) : WriteJob
    {
        public override void Run(SqliteConnection connection)
        {
            using var transaction = connection.BeginTransaction();

            using (var insertCmd = connection.CreateCommand())
            {
                insertCmd.Transaction = transaction;
                insertCmd.CommandText = "INSERT OR IGNORE INTO ExcludedPaths (Path, AddedUtcTicks) VALUES (@p, @t)";
                insertCmd.Parameters.AddWithValue("@p", path);
                insertCmd.Parameters.AddWithValue("@t", DateTimeOffset.UtcNow.Ticks);
                insertCmd.ExecuteNonQuery();
            }

            // Same "delete self or anything nested under it" prefix pattern ApplyWatcherJob uses for
            // a deleted directory - an excluded folder includes all its children.
            using (var deleteCmd = connection.CreateCommand())
            {
                deleteCmd.Transaction = transaction;
                deleteCmd.CommandText = "DELETE FROM Entries WHERE Path = @p OR Path LIKE @prefix ESCAPE '\\'";
                deleteCmd.Parameters.AddWithValue("@p", path);
                deleteCmd.Parameters.AddWithValue("@prefix", EscapeLike(path) + "\\\\%");
                deleteCmd.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    private sealed class RemoveExcludedPathJob(string path) : WriteJob
    {
        public override void Run(SqliteConnection connection)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM ExcludedPaths WHERE Path = @p";
            cmd.Parameters.AddWithValue("@p", path);
            cmd.ExecuteNonQuery();
        }
    }

    private sealed class ClearAllEntriesJob : WriteJob
    {
        public override void Run(SqliteConnection connection)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM Entries";
            cmd.ExecuteNonQuery();
        }
    }

    /// Deletes every row already indexed for one root, right before RebuildRootsAsync rescans it -
    /// so the walk that follows only ever does fresh INSERTs, never an UPDATE via ON CONFLICT. Uses
    /// IX_Entries_RootPath, so this is a single indexed delete rather than a per-row operation.
    private sealed class DeleteRootEntriesJob(string root) : WriteJob
    {
        public override void Run(SqliteConnection connection)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM Entries WHERE RootPath = @root";
            cmd.Parameters.AddWithValue("@root", root);
            cmd.ExecuteNonQuery();
        }
    }

    /// Same idea as DeleteRootEntriesJob, but for an arbitrary folder rather than a whole configured
    /// root - "Index From Here...", right before RebuildFolderAsync rescans it. Same Path-prefix
    /// pattern AddExcludedPathJob and GetIndexedFilesUnder already use for "this path and everything
    /// nested under it" (no RootPath index to lean on here, since the folder isn't necessarily a
    /// root itself).
    private sealed class DeleteFolderEntriesJob(string folder) : WriteJob
    {
        public override void Run(SqliteConnection connection)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM Entries WHERE Path = @p OR Path LIKE @prefix ESCAPE '\\'";
            cmd.Parameters.AddWithValue("@p", folder);
            cmd.Parameters.AddWithValue("@prefix", EscapeLike(folder) + "\\\\%");
            cmd.ExecuteNonQuery();
        }
    }

    // ================================================================= search (reads)

    /// Substring match on filename, then ranked with the same typo-tolerant FuzzyMatcher the per-pane
    /// search uses. Queries of 3+ characters use the trigram FTS index; shorter ones and any FTS
    /// error fall back to a LIKE scan.
    public static async Task<List<SearchIndexEntry>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken, int minRating = 0, bool caseSensitive = false, bool prioritizeFolders = false, string? scopePath = null)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new List<SearchIndexEntry>();
        }

        return await Task.Run(() =>
        {
            var candidates = new List<SearchIndexEntry>();
            var trimmed = query.Trim();

            // "Search From Here" (Alt+F9) - restricts results to scopePath itself plus everything
            // nested under it, same prefix-match pattern as AddExcludedPathJob's "everything under
            // this folder" delete. Pushed into the SQL WHERE clause (not a post-filter after
            // candidates come back) so a scoped search over a small folder isn't starved by
            // SqlCandidateLimit being spent on name matches elsewhere in a huge index.
            var normalizedScope = scopePath?.TrimEnd('\\', '/');
            var scopeClause = normalizedScope is null ? string.Empty : " AND (Path = @scopePath OR Path LIKE @scopePrefix ESCAPE '\\')";
            void AddScopeParameters(SqliteCommand cmd)
            {
                if (normalizedScope is not null)
                {
                    cmd.Parameters.AddWithValue("@scopePath", normalizedScope);
                    cmd.Parameters.AddWithValue("@scopePrefix", EscapeLike(normalizedScope) + "\\\\%");
                }
            }

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
                    cmd.CommandText = "SELECT Path, Name, DirectoryPath, IsDirectory, SizeBytes, ModifiedTicks FROM Entries WHERE Name LIKE @pattern ESCAPE '\\'" + scopeClause + " LIMIT @limit";
                    cmd.Parameters.AddWithValue("@pattern", "%" + EscapeLike(query) + "%");
                    AddScopeParameters(cmd);
                    cmd.Parameters.AddWithValue("@limit", SqlCandidateLimit);

                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        candidates.Add(ReadEntry(reader));
                    }
                }

                if (_ftsReady && trimmed.Length >= 3)
                {
                    try
                    {
                        using var cmd = connection.CreateCommand();
                        cmd.CommandText = "SELECT e.Path, e.Name, e.DirectoryPath, e.IsDirectory, e.SizeBytes, e.ModifiedTicks " +
                                          "FROM EntriesFts f JOIN Entries e ON e.rowid = f.rowid " +
                                          "WHERE f.Name MATCH @q" + scopeClause + " LIMIT @limit";
                        cmd.Parameters.AddWithValue("@q", "\"" + trimmed.Replace("\"", "\"\"") + "\"");
                        AddScopeParameters(cmd);
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
                if (FuzzyMatcher.TryScore(candidate.Name, query, out var score, caseSensitive))
                {
                    scored.Add((candidate, score));
                }
            }

            // Folder-priority is a stable primary sort key ahead of score, not a separate pass -
            // within "all folders, then all files" each group still ranks by fuzzy-match quality.
            var orderedScored = prioritizeFolders
                ? scored.OrderByDescending(s => s.Entry.IsDirectory).ThenByDescending(s => s.Score)
                : scored.OrderByDescending(s => s.Score);
            var ranked = orderedScored.Select(s => s.Entry);

            if (minRating > 0)
            {
                var byRating = ranked
                    .Select(e => e with { Rating = RatingService.GetEffective(e.Path, e.IsDirectory)?.Value })
                    .Where(e => e.Rating is { } r && r >= minRating - 0.0001);

                return (prioritizeFolders
                        ? byRating.OrderByDescending(e => e.IsDirectory).ThenByDescending(e => e.Rating)
                        : byRating.OrderByDescending(e => e.Rating))
                    .Take(maxResults)
                    .ToList();
            }

            return ranked
                .Take(maxResults)
                .Select(e => e with { Rating = RatingService.GetEffective(e.Path, e.IsDirectory)?.Value })
                .ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    /// Combined size on disk of the SQLite database plus its WAL/shared-memory sidecar files.
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

    /// Entry count per configured root, for Control Centre's Search Index list.
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

    /// One size bucket's hash-backfill progress, for Control Centre's Search Index table.
    public sealed record HashBucketRow(string Label, long Hashed, long Unhashed, long UnhashedBytes);

    // Bucket upper bounds in bytes (exclusive) - matches the backfill's own smallest-first sweep
    // order, so the table reads as "what's done" at the top and "what's left" at the bottom.
    private static readonly (long UpperBound, string Label)[] HashBuckets =
    [
        (1024L * 1024, "< 1 MB"),
        (10L * 1024 * 1024, "1-10 MB"),
        (100L * 1024 * 1024, "10-100 MB"),
        (1024L * 1024 * 1024, "100 MB-1 GB"),
        (long.MaxValue, "> 1 GB"),
    ];

    /// Hashed/unhashed file counts (and remaining bytes), bucketed by size. Zero-byte files and
    /// directories are excluded - the backfill never hashes them. Opens its own read connection so
    /// it can run alongside the writer and backfill threads.
    ///
    /// This is a full GROUP BY scan over every indexed row (millions on a large index) - callers
    /// must run it off the UI thread and poll it infrequently (Control Centre does so via
    /// Task.Run on a 120s timer, not the 1s status poll the rest of the panel uses).
    public static List<HashBucketRow> GetHashBucketSummary()
    {
        var rows = new List<HashBucketRow>();

        try
        {
            using var connection = OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT
                    CASE
                        WHEN SizeBytes < 1048576 THEN 0
                        WHEN SizeBytes < 10485760 THEN 1
                        WHEN SizeBytes < 104857600 THEN 2
                        WHEN SizeBytes < 1073741824 THEN 3
                        ELSE 4
                    END AS Bucket,
                    SUM(CASE WHEN Md5Hash IS NOT NULL THEN 1 ELSE 0 END) AS Hashed,
                    SUM(CASE WHEN Md5Hash IS NULL THEN 1 ELSE 0 END) AS Unhashed,
                    SUM(CASE WHEN Md5Hash IS NULL THEN SizeBytes ELSE 0 END) AS UnhashedBytes
                FROM Entries
                WHERE IsDirectory = 0 AND SizeBytes > 0
                GROUP BY Bucket
                """;

            using var reader = cmd.ExecuteReader();
            var byBucket = new Dictionary<int, (long Hashed, long Unhashed, long UnhashedBytes)>();
            while (reader.Read())
            {
                byBucket[reader.GetInt32(0)] = (reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
            }

            for (var i = 0; i < HashBuckets.Length; i++)
            {
                var (hashed, unhashed, unhashedBytes) = byBucket.TryGetValue(i, out var v) ? v : (0, 0, 0);
                rows.Add(new HashBucketRow(HashBuckets[i].Label, hashed, unhashed, unhashedBytes));
            }
        }
        catch (SqliteException ex)
        {
            LoggingService.LogWarning("SearchIndexService.GetHashBucketSummary", ex);
        }

        return rows;
    }

    /// True when <paramref name="path"/> is one of the configured index roots or sits underneath one.
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
    /// material for index-backed duplicate detection. Directories excluded. A Md5Hash that isn't
    /// 32 hex chars means "hash from disk".
    public static List<IndexedFile> GetIndexedFilesUnder(string path)
    {
        var result = new List<IndexedFile>();

        try
        {
            using var connection = OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Path, SizeBytes, Md5Hash FROM Entries WHERE IsDirectory = 0 AND (Path = @p OR Path LIKE @prefix ESCAPE '\\')";
            cmd.Parameters.AddWithValue("@p", path.TrimEnd('\\'));
            cmd.Parameters.AddWithValue("@prefix", EscapeLike(path.TrimEnd('\\')) + "\\\\%");

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

    // ================================================================= SQLite plumbing

    private static SqliteConnection OpenConnection()
    {
        Directory.CreateDirectory(DbDirectory);
        var connection = new SqliteConnection($"Data Source={DbPath}");
        connection.Open();

        using var pragma = connection.CreateCommand();
        // busy_timeout still matters for the brief windows a reader and the writer overlap, and for
        // the checkpoint. It no longer has to absorb writer-vs-writer contention - there is only one
        // writer now.
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=15000;";
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
            CREATE TABLE IF NOT EXISTS ExcludedPaths (Path TEXT PRIMARY KEY, AddedUtcTicks INTEGER NOT NULL);
            """;
        cmd.ExecuteNonQuery();

        // Migration for databases created before the Md5Hash column existed. MUST run before the
        // IX_Entries_SizeHash index, which references Md5Hash.
        try
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE Entries ADD COLUMN Md5Hash TEXT";
            alter.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
        }

        using var indexCmd = connection.CreateCommand();
        indexCmd.CommandText = "CREATE INDEX IF NOT EXISTS IX_Entries_SizeHash ON Entries(SizeBytes, Md5Hash);";
        indexCmd.ExecuteNonQuery();

        EnsureFtsSchema(connection);
    }

    /// External-content FTS5 table over Entries.Name with the trigram tokenizer, plus the triggers
    /// that keep it in sync. Triggers are dropped and recreated every launch so body changes take
    /// effect. Wrapped so a SQLite build without FTS5/trigram can't take down app launch.
    private static void EnsureFtsSchema(SqliteConnection connection)
    {
        try
        {
            using var cmd = connection.CreateCommand();
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
                -- A caller-supplied hash always wins. Otherwise keep the stored hash while the size
                -- is unchanged; a resized file's old hash is stale, so clear it and let the backfill
                -- recompute if the new size still collides with something.
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
