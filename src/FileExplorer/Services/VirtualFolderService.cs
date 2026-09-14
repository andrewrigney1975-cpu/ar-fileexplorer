using System.Security.Cryptography;
using System.Text;
using FileExplorer.Models;
using Microsoft.Data.Sqlite;

namespace FileExplorer.Services;

/// Virtual folders - named collections of real file/folder paths, stored in virtualfolders.db (next
/// to ratings.db/search-index.db). Shown in the left rail; navigated into via a
/// "virtualfolder://{id}" path (see [[VirtualFolderPathService]]) which [[FileSystemService]]
/// resolves by looking up this service's membership list and stat-ing each real path.
public static class VirtualFolderService
{
    private static readonly string DbDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileExplorerApp");

    private static string DbPath => Path.Combine(DbDirectory, "virtualfolders.db");

    private static readonly object Gate = new();
    private static List<VirtualFolder>? _folders;
    private static Dictionary<string, List<string>>? _members;
    private static byte[]? _pinHash;
    private static byte[]? _pinSalt;

    /// Raised whenever a virtual folder or its membership changes, so the rail and any open pane
    /// browsing into one can refresh.
    public static event EventHandler? Changed;

    public static List<VirtualFolder> List()
    {
        lock (Gate)
        {
            LoadLocked();
            return new List<VirtualFolder>(_folders!);
        }
    }

