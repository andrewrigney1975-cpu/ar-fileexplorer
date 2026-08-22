namespace FileExplorer.Services;

/// File-backed CRUD over the user's saved scripts - each script is a plain .js file, named after
/// the script, under %LocalAppData%\FileExplorerApp\Scripts\. Kept on disk (not a JSON index) so
/// scripts stay transparent/inspectable/copyable outside the app too.
public static class ScriptService
{
    private static readonly string FolderPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileExplorerApp", "Scripts");

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
