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
}
