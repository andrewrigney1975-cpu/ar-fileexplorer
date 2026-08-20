namespace FileExplorer.Services;

public abstract record UndoAction
{
    public abstract string Description { get; }

    public abstract Task UndoAsync();
}

public sealed record CreateFolderUndo(string Path) : UndoAction
{
    public override string Description => $"Undo New Folder \"{System.IO.Path.GetFileName(Path)}\"";

    public override Task UndoAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                if (Directory.Exists(Path) && !Directory.EnumerateFileSystemEntries(Path).Any())
                {
                    Directory.Delete(Path);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        });
    }
}

public sealed record CreateLinkUndo(string Path) : UndoAction
{
    public override string Description => $"Undo Create Link \"{System.IO.Path.GetFileName(Path)}\"";

    public override Task UndoAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                // A link is a leaf node regardless of whether it points at a file or a folder -
                // recursive:true here only matters for a directory-typed link, and (verified) still
                // removes just the link itself rather than following it into its target's contents.
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
                else if (File.Exists(Path))
                {
                    File.Delete(Path);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        });
    }
}

public sealed record RenameUndo(string OldPath, string NewPath) : UndoAction
{
    public override string Description => $"Undo Rename \"{System.IO.Path.GetFileName(NewPath)}\"";

    public override Task UndoAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                if (Directory.Exists(NewPath))
                {
                    Directory.Move(NewPath, OldPath);
                }
                else if (File.Exists(NewPath))
                {
                    File.Move(NewPath, OldPath);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        });
    }
}

public sealed record MoveUndo(IReadOnlyList<(string Source, string Destination)> Items, string? CreatedDestinationFolder = null) : UndoAction
{
    public override string Description => $"Undo Move ({Items.Count} item{(Items.Count == 1 ? "" : "s")})";

    public override Task UndoAsync()
    {
        return Task.Run(() =>
        {
            foreach (var (source, destination) in Items)
            {
                try
                {
                    MoveBack(destination, source);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            // Only remove the destination folder if this move itself created it (e.g. "Move to new
            // folder..."); a pre-existing folder the user moved things into is never deleted by undo.
            if (CreatedDestinationFolder is { } folder)
            {
                try
                {
                    if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                    {
                        Directory.Delete(folder);
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        });
    }

    private static void MoveBack(string destination, string originalSource)
    {
        if (Directory.Exists(destination))
        {
            try
            {
                Directory.Move(destination, originalSource);
            }
            catch (IOException)
            {
                CopyDirectoryRecursive(destination, originalSource);
                Directory.Delete(destination, recursive: true);
            }
        }
        else if (File.Exists(destination))
        {
            File.Move(destination, originalSource);
        }
    }

    private static void CopyDirectoryRecursive(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, System.IO.Path.Combine(destination, System.IO.Path.GetFileName(file)));
        }

        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            CopyDirectoryRecursive(dir, System.IO.Path.Combine(destination, System.IO.Path.GetFileName(dir)));
        }
    }
}

public sealed record CopyUndo(IReadOnlyList<string> CreatedPaths) : UndoAction
{
    public override string Description => $"Undo Copy ({CreatedPaths.Count} item{(CreatedPaths.Count == 1 ? "" : "s")})";

    public override Task UndoAsync()
    {
        return Task.Run(() =>
        {
            foreach (var path in CreatedPaths)
            {
                try
                {
                    if (Directory.Exists(path))
                    {
                        Directory.Delete(path, recursive: true);
                    }
                    else if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        });
    }
}
