using System.Text.Json;
using System.Text.RegularExpressions;
using FileExplorer.Models;
using Microsoft.Win32;

namespace FileExplorer.Services;

/// Registers/unregisters this app as a Chrome/Edge Native Messaging host, scoped to a list of caller
/// extension IDs, so a browser extension can ask "does a folder named X already exist?" against the
/// Search Everywhere index (see NativeMessagingHostService for the actual query). Deliberately NOT
/// a local HTTP/WebSocket server - that would open a port any webpage's script could probe, not
/// just the intended extensions. Native Messaging never opens a network port at all: the browser
/// launches the host process directly, and only origins listed in the manifest's allowed_origins
/// are permitted to.
///
/// The registered extensions live in browser-extensions.json; the manifest files and registry
/// pointers are always regenerated from that list as a whole (one allowed_origins entry per
/// extension), so adding/removing one never disturbs the others.
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
    private const string StoreFileName = "browser-extensions.json";
    private const string OriginPrefix = "chrome-extension://";

    private static readonly (string HostName, string ManifestFile)[] Registrations =
    {
        (HostName, "native-messaging-host-manifest.json"),
        (LegacyHostName, "native-messaging-host-manifest.legacy.json"),
    };

    private static readonly JsonFileStore<List<BrowserExtensionRegistration>> Store = new(StoreFileName, () => new List<BrowserExtensionRegistration>());

    // Chrome/Edge extension IDs are always exactly 32 characters from a-p (a base-16 encoding of
    // a hash of the extension's public key, using letters instead of 0-9a-f).
    private static readonly Regex ExtensionIdPattern = new("^[a-p]{32}$", RegexOptions.Compiled);

    private static string AppDataPath(string fileName) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FileExplorerApp", fileName);

    private static string ChromeRegistryKey(string hostName) => $@"Software\Google\Chrome\NativeMessagingHosts\{hostName}";

    private static string EdgeRegistryKey(string hostName) => $@"Software\Microsoft\Edge\NativeMessagingHosts\{hostName}";

    public static bool IsRegistered =>
        Registry.CurrentUser.OpenSubKey(ChromeRegistryKey(HostName)) is not null ||
        Registry.CurrentUser.OpenSubKey(EdgeRegistryKey(HostName)) is not null;

    public static IReadOnlyList<BrowserExtensionRegistration> Extensions
    {
        get
        {
            MigrateFromManifestsIfNeeded();
            return Store.Load();
        }
    }

    /// Accepts either a bare ID or a pasted origin ("chrome-extension://<id>/"), and returns the
    /// bare lowercase ID - or null if it isn't a valid Chrome/Edge extension ID.
    public static string? NormalizeExtensionId(string input)
    {
        var id = input.Trim();
        if (id.StartsWith(OriginPrefix, StringComparison.OrdinalIgnoreCase))
        {
            id = id[OriginPrefix.Length..];
        }

        id = id.Trim('/').ToLowerInvariant();
        return ExtensionIdPattern.IsMatch(id) ? id : null;
    }

    /// extensionId is the 32-character Chrome/Edge extension ID (Manage Extensions > Developer mode
    /// shows it under the extension's name). Adding an ID that's already registered just updates
    /// its label. Either way every manifest is rewritten with the full list.
    public static void AddExtension(string extensionId, string? label)
    {
        var id = NormalizeExtensionId(extensionId) ?? throw new ArgumentException($"\"{extensionId}\" isn't a valid Chrome/Edge extension ID.", nameof(extensionId));
        label = string.IsNullOrWhiteSpace(label) ? null : label.Trim();

        var list = Extensions.ToList();
        var existing = list.FindIndex(e => e.Id == id);
        if (existing >= 0)
        {
            list[existing] = list[existing] with { Label = label ?? list[existing].Label };
        }
        else
        {
            list.Add(new BrowserExtensionRegistration(id, label));
        }

        Store.Save(list);
        WriteRegistration(list);
    }

    /// Removing the last extension unregisters the host entirely - a manifest with an empty
    /// allowed_origins would be inert anyway, but leaving registry keys behind would still make
    /// IsRegistered report true.
    public static void RemoveExtension(string extensionId)
    {
        var list = Extensions.Where(e => e.Id != extensionId).ToList();
        Store.Save(list);

        if (list.Count == 0)
        {
            DeleteRegistration();
        }
        else
        {
            WriteRegistration(list);
        }
    }

    /// Removes every registered extension and the host registration itself.
    public static void Unregister()
    {
        Store.Save(new List<BrowserExtensionRegistration>());
        DeleteRegistration();
    }

    /// Run at normal app startup: migrates a pre-multi-extension setup into the store, then
    /// rewrites the manifests from it so `path` always points at the exe actually being run (a new
    /// build, a moved install) rather than whichever build last clicked Register.
    public static void RefreshRegistration()
    {
        var list = Extensions;
        if (list.Count > 0)
        {
            WriteRegistration(list);
        }
    }

    /// Before the store existed, the only record of which extensions were allowed was the
    /// allowed_origins inside the manifest files themselves (one ID per Register click, or more if
    /// edited by hand). The first time the store is needed, seed it from every origin found in
    /// any existing manifest so an upgrade never silently drops an extension.
    private static void MigrateFromManifestsIfNeeded()
    {
        if (File.Exists(AppDataPath(StoreFileName)))
        {
            return;
        }

        var migrated = new List<BrowserExtensionRegistration>();

        foreach (var (_, manifestFile) in Registrations)
        {
            var manifestPath = AppDataPath(manifestFile);
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
                if (!document.RootElement.TryGetProperty("allowed_origins", out var origins) || origins.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var origin in origins.EnumerateArray())
                {
                    if (origin.GetString() is { } value
                        && NormalizeExtensionId(value) is { } id
                        && !migrated.Any(e => e.Id == id))
                    {
                        migrated.Add(new BrowserExtensionRegistration(id, null));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                LoggingService.LogWarning($"BrowserIntegrationService.MigrateFromManifestsIfNeeded: {manifestPath}", ex);
            }
        }

        // Saved even when empty, so this one-time scan doesn't repeat on every access.
        Store.Save(migrated);
    }

    private static void WriteRegistration(IReadOnlyList<BrowserExtensionRegistration> extensions)
    {
        var exePath = Environment.ProcessPath ?? throw new InvalidOperationException("Could not resolve the running exe's path.");
        var allowedOrigins = extensions.Select(e => $"{OriginPrefix}{e.Id}/").ToArray();

        foreach (var (hostName, manifestFile) in Registrations)
        {
            var manifestPath = AppDataPath(manifestFile);

            var manifest = new
            {
                name = hostName,
                description = "Docket - folder-exists lookup against the Search Everywhere index",
                path = exePath,
                type = "stdio",
                allowed_origins = allowedOrigins,
            };

            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

            WriteRegistryPointer(ChromeRegistryKey(hostName), manifestPath);
            WriteRegistryPointer(EdgeRegistryKey(hostName), manifestPath);
        }
    }

    private static void DeleteRegistration()
    {
        foreach (var (hostName, manifestFile) in Registrations)
        {
            Registry.CurrentUser.DeleteSubKeyTree(ChromeRegistryKey(hostName), throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(EdgeRegistryKey(hostName), throwOnMissingSubKey: false);

            try
            {
                File.Delete(AppDataPath(manifestFile));
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
