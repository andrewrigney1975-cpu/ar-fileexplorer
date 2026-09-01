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
/// The host is registered under BOTH the current identifier (`com.docket.searchindex`) and the
/// legacy one from before the "enfyl Explorer" -> "Docket" rename (`com.enfylexplorer.searchindex`),
/// so an already-published extension that still calls `sendNativeMessage("com.enfylexplorer.searchindex", ...)`
/// keeps working without an extension update. Each name gets its own manifest file, since the
/// browser requires the manifest's `name` field to match the registry key name it was found under.
///
/// Firefox uses a different manifest key (allowed_extensions, keyed by an extension id@domain
/// string rather than a chrome-extension:// origin) and a different registry hive - out of scope
/// for v1, Chrome/Edge only.
public static class BrowserIntegrationService
{
    private const string HostName = "com.docket.searchindex";
    private const string LegacyHostName = "com.enfylexplorer.searchindex";

    private static readonly (string HostName, string ManifestFile)[] Registrations =
    {
        (HostName, "native-messaging-host-manifest.json"),
        (LegacyHostName, "native-messaging-host-manifest.legacy.json"),
    };

    private static string ManifestPath(string fileName) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FileExplorerApp", fileName);

    private static string ChromeRegistryKey(string hostName) => $@"Software\Google\Chrome\NativeMessagingHosts\{hostName}";

    private static string EdgeRegistryKey(string hostName) => $@"Software\Microsoft\Edge\NativeMessagingHosts\{hostName}";

    public static bool IsRegistered =>
        Registry.CurrentUser.OpenSubKey(ChromeRegistryKey(HostName)) is not null ||
        Registry.CurrentUser.OpenSubKey(EdgeRegistryKey(HostName)) is not null;

    /// extensionId is the 32-character Chrome/Edge extension ID (Manage Extensions > Developer mode
    /// shows it under the extension's name) - the manifest's allowed_origins restricts which
    /// extension is permitted to invoke this host at all.
    public static void Register(string extensionId)
    {
        var exePath = Environment.ProcessPath ?? throw new InvalidOperationException("Could not resolve the running exe's path.");

        foreach (var (hostName, manifestFile) in Registrations)
        {
            var manifestPath = ManifestPath(manifestFile);

            var manifest = new
            {
                name = hostName,
                description = "Docket - folder-exists lookup against the Search Everywhere index",
                path = exePath,
                type = "stdio",
                allowed_origins = new[] { $"chrome-extension://{extensionId}/" },
            };

            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

            WriteRegistryPointer(ChromeRegistryKey(hostName), manifestPath);
            WriteRegistryPointer(EdgeRegistryKey(hostName), manifestPath);
        }
    }

    public static void Unregister()
    {
        foreach (var (hostName, manifestFile) in Registrations)
        {
            Registry.CurrentUser.DeleteSubKeyTree(ChromeRegistryKey(hostName), throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(EdgeRegistryKey(hostName), throwOnMissingSubKey: false);

            try
            {
                File.Delete(ManifestPath(manifestFile));
            }
            catch (IOException)
            {
                // Not fatal - an orphaned manifest file with no registry pointer to it is inert.
            }
        }
    }

    private static void WriteRegistryPointer(string keyPath, string manifestPath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        key.SetValue(null, manifestPath);
    }
}
