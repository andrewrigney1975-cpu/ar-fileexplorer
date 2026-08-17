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

    /// False if dropping sourcePaths onto targetFolder would be a no-op or corrupt the tree
    /// (dropping an item onto itself, or a folder into its own subtree).
    public static bool IsValidDropTarget(IEnumerable<string> sourcePaths, string targetFolder)
    {
        var normalizedTarget = Path.GetFullPath(targetFolder).TrimEnd(Path.DirectorySeparatorChar);

        foreach (var source in sourcePaths)
        {
            var normalizedSource = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar);

            if (string.Equals(normalizedSource, normalizedTarget, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var parent = Path.GetDirectoryName(normalizedSource);
            if (string.Equals(parent, normalizedTarget, StringComparison.OrdinalIgnoreCase))
            {
                return false; // already there
            }

            if (Directory.Exists(source) &&
                normalizedTarget.StartsWith(normalizedSource + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return false; // would move a folder into its own descendant
            }
        }

        return true;
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
