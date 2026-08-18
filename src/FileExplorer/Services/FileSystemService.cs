using FileExplorer.Models;

namespace FileExplorer.Services;

public static class FileSystemService
{
    public static IReadOnlyList<DriveInfo> GetReadyDrives()
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

    public static bool HasSubdirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).Any();
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }

    public static List<FolderNode> GetSubfolderNodes(string path)
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
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }

        return result;
    }

    public static List<FileSystemItem> GetItems(string path)
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
                items.Add(new FileSystemItem
                {
                    Name = info.Name,
                    FullPath = info.FullName,
                    IsDirectory = true,
                    Modified = info.LastWriteTimeUtc,
                    TagColor = TagService.GetColor(info.FullName),
                });
            }
        }
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
                items.Add(new FileSystemItem
                {
                    Name = info.Name,
                    FullPath = info.FullName,
                    IsDirectory = false,
                    SizeBytes = info.Length,
                    Modified = info.LastWriteTimeUtc,
                    Extension = info.Extension,
                    TagColor = TagService.GetColor(info.FullName),
                });
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }

        return items;
    }

    private const int MaxRecursiveSearchResults = 500;
    private const int ContentSearchPeekChars = 8000;

    /// Recursive filename + (for text/code files) content search under root, capped for responsiveness.
    public static List<FileSystemItem> SearchRecursive(string root, string query, CancellationToken cancellationToken)
    {
        var results = new List<FileSystemItem>();

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories);
        }
        catch (IOException) { return results; }
        catch (UnauthorizedAccessException) { return results; }

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

            var nameMatch = info.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
            var contentMatch = !nameMatch && IconHelper.IsPreviewableText(info.Extension) && FileContentContains(file, query);

            if (nameMatch || contentMatch)
            {
                results.Add(new FileSystemItem
                {
                    Name = info.Name,
                    FullPath = info.FullName,
                    IsDirectory = false,
                    SizeBytes = info.Length,
                    Modified = info.LastWriteTimeUtc,
                    Extension = info.Extension,
                    TagColor = TagService.GetColor(info.FullName),
                });

                if (results.Count >= MaxRecursiveSearchResults)
                {
                    break;
                }
            }
        }

        return results;
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
