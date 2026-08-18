using FileExplorer.Models;

namespace FileExplorer.Services;

/// Detects locally-installed cloud storage sync folders (OneDrive, Google Drive, Dropbox, Box)
/// and reports per-file cloud placeholder status via the Windows Cloud Files attribute bits.
/// This deliberately does not talk to any provider's web API - it only reads what each desktop
/// sync client already mirrors onto the local filesystem, so it needs no accounts or credentials.
public static class CloudProviderService
{
    private const int FileAttributeRecallOnOpen = 0x00040000;
    private const int FileAttributePinned = 0x00080000;
    private const int FileAttributeRecallOnDataAccess = 0x00400000;

    private static List<CloudLocation>? _cache;

    public static List<CloudLocation> DetectLocations()
    {
        if (_cache is not null)
        {
            return _cache;
        }

        var locations = new List<CloudLocation>();
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        foreach (var envVar in new[] { "OneDriveCommercial", "OneDriveConsumer", "OneDrive" })
        {
            var path = Environment.GetEnvironmentVariable(envVar);
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path) && !locations.Any(l => l.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                locations.Add(new CloudLocation("OneDrive", path, "OneDrive"));
            }
        }

        var googleDrive = Path.Combine(userProfile, "Google Drive");
        if (Directory.Exists(googleDrive))
        {
            locations.Add(new CloudLocation("Google Drive", googleDrive, "GoogleDrive"));
        }

        var dropbox = TryFindDropboxFolder();
        if (dropbox is not null)
        {
            locations.Add(new CloudLocation("Dropbox", dropbox, "Dropbox"));
        }

        var box = Path.Combine(userProfile, "Box");
        if (Directory.Exists(box))
        {
            locations.Add(new CloudLocation("Box", box, "Box"));
        }

        _cache = locations;
        return locations;
    }

    private static string? TryFindDropboxFolder()
    {
        var infoPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dropbox", "info.json");

        if (!File.Exists(infoPath))
        {
            return null;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(infoPath));
            if (doc.RootElement.TryGetProperty("personal", out var personal) &&
                personal.TryGetProperty("path", out var path))
            {
                var dir = path.GetString();
                if (dir is not null && Directory.Exists(dir))
                {
                    return dir;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
        }

        return null;
    }

    /// True if fullPath falls under any detected cloud provider's sync root.
    public static bool IsUnderCloudRoot(string fullPath)
    {
        foreach (var loc in DetectLocations())
        {
            if (fullPath.StartsWith(loc.Path, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// Glyph for an online-only placeholder ("cloud") or an always-available pinned item
    /// ("checkmark"); null for ordinary locally-cached files or files outside a cloud root.
    public static string? GetBadgeGlyph(string fullPath)
    {
        if (!IsUnderCloudRoot(fullPath))
        {
            return null;
        }

        try
        {
            var attrs = (int)File.GetAttributes(fullPath);

            if ((attrs & (FileAttributeRecallOnDataAccess | FileAttributeRecallOnOpen)) != 0)
            {
                return ""; // cloud outline: online-only, not downloaded
            }

            if ((attrs & FileAttributePinned) != 0)
            {
                return ""; // checkmark: always kept on this device
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return null;
    }
}
