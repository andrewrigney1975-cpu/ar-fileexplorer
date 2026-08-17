namespace FileExplorer.Services;

public enum FileDropOperation
{
    Move,
    Copy,
}

/// Shared path helpers used by the file operation queue.
public static class FileOperationService
{
    /// True when two paths share the same drive/volume root.
    public static bool SameDrive(string pathA, string pathB)
    {
        var rootA = Path.GetPathRoot(pathA);
        var rootB = Path.GetPathRoot(pathB);
        return string.Equals(rootA, rootB, StringComparison.OrdinalIgnoreCase);
    }

    /// Appends " (2)", " (3)", ... to avoid overwriting an existing file or folder at the destination.
    public static string MakeUniqueDestination(string destination)
    {
        if (!File.Exists(destination) && !Directory.Exists(destination))
        {
            return destination;
        }

        var dir = Path.GetDirectoryName(destination)!;
        var name = Path.GetFileNameWithoutExtension(destination);
        var ext = Path.GetExtension(destination);

        for (int i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}
