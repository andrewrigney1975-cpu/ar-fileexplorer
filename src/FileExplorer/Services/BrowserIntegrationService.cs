using System.Text.Json;
using Microsoft.Win32;

namespace FileExplorer.Services;

/// Registers/unregisters this app as a Chrome/Edge Native Messaging host, scoped to one caller
/// extension ID, so a browser extension can ask "does a folder named X already exist?" against the
/// Search Everywhere index (see NativeMessagingHostService for the actual query). Deliberately NOT
/// a local HTTP/WebSocket server - that would open a port any webpage's script could probe, not
/// just the intended extension. Native Messaging never opens a network port at all: the browser
/// launches the host process directly, and only origins listed in the manifest's allowed_origins
/// are permitted to.
///
/// Firefox uses a different manifest key (allowed_extensions, keyed by an extension id@domain
/// string rather than a chrome-extension:// origin) and a different registry hive - out of scope
/// for v1, Chrome/Edge only.
public static class BrowserIntegrationService
{
    private const string HostName = "com.enfylexplorer.searchindex";

    private static string ManifestPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FileExplorerApp", "native-messaging-host-manifest.json");

    private static string ChromeRegistryKey => $@"Software\Google\Chrome\NativeMessagingHosts\{HostName}";
    private static string EdgeRegistryKey => $@"Software\Microsoft\Edge\NativeMessagingHosts\{HostName}";

    public static bool IsRegistered =>
        Registry.CurrentUser.OpenSubKey(ChromeRegistryKey) is not null ||
        Registry.CurrentUser.OpenSubKey(EdgeRegistryKey) is not null;

    /// extensionId is the 32-character Chrome/Edge extension ID (Manage Extensions > Developer mode
    /// shows it under the extension's name) - the manifest's allowed_origins restricts which
    /// extension is permitted to invoke this host at all.
    public static void Register(string extensionId)
    {
        var exePath = Environment.ProcessPath ?? throw new InvalidOperationException("Could not resolve the running exe's path.");

        var manifest = new
        {
            name = HostName,
            description = "enfyl Explorer - folder-exists lookup against the Search Everywhere index",
            path = exePath,
            type = "stdio",
            allowed_origins = new[] { $"chrome-extension://{extensionId}/" },
        };

        Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath)!);
        File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        WriteRegistryPointer(ChromeRegistryKey);
        WriteRegistryPointer(EdgeRegistryKey);
    }

    public static void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree(ChromeRegistryKey, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(EdgeRegistryKey, throwOnMissingSubKey: false);

        try
        {
            File.Delete(ManifestPath);
        }
        catch (IOException)
        {
            // Not fatal - an orphaned manifest file with no registry pointer to it is inert.
        }
    }

    private static void WriteRegistryPointer(string keyPath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        key.SetValue(null, ManifestPath);
    }
}
