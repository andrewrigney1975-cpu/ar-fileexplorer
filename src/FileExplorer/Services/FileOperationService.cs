namespace FileExplorer.Services;

public enum FileDropOperation
{
    Move,
    Copy,
}

public static class FileOperationService
{
    /// True when two paths share the same drive/volume root.
    public static bool SameDrive(string pathA, string pathB)
    {
        var rootA = Path.GetPathRoot(pathA);
        var rootB = Path.GetPathRoot(pathB);
        return string.Equals(rootA, rootB, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task ExecuteAsync(IReadOnlyList<string> sourcePaths, string targetFolder, FileDropOperation operation)
    {
        await Task.Run(() =>
        {
            foreach (var source in sourcePaths)
            {
                try
                {
                    var name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar));
                    var destination = Path.Combine(targetFolder, name);

                    if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                    {
                        continue; // dropped onto its own folder
                    }

                    destination = MakeUniqueIfNeeded(destination);

                    if (Directory.Exists(source))
                    {
                        if (operation == FileDropOperation.Copy)
                        {
                            CopyDirectory(source, destination);
                        }
                        else
                        {
                            Directory.Move(source, destination);
                        }
                    }
                    else if (File.Exists(source))
                    {
                        if (operation == FileDropOperation.Copy)
                        {
                            File.Copy(source, destination, overwrite: false);
                        }
                        else
                        {
                            File.Move(source, destination);
                        }
                    }
                }
                catch (IOException)
                {
                    // Best-effort: skip items that fail (locked file, permissions, etc.) and continue with the rest.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        });
    }

    private static string MakeUniqueIfNeeded(string destination)
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

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);

        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            var dest = Path.Combine(destinationDir, Path.GetFileName(file));
            File.Copy(file, dest, overwrite: false);
        }

        foreach (var subDir in Directory.EnumerateDirectories(sourceDir))
        {
            var dest = Path.Combine(destinationDir, Path.GetFileName(subDir));
            CopyDirectory(subDir, dest);
        }
    }
}
