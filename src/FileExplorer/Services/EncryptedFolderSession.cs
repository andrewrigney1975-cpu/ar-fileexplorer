using FileExplorer.Models;

namespace FileExplorer.Services;

/// In-memory, session-only state for unlocked encrypted folder containers: the derived AES key and
/// decrypted index stay in memory only (never written to disk), and each opened file is decrypted to
/// a per-container temp folder that's wiped on Lock - so the only durable artifact on disk is ever
/// the encrypted container itself. Nothing here persists across an app restart by design, matching
/// the "always re-locked on next launch" convention already established for Private Virtual Folders'
/// PIN gate.
public static class EncryptedFolderSession
{
    private sealed class Unlocked
    {
        public required byte[] Key;
        public required List<EncryptedFolderService.IndexEntry> Entries;
        public required string TempDir;

        /// Serializes add/delete/compact calls against this one container - each rewrites the whole
        /// file, so two concurrent rewrites racing would corrupt or lose one of them.
        public readonly SemaphoreSlim MutationLock = new(1, 1);
    }

    private static readonly Dictionary<string, Unlocked> _unlocked = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    /// Raised after a container is unlocked or locked, so open panes browsing it can react.
    public static event EventHandler? Changed;

    public static bool IsUnlocked(string containerPath)
    {
        lock (Gate)
        {
            return _unlocked.ContainsKey(containerPath);
        }
    }

