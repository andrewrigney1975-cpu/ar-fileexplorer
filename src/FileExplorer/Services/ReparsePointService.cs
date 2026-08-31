using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FileExplorer.Services;

public enum ReparsePointKind
{
    None,
    SymbolicLink,
    Junction,
}

public sealed record LinkCreationResult(bool Success, string? ErrorMessage);

/// Detects and creates NTFS reparse points (symbolic links and junctions). .NET has native support
/// for creating/reading symbolic links (Directory/File.CreateSymbolicLink, .LinkTarget) but no way
/// to tell a symbolic link apart from a junction, or to create a junction at all - both gaps are
/// filled here: tag detection via a minimal FindFirstFileW P/Invoke (the reparse tag rides along in
/// WIN32_FIND_DATA.dwReserved0 whenever FILE_ATTRIBUTE_REPARSE_POINT is set, so reading it needs no
/// open handle or DeviceIoControl call), and junction creation by shelling out to the OS's own
/// `mklink /J` (hand-rolling the FSCTL_SET_REPARSE_POINT buffer layout is a well-known way to get
/// this subtly wrong; the built-in tool is guaranteed correct).
public static class ReparsePointService
{
    private const uint FileAttributeReparsePoint = 0x400;
    private const uint IoReparseTagSymlink = 0xA000000C;
    private const uint IoReparseTagMountPoint = 0xA0000003;

    public static ReparsePointKind GetKind(string path)
    {
        try
        {
            var handle = FindFirstFileW(path, out var data);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            {
                return ReparsePointKind.None;
            }

            FindClose(handle);

            if ((data.dwFileAttributes & FileAttributeReparsePoint) == 0)
            {
                return ReparsePointKind.None;
            }

            return data.dwReserved0 switch
            {
                IoReparseTagMountPoint => ReparsePointKind.Junction,
                IoReparseTagSymlink => ReparsePointKind.SymbolicLink,
                // An unrecognized reparse tag (e.g. a cloud-placeholder or app-specific one) still
                // gets treated as a link rather than silently hidden from the rest of the app.
                _ => ReparsePointKind.SymbolicLink,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ReparsePointKind.None;
        }
    }

    /// The link's immediate target (not resolved through a chain of multiple links), or null if
    /// path isn't a link or the target can't be read. Works for both symbolic links and junctions.
    public static string? TryGetLinkTarget(string path)
    {
        try
        {
            FileSystemInfo? info = Directory.Exists(path) ? new DirectoryInfo(path)
                : File.Exists(path) ? new FileInfo(path)
                : null;
            return info?.LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static LinkCreationResult CreateSymbolicLink(string linkPath, string targetPath, bool targetIsDirectory)
    {
        try
        {
            if (targetIsDirectory)
            {
                Directory.CreateSymbolicLink(linkPath, targetPath);
            }
            else
            {
                File.CreateSymbolicLink(linkPath, targetPath);
            }

            return new LinkCreationResult(true, null);
        }
        catch (UnauthorizedAccessException)
        {
            return new LinkCreationResult(false,
                "Creating symbolic links needs either Developer Mode turned on (Settings > Privacy & security > For developers) or running Docket as Administrator.");
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            return new LinkCreationResult(false, ex.Message);
        }
    }

    /// Junctions never need elevation or Developer Mode (unlike symbolic links) - one of the main
    /// reasons to offer them as a distinct option rather than just always creating symbolic links.
    public static async Task<LinkCreationResult> CreateJunctionAsync(string linkPath, string targetDirectoryPath)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetDirectoryPath}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return new LinkCreationResult(false, "Could not start mklink.");
            }

            var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);

            return process.ExitCode == 0
                ? new LinkCreationResult(true, null)
                : new LinkCreationResult(false, string.IsNullOrWhiteSpace(error) ? "mklink failed." : error.Trim());
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            return new LinkCreationResult(false, ex.Message);
        }
    }

    /// Recreates a link at destination matching source's own kind (a junction stays a junction, a
    /// symbolic link stays a symbolic link) pointing at the same resolved target - used when
    /// copying/moving a link itself rather than descending into whatever it points to.
    public static async Task<LinkCreationResult> RecreateLinkAsync(string sourceLinkPath, string destinationPath)
    {
        var target = TryGetLinkTarget(sourceLinkPath);
        if (target is null)
        {
            return new LinkCreationResult(false, "Could not read the link's target.");
        }

        var kind = GetKind(sourceLinkPath);
        var isDirectory = Directory.Exists(sourceLinkPath);

        return kind == ReparsePointKind.Junction
            ? await CreateJunctionAsync(destinationPath, target).ConfigureAwait(false)
            : CreateSymbolicLink(destinationPath, target, isDirectory);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstFileW(string lpFileName, out WIN32_FIND_DATA lpFindFileData);

    [DllImport("kernel32.dll")]
    private static extern bool FindClose(IntPtr hFindFile);

    // Deliberately laid out with explicit paired uint fields for the FILETIME members rather than a
    // single 8-byte `long` each - the latter gets 8-byte-aligned by the marshaler, inserting 4 bytes
    // of padding the real Win32 struct doesn't have and silently shifting every field after it
    // (verified empirically: cFileName came back missing its first two characters until fixed).
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_DATA
    {
        public uint dwFileAttributes;
        public uint ftCreationTime1, ftCreationTime2;
        public uint ftLastAccessTime1, ftLastAccessTime2;
        public uint ftLastWriteTime1, ftLastWriteTime2;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
    }
}
