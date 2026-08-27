using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FileExplorer.Services;

/// Runs instead of the normal WinUI window when this exe is launched by a browser as a Chrome/Edge
/// Native Messaging host (see BrowserIntegrationService for registration). Answers a single kind of
/// request - "does a folder with this exact name exist?" - by querying the Search Everywhere SQLite
/// index directly and read-only. Deliberately narrow scope: exact name match, folders only, no
/// general filesystem access, no writes.
///
/// Detected via DetectLaunch rather than a manifest launch argument, because Chrome's native
/// messaging manifest has no field for custom arguments - Chrome/Edge instead always append the
/// calling extension's origin (e.g. "chrome-extension://&lt;id&gt;/") as a command-line argument when
/// they launch the host, which is what DetectLaunch actually looks for.
public static class NativeMessagingHostService
{
    // Generous for a request this small ({"folderName":"..."}) - guards against a corrupt/hostile
    // length prefix making ReadMessage allocate or read an enormous buffer.
    private const int MaxMessageBytes = 1024 * 1024;

    public static bool DetectLaunch(string[] args) =>
        args.Any(a => a.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase) ||
                      a.StartsWith("moz-extension://", StringComparison.OrdinalIgnoreCase));

    public static void Run()
    {
        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();

        // Loops so a persistent chrome.runtime.connectNative port can send more than one message
        // over the same host process - a one-shot chrome.runtime.sendNativeMessage call just closes
        // stdin after its single message, which ReadMessage sees as EOF and returns from cleanly.
        while (true)
        {
            var requestJson = ReadMessage(stdin);
            if (requestJson is null)
            {
                return;
            }

            WriteMessage(stdout, HandleRequest(requestJson));
        }
    }

    private static string HandleRequest(string requestJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(requestJson);
            if (!doc.RootElement.TryGetProperty("folderName", out var nameProp) || nameProp.ValueKind != JsonValueKind.String)
            {
                return JsonSerializer.Serialize(new { error = "Request must be a JSON object with a string \"folderName\" property." });
            }

            var matches = FindFolders(nameProp.GetString()!);
            return JsonSerializer.Serialize(new { exists = matches.Count > 0, matches });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    private static List<string> FindFolders(string folderName)
    {
        var matches = new List<string>();

        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FileExplorerApp", "search-index.db");

        if (string.IsNullOrWhiteSpace(folderName) || !File.Exists(dbPath))
        {
            // Empty result either way: no query text, or the index doesn't exist yet (Search
            // Everywhere never enabled/no roots added) - "not found" is the honest answer for both,
            // not an error.
            return matches;
        }

        // Read-only: this process only ever queries. Mode=ReadOnly also means a missing/corrupt
        // file fails to open (caught below, empty result) instead of Microsoft.Data.Sqlite silently
        // creating a stray empty database, which the default read-write mode would do.
        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Path FROM Entries WHERE IsDirectory = 1 AND Name = @name COLLATE NOCASE LIMIT 50";
        cmd.Parameters.AddWithValue("@name", folderName);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            matches.Add(reader.GetString(0));
        }

        return matches;
    }

    // ----- Native messaging stdio framing: 4-byte little-endian length prefix + UTF-8 JSON, same
    // shape in both directions. -----

    private static string? ReadMessage(Stream stdin)
    {
        var lengthBytes = new byte[4];
        if (!ReadExact(stdin, lengthBytes))
        {
            return null;
        }

        var length = BitConverter.ToUInt32(lengthBytes, 0);
        if (length == 0 || length > MaxMessageBytes)
        {
            return null;
        }

        var buffer = new byte[length];
        return ReadExact(stdin, buffer) ? Encoding.UTF8.GetString(buffer) : null;
    }

    private static bool ReadExact(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
            {
                return false;
            }
            offset += read;
        }
        return true;
    }

    private static void WriteMessage(Stream stdout, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        stdout.Write(BitConverter.GetBytes((uint)bytes.Length), 0, 4);
        stdout.Write(bytes, 0, bytes.Length);
        stdout.Flush();
    }
}