    public static VirtualFolder? Find(string id)
    {
        lock (Gate)
        {
            LoadLocked();
            return _folders!.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));
        }
    }

    public static VirtualFolder Create(string name, bool isPrivate = false)
    {
        var folder = new VirtualFolder(Guid.NewGuid().ToString("N"), name, DateTimeOffset.UtcNow, isPrivate);

        lock (Gate)
        {
            LoadLocked();
            _folders!.Add(folder);
            _members![folder.Id] = new List<string>();
        }

        ExecuteWrite(
            "INSERT INTO VirtualFolders (Id, Name, Created, IsPrivate) VALUES (@id, @name, @created, @priv)",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@id", folder.Id);
                cmd.Parameters.AddWithValue("@name", folder.Name);
                cmd.Parameters.AddWithValue("@created", folder.Created.UtcTicks);
                cmd.Parameters.AddWithValue("@priv", folder.IsPrivate ? 1 : 0);
            });

        Changed?.Invoke(null, EventArgs.Empty);
        return folder;
    }

    /// A PIN has been set for the "Private Virtual Folders" rail section (see VerifyPin) - once
    /// true it stays true; there is no "forgot PIN" recovery beyond deleting virtualfolders.db.
    public static bool HasPin()
    {
        lock (Gate)
        {
            LoadLocked();
            return _pinHash is not null;
        }
    }

    public static void SetPin(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = HashPin(pin, salt);

        lock (Gate)
        {
            _pinHash = hash;
            _pinSalt = salt;
        }

        ExecuteWrite(
            "INSERT INTO Settings (Key, Value) VALUES ('PinSalt', @s) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value",
            cmd => cmd.Parameters.AddWithValue("@s", Convert.ToHexString(salt)));
        ExecuteWrite(
            "INSERT INTO Settings (Key, Value) VALUES ('PinHash', @h) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value",
            cmd => cmd.Parameters.AddWithValue("@h", Convert.ToHexString(hash)));
    }

    public static bool VerifyPin(string pin)
    {
        lock (Gate)
        {
            LoadLocked();
            if (_pinHash is null || _pinSalt is null)
            {
                return false;
            }

            return CryptographicOperations.FixedTimeEquals(HashPin(pin, _pinSalt), _pinHash);
        }
    }

    private static byte[] HashPin(string pin, byte[] salt) =>
        SHA256.HashData(salt.Concat(Encoding.UTF8.GetBytes(pin)).ToArray());

    public static void Rename(string id, string name)
    {
        lock (Gate)
        {
            LoadLocked();
            var index = _folders!.FindIndex(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return;
            }

            _folders[index] = _folders[index] with { Name = name };
        }

        ExecuteWrite(
            "UPDATE VirtualFolders SET Name = @name WHERE Id = @id",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@id", id);
                cmd.Parameters.AddWithValue("@name", name);
            });

        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void Delete(string id)
    {
        lock (Gate)
        {
            LoadLocked();
            _folders!.RemoveAll(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));
            _members!.Remove(id);
        }

        ExecuteWrite(
            "DELETE FROM VirtualFolderMembers WHERE VirtualFolderId = @id; DELETE FROM VirtualFolders WHERE Id = @id",
            cmd => cmd.Parameters.AddWithValue("@id", id));

        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// Member paths in the order they were added; a path missing from disk is left in the list
    /// (FileSystemService callers decide how to represent it) rather than silently dropped, so it
    /// can reappear if the drive/share comes back online.
    public static List<string> GetMembers(string id)
    {
        lock (Gate)
        {
            LoadLocked();
            return _members!.TryGetValue(id, out var list) ? new List<string>(list) : new List<string>();
        }
    }

    public static void AddMembers(string id, IEnumerable<string> paths)
    {
        var added = new List<string>();

        lock (Gate)
        {
            LoadLocked();
            if (!_members!.TryGetValue(id, out var list))
            {
                return;
            }

            foreach (var path in paths)
            {
                if (!list.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
                {
                    list.Add(path);
                    added.Add(path);
                }
            }
        }

        if (added.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow.UtcTicks;
        ExecuteWriteMany(added, (cmd, path) =>
        {
            cmd.CommandText =
                "INSERT INTO VirtualFolderMembers (VirtualFolderId, Path, AddedAt) VALUES (@id, @path, @added) " +
                "ON CONFLICT(VirtualFolderId, Path) DO NOTHING";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@path", path);
            cmd.Parameters.AddWithValue("@added", now);
        });

        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void RemoveMember(string id, string path)
    {
        lock (Gate)
        {
            LoadLocked();
            if (_members!.TryGetValue(id, out var list))
            {
                list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            }
        }

        ExecuteWrite(
            "DELETE FROM VirtualFolderMembers WHERE VirtualFolderId = @id AND Path = @path",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@id", id);
                cmd.Parameters.AddWithValue("@path", path);
            });

        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static void LoadLocked()
    {
        if (_folders is not null && _members is not null)
        {
            return;
        }

        _folders = new List<VirtualFolder>();
        _members = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        try
        {
            Directory.CreateDirectory(DbDirectory);
            using var connection = OpenConnection();

            using (var folders = connection.CreateCommand())
            {
                folders.CommandText = "SELECT Id, Name, Created, IsPrivate FROM VirtualFolders";
                using var reader = folders.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetString(0);
                    _folders.Add(new VirtualFolder(id, reader.GetString(1), new DateTimeOffset(reader.GetInt64(2), TimeSpan.Zero), reader.GetInt64(3) != 0));
                    _members[id] = new List<string>();
                }
            }

            using (var members = connection.CreateCommand())
            {
                members.CommandText = "SELECT VirtualFolderId, Path FROM VirtualFolderMembers ORDER BY AddedAt";
                using var reader = members.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetString(0);
                    if (_members.TryGetValue(id, out var list))
                    {
                        list.Add(reader.GetString(1));
                    }
                }
            }

            using (var settings = connection.CreateCommand())
            {
                settings.CommandText = "SELECT Key, Value FROM Settings WHERE Key IN ('PinHash', 'PinSalt')";
                using var reader = settings.ExecuteReader();
                while (reader.Read())
                {
                    var value = Convert.FromHexString(reader.GetString(1));
                    if (string.Equals(reader.GetString(0), "PinHash", StringComparison.Ordinal))
                    {
                        _pinHash = value;
                    }
                    else
                    {
                        _pinSalt = value;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            LoggingService.LogWarning("VirtualFolderService.Load", ex);
        }
    }

    private static SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data Source={DbPath}");
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=15000;";
        pragma.ExecuteNonQuery();

        using var schema = connection.CreateCommand();
        schema.CommandText =
            "CREATE TABLE IF NOT EXISTS VirtualFolders (Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Created INTEGER NOT NULL, IsPrivate INTEGER NOT NULL DEFAULT 0);" +
            "CREATE TABLE IF NOT EXISTS Settings (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);" +
            "CREATE TABLE IF NOT EXISTS VirtualFolderMembers (VirtualFolderId TEXT NOT NULL, Path TEXT NOT NULL, AddedAt INTEGER NOT NULL, " +
            "PRIMARY KEY (VirtualFolderId, Path));";
        schema.ExecuteNonQuery();

        return connection;
    }

    private static void ExecuteWrite(string sql, Action<SqliteCommand> bind)
    {
        try
        {
            Directory.CreateDirectory(DbDirectory);
            using var connection = OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            bind(cmd);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            LoggingService.LogWarning("VirtualFolderService.ExecuteWrite", ex);
        }
    }

    private static void ExecuteWriteMany<T>(IEnumerable<T> values, Action<SqliteCommand, T> bindEach)
    {
        try
        {
            Directory.CreateDirectory(DbDirectory);
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();

            foreach (var value in values)
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;
                bindEach(cmd, value);
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            LoggingService.LogWarning("VirtualFolderService.ExecuteWriteMany", ex);
        }
    }
}
