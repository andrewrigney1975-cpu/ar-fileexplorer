namespace FileExplorer.Services;

/// File-backed CRUD over the user's saved scripts - each script is a plain .js file, named after
/// the script, under %LocalAppData%\FileExplorerApp\Scripts\. Kept on disk (not a JSON index) so
/// scripts stay transparent/inspectable/copyable outside the app too.
public static class ScriptService
{
    private static readonly string FolderPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileExplorerApp", "Scripts");

    /// Scripts the app ships. Written to the Scripts folder on startup only if a file of that name
    /// doesn't already exist, so a user's own edits (or deletions, until next launch) are respected.
    private static readonly Dictionary<string, string> BuiltInScripts = new()
    {
        ["DeleteFilesInFolder"] =
            """
            // DeleteFilesInFolder
            // Moves every file and subfolder inside the selected folder(s) to the Recycle Bin,
            // leaving the folder itself in place (empty). Hidden/system items are left untouched.
            //
            // Right-click a folder -> Run Action... -> DeleteFilesInFolder.

            var folders = selection().filter(function (item) { return item.IsDirectory; });

            if (folders.length === 0) {
                notify("DeleteFilesInFolder", "Right-click a folder to use this action.");
            } else {
                var total = 0;

                folders.forEach(function (folder) {
                    var children = listFiles(folder.FullPath);
                    if (children.length === 0) {
                        log("\"" + folder.Name + "\" is already empty.");
                        return;
                    }

                    if (!confirm("Move all " + children.length + " item(s) inside \"" + folder.Name +
                                 "\" to the Recycle Bin?")) {
                        log("Skipped \"" + folder.Name + "\".");
                        return;
                    }

                    children.forEach(function (child) {
                        deleteItem(child.FullPath, false);
                        log("Recycled " + child.FullPath);
                        total++;
                    });
                });

                refresh();

                if (total > 0) {
                    notify("DeleteFilesInFolder", "Moved " + total + " item(s) to the Recycle Bin.");
                }
            }
            """,
    };

    /// Writes any missing built-in scripts. Called once at startup.
    public static void EnsureBuiltInScripts()
    {
        try
        {
            Directory.CreateDirectory(FolderPath);
            foreach (var (name, content) in BuiltInScripts)
            {
                var path = PathFor(name);
                if (!File.Exists(path))
                {
                    File.WriteAllText(path, content.ReplaceLineEndings());
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoggingService.LogWarning("ScriptService.EnsureBuiltInScripts", ex);
        }
    }

    public static List<string> List()
    {
        try
        {
            if (!Directory.Exists(FolderPath))
            {
                return new List<string>();
            }

            return Directory.EnumerateFiles(FolderPath, "*.js")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => n is not null)
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoggingService.LogWarning("ScriptService.List", ex);
            return new List<string>();
        }
    }

    public static string? Load(string name)
    {
        try
        {
            var path = PathFor(name);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoggingService.LogWarning($"ScriptService.Load: {name}", ex);
            return null;
        }
    }

    public static void Save(string name, string content)
    {
        try
        {
            Directory.CreateDirectory(FolderPath);
            File.WriteAllText(PathFor(name), content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoggingService.LogWarning($"ScriptService.Save: {name}", ex);
        }
    }

    public static void Delete(string name)
    {
        try
        {
            var path = PathFor(name);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoggingService.LogWarning($"ScriptService.Delete: {name}", ex);
        }
    }

    /// True if name isn't already used by another script (case-insensitive, matching the filesystem).
    public static bool IsNameAvailable(string name) => !File.Exists(PathFor(name));

    /// Renames the script's file on disk. Does not touch WatchService/ScheduleService - callers
    /// that need a renamed script to keep working with a watched folder or scheduled run must also
    /// call WatchService.RenameScriptReferences / ScheduleService.RenameScriptTarget.
    public static bool Rename(string oldName, string newName)
    {
        if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            var oldPath = PathFor(oldName);
            if (!File.Exists(oldPath) || !IsNameAvailable(newName))
            {
                return false;
            }

            File.Move(oldPath, PathFor(newName));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoggingService.LogWarning($"ScriptService.Rename: {oldName} -> {newName}", ex);
            return false;
        }
    }

    private static string PathFor(string name) => Path.Combine(FolderPath, name + ".js");
}
