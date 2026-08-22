using FileExplorer.Helpers;
using FileExplorer.Models;

namespace FileExplorer.Services;

public interface IFileSystemService
{
    Task<List<FileSystemItem>> GetItemsAsync(string path, CancellationToken cancellationToken);
    IReadOnlyList<DriveInfo> GetReadyDrives();
    bool HasSubdirectories(string path);
    List<FolderNode> GetSubfolderNodes(string path);
    List<FileSystemItem> GetItems(string path);
    List<FileSystemItem> SearchRecursive(string root, string query, CancellationToken cancellationToken);
}

public sealed class FileSystemService : IFileSystemService
{
    /// Local branch wraps the existing synchronous GetItems (unchanged) in Task.Run; remote
    /// branch lists via whichever session is already open for that connection - see
    /// RemoteSessionManager, established by the left-rail "Remote Connections" click-to-connect
    /// flow before any navigation into that connection happens.
    public Task<List<FileSystemItem>> GetItemsAsync(string path, CancellationToken cancellationToken)
    {
        return RemotePathService.IsRemote(path)
            ? GetRemoteItemsAsync(path, cancellationToken)
            : Task.Run(() => GetItems(path), cancellationToken);
    }

    private async Task<List<FileSystemItem>> GetRemoteItemsAsync(string path, CancellationToken cancellationToken)
    {
        if (!RemotePathService.TryParse(path, out _, out var connectionId, out var remotePath))
        {
            return new List<FileSystemItem>();
        }

        var session = RemoteSessionManager.TryGetSession(connectionId)
            ?? throw new InvalidOperationException("Not connected - reconnect from the Remote Connections list.");

        var entries = await session.ListAsync(remotePath, cancellationToken).ConfigureAwait(false);

        return entries
            .OrderBy(e => !e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(e => new FileSystemItem
            {
                Name = e.Name,
                FullPath = RemotePathService.Combine(path, e.Name),
                IsDirectory = e.IsDirectory,
                SizeBytes = e.Size,
                Modified = e.Modified,
                Extension = e.IsDirectory ? string.Empty : Path.GetExtension(e.Name),
            })
            .ToList();
    }

    public IReadOnlyList<DriveInfo> GetReadyDrives()
    {
        try
        {
            return DriveInfo.GetDrives().Where(d => d.IsReady).ToList();
        }
        catch (IOException)
        {
            return Array.Empty<DriveInfo>();
        }
    }

    public bool HasSubdirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).Any();
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }

    public List<FolderNode> GetSubfolderNodes(string path)
    {
        var result = new List<FolderNode>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(path).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var attrs = File.GetAttributes(dir);
                if (attrs.HasFlag(FileAttributes.Hidden) || attrs.HasFlag(FileAttributes.System))
                {
                    continue;
                }
                result.Add(new FolderNode { Name = Path.GetFileName(dir), FullPath = dir });
            }
        }
        // Deliberately not logged: this fires routinely for ordinary ACL-protected system folders
        // (System Volume Information, $Recycle.Bin, ...) on every folder listing - logging it would
        // flood app.log with expected noise rather than surfacing real problems.
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }

        return result;
    }

    public List<FileSystemItem> GetItems(string path)
    {
        var items = new List<FileSystemItem>();

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(path).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var info = new DirectoryInfo(dir);
                if (info.Attributes.HasFlag(FileAttributes.Hidden) || info.Attributes.HasFlag(FileAttributes.System))
                {
                    continue;
                }
                var (linkKind, linkTarget) = GetLinkInfo(info.Attributes, info.FullName);
                items.Add(new FileSystemItem
                {
                    Name = info.Name,
                    FullPath = info.FullName,
                    IsDirectory = true,
                    Modified = info.LastWriteTimeUtc,
                    Attributes = info.Attributes,
                    LinkKind = linkKind,
                    LinkTarget = linkTarget,
                    TagColor = TagService.GetColor(info.FullName),
                    CloudBadge = CloudProviderService.GetBadgeGlyph(info.FullName),
                    SyncRole = SettingsService.Current.EnableSyncTasks ? SyncTaskService.GetRole(info.FullName) : SyncRole.None,
                    IsWatched = SettingsService.Current.EnableFolderWatching && WatchService.IsWatched(info.FullName),
                });
            }
        }
        // Deliberately not logged - see the comment in GetSubfolderNodes above.
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }

        try
        {
            foreach (var file in Directory.EnumerateFiles(path).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var info = new FileInfo(file);
                if (info.Attributes.HasFlag(FileAttributes.Hidden) || info.Attributes.HasFlag(FileAttributes.System))
                {
                    continue;
                }
                var (linkKind, linkTarget) = GetLinkInfo(info.Attributes, info.FullName);
                items.Add(new FileSystemItem
                {
                    Name = info.Name,
                    FullPath = info.FullName,
                    IsDirectory = false,
                    SizeBytes = info.Length,
                    Modified = info.LastWriteTimeUtc,
                    Extension = info.Extension,
                    Attributes = info.Attributes,
                    LinkKind = linkKind,
                    LinkTarget = linkTarget,
                    TagColor = TagService.GetColor(info.FullName),
                    CloudBadge = CloudProviderService.GetBadgeGlyph(info.FullName),
                });
            }
        }
        // Deliberately not logged - see the comment in GetSubfolderNodes above.
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }

        return items;
    }

    /// Only touches ReparsePointService (a P/Invoke call plus a LinkTarget read) when the
    /// ReparsePoint attribute is actually set - every other item in a folder listing pays nothing
    /// extra for this.
    private static (ReparsePointKind Kind, string? Target) GetLinkInfo(FileAttributes attributes, string fullPath)
    {
        if (!attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return (ReparsePointKind.None, null);
        }

        return (ReparsePointService.GetKind(fullPath), ReparsePointService.TryGetLinkTarget(fullPath));
    }

    private const int MaxRecursiveSearchResults = 500;
    private const int MaxRecursiveSearchCandidates = 3000;
    private const int ContentSearchPeekChars = 8000;
    private const int ContentMatchScore = -1;

    /// Recursive fuzzy filename + (for text/code files) content search under root, ranked by
    /// relevance and capped for responsiveness.
    public List<FileSystemItem> SearchRecursive(string root, string query, CancellationToken cancellationToken)
    {
        var candidates = new List<(FileSystemItem Item, int Score)>();

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories);
        }
        catch (IOException) { return new List<FileSystemItem>(); }
        catch (UnauthorizedAccessException) { return new List<FileSystemItem>(); }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            FileInfo info;
            try
            {
                info = new FileInfo(file);
            }
            catch (IOException) { continue; }

            if (info.Attributes.HasFlag(FileAttributes.Hidden) || info.Attributes.HasFlag(FileAttributes.System))
            {
                continue;
            }

            var nameMatched = FuzzyMatcher.TryScore(info.Name, query, out var nameScore);
            var contentMatched = !nameMatched && IconHelper.IsPreviewableText(info.Extension) && FileContentContains(file, query);

            if (nameMatched || contentMatched)
            {
                candidates.Add((new FileSystemItem
                {
                    Name = info.Name,
                    FullPath = info.FullName,
                    IsDirectory = false,
                    SizeBytes = info.Length,
                    Modified = info.LastWriteTimeUtc,
                    Extension = info.Extension,
                    Attributes = info.Attributes,
                    TagColor = TagService.GetColor(info.FullName),
                    CloudBadge = CloudProviderService.GetBadgeGlyph(info.FullName),
                }, nameMatched ? nameScore : ContentMatchScore));

                if (candidates.Count >= MaxRecursiveSearchCandidates)
                {
                    break;
                }
            }
        }

        return candidates
            .OrderByDescending(c => c.Score)
            .Take(MaxRecursiveSearchResults)
            .Select(c => c.Item)
            .ToList();
    }

    private static bool FileContentContains(string path, string query)
    {
        try
        {
            using var reader = new StreamReader(path);
            var buffer = new char[ContentSearchPeekChars];
            var read = reader.Read(buffer, 0, buffer.Length);
            return new string(buffer, 0, read).Contains(query, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