    /// True on success. False for a wrong PIN or a corrupt/foreign file - TryOpenAsync already
    /// can't distinguish those without leaking timing information either way, so neither can this.
    public static async Task<bool> TryUnlockAsync(string containerPath, string pin, CancellationToken ct)
    {
        var opened = await EncryptedFolderService.TryOpenAsync(containerPath, pin, ct);
        if (opened is null)
        {
            return false;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "docket-encrypted", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        lock (Gate)
        {
            _unlocked[containerPath] = new Unlocked { Key = opened.Value.Key, Entries = opened.Value.Entries, TempDir = tempDir };
        }

        Changed?.Invoke(null, EventArgs.Empty);
        return true;
    }

    public static void Lock(string containerPath)
    {
        Unlocked? unlocked;
        lock (Gate)
        {
            if (!_unlocked.TryGetValue(containerPath, out unlocked))
            {
                return;
            }

            _unlocked.Remove(containerPath);
        }

        try
        {
            if (Directory.Exists(unlocked.TempDir))
            {
                foreach (var file in Directory.EnumerateFiles(unlocked.TempDir, "*", SearchOption.AllDirectories))
                {
                    try { File.WriteAllBytes(file, Array.Empty<byte>()); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }

                Directory.Delete(unlocked.TempDir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoggingService.LogWarning("EncryptedFolderSession.Lock", ex);
        }

        unlocked.MutationLock.Dispose();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// Locks every currently-unlocked container - called on app shutdown so no decrypted temp file
    /// is ever left behind between sessions.
    public static void LockAll()
    {
        List<string> containers;
        lock (Gate)
        {
            containers = new List<string>(_unlocked.Keys);
        }

        foreach (var container in containers)
        {
            Lock(container);
        }
    }

    /// Lists the direct children of <paramref name="relativePath"/> ("/"-rooted, "/" for the
    /// container's own root) - built entirely from the decrypted in-memory index, no disk I/O.
    public static List<FileSystemItem> GetChildren(string containerPath, string relativePath)
    {
        var unlocked = GetUnlockedOrThrow(containerPath);
        var key = EncryptedFolderPathService.NormalizeIndexKey(relativePath);

        var items = new List<FileSystemItem>();
        foreach (var entry in unlocked.Entries)
        {
            var parent = ParentKey(entry.RelativePath);
            if (!string.Equals(parent, key, StringComparison.Ordinal))
            {
                continue;
            }

            var name = entry.RelativePath[(entry.RelativePath.LastIndexOf('/') + 1)..];
            var childPath = EncryptedFolderPathService.Combine(EncryptedFolderPathService.BuildRoot(containerPath).TrimEnd('/') + relativePath, name);
            items.Add(new FileSystemItem
            {
                Name = name,
                FullPath = childPath,
                IsDirectory = entry.IsDirectory,
                SizeBytes = entry.SizeBytes,
                Modified = new DateTimeOffset(entry.ModifiedUtcTicks, TimeSpan.Zero),
                Extension = entry.IsDirectory ? string.Empty : Path.GetExtension(name),
                Attributes = (FileAttributes)entry.Attributes,
            });
        }

        return items.OrderBy(i => !i.IsDirectory).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// Decrypts one file to this container's session temp folder (creating it once, reusing it on
    /// repeat opens of the same file within the session) and returns the temp path to hand to
    /// Process.Start/the preview pane.
    public static async Task<string> DecryptToTempAsync(string containerPath, string relativePath, CancellationToken ct)
    {
        var unlocked = GetUnlockedOrThrow(containerPath);
        var key = EncryptedFolderPathService.NormalizeIndexKey(relativePath);

        var entry = unlocked.Entries.FirstOrDefault(e => !e.IsDirectory && string.Equals(e.RelativePath, key, StringComparison.Ordinal))
            ?? throw new FileNotFoundException("That file is no longer in the encrypted folder's index.", relativePath);

        var destPath = Path.Combine(unlocked.TempDir, key.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        if (!File.Exists(destPath))
        {
            await EncryptedFolderService.DecryptEntryToFileAsync(containerPath, unlocked.Key, entry, destPath, ct);
        }

        return destPath;
    }

    /// Encrypts a real file into this container as a new entry under the given "/"-rooted parent
    /// path ("/" for the container root), updates the in-memory index, and raises Changed so any
    /// open pane browsing this container refreshes. Rewrites the whole container on disk - see
    /// EncryptedFolderService.RewriteContainerAsync.
    public static async Task AddFileAsync(string containerPath, string parentRelativePath, string sourceFilePath, CancellationToken ct)
    {
        var unlocked = GetUnlockedOrThrow(containerPath);
        var parentKey = EncryptedFolderPathService.NormalizeIndexKey(parentRelativePath);

        await unlocked.MutationLock.WaitAsync(ct);
        try
        {
            unlocked.Entries = await EncryptedFolderService.AddFileEntryAsync(
                containerPath, unlocked.Key, unlocked.Entries, parentKey, sourceFilePath, ct);
        }
        finally
        {
            unlocked.MutationLock.Release();
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// Recursively encrypts a real folder (and everything inside it) into this container as a new
    /// subtree under the given parent path.
    public static async Task AddFolderAsync(string containerPath, string parentRelativePath, string sourceFolderPath, CancellationToken ct)
    {
        var unlocked = GetUnlockedOrThrow(containerPath);
        var parentKey = EncryptedFolderPathService.NormalizeIndexKey(parentRelativePath);

        await unlocked.MutationLock.WaitAsync(ct);
        try
        {
            unlocked.Entries = await EncryptedFolderService.AddFolderTreeEntriesAsync(
                containerPath, unlocked.Key, unlocked.Entries, parentKey, sourceFolderPath, ct);
        }
        finally
        {
            unlocked.MutationLock.Release();
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// Adds a new, empty directory entry under the given parent path (the encrypted-folder
    /// equivalent of "New Folder").
    public static async Task NewFolderAsync(string containerPath, string parentRelativePath, string name, CancellationToken ct)
    {
        var unlocked = GetUnlockedOrThrow(containerPath);
        var parentKey = EncryptedFolderPathService.NormalizeIndexKey(parentRelativePath);

        await unlocked.MutationLock.WaitAsync(ct);
        try
        {
            unlocked.Entries = await EncryptedFolderService.AddEmptyFolderEntryAsync(
                containerPath, unlocked.Key, unlocked.Entries, parentKey, name, ct);
        }
        finally
        {
            unlocked.MutationLock.Release();
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// Removes the entry at the given "/"-rooted path - and, for a directory, everything nested
    /// under it - from the container. Permanent: there is no Recycle Bin equivalent inside a
    /// container, matching how a remote-connection delete has no undo either.
    public static async Task DeleteEntryAsync(string containerPath, string relativePath, CancellationToken ct)
    {
        var unlocked = GetUnlockedOrThrow(containerPath);
        var key = EncryptedFolderPathService.NormalizeIndexKey(relativePath);

        await unlocked.MutationLock.WaitAsync(ct);
        try
        {
            unlocked.Entries = await EncryptedFolderService.RemoveEntryAsync(
                containerPath, unlocked.Key, unlocked.Entries, key, ct);
        }
        finally
        {
            unlocked.MutationLock.Release();
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// Manually reclaims any dead space in the container (see EncryptedFolderService.CompactAsync -
    /// normally a no-op since every add/delete already rebuilds without dead bytes).
    public static async Task CompactAsync(string containerPath, CancellationToken ct)
    {
        var unlocked = GetUnlockedOrThrow(containerPath);

        await unlocked.MutationLock.WaitAsync(ct);
        try
        {
            unlocked.Entries = await EncryptedFolderService.CompactAsync(containerPath, unlocked.Key, unlocked.Entries, ct);
        }
        finally
        {
            unlocked.MutationLock.Release();
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static (byte[] Key, List<EncryptedFolderService.IndexEntry> Entries)? TryGetUnlocked(string containerPath)
    {
        lock (Gate)
        {
            return _unlocked.TryGetValue(containerPath, out var unlocked) ? (unlocked.Key, unlocked.Entries) : null;
        }
    }

    private static Unlocked GetUnlockedOrThrow(string containerPath)
    {
        lock (Gate)
        {
            if (_unlocked.TryGetValue(containerPath, out var unlocked))
            {
                return unlocked;
            }
        }

        throw new InvalidOperationException("This encrypted folder isn't unlocked - open it with its PIN first.");
    }

    private static string ParentKey(string relativePath)
    {
        var lastSlash = relativePath.LastIndexOf('/');
        return lastSlash < 0 ? string.Empty : relativePath[..lastSlash];
    }
}
