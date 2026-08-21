using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FileExplorer.Services;

public sealed record NetworkDriveResult(bool Success, string? ErrorMessage);

/// Maps/disconnects a Windows drive letter to a UNC path (mpr.dll's WNetAddConnection2W /
/// WNetCancelConnection2W - the same API "net use" and Explorer's own "Map Network Drive" wizard
/// use). This is a different thing from NetworkLocationService: that just pins a UNC path as a
/// bookmark in the left rail; this actually creates a lettered drive that shows up in
/// FileSystemService.GetReadyDrives() like any local disk, credentials and all.
public static class NetworkDriveService
{
    private const int ResourceTypeDisk = 0x1;
    private const int ConnectUpdateProfile = 0x1;
    private const int NoError = 0;

    public static NetworkDriveResult MapDrive(char driveLetter, string uncPath, string? username, string? password, bool reconnectAtSignIn)
    {
        var resource = new NETRESOURCEW
        {
            dwType = ResourceTypeDisk,
            lpLocalName = $"{char.ToUpperInvariant(driveLetter)}:",
            lpRemoteName = uncPath,
        };

        var flags = reconnectAtSignIn ? ConnectUpdateProfile : 0;

        var result = WNetAddConnection2W(
            ref resource,
            string.IsNullOrEmpty(password) ? null : password,
            string.IsNullOrEmpty(username) ? null : username,
            flags);

        return result == NoError
            ? new NetworkDriveResult(true, null)
            : new NetworkDriveResult(false, new Win32Exception(result).Message);
    }

    public static NetworkDriveResult DisconnectDrive(char driveLetter, bool force = false)
    {
        var result = WNetCancelConnection2W($"{char.ToUpperInvariant(driveLetter)}:", ConnectUpdateProfile, force);

        return result == NoError
            ? new NetworkDriveResult(true, null)
            : new NetworkDriveResult(false, new Win32Exception(result).Message);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NETRESOURCEW
    {
        public int dwScope;
        public int dwType;
        public int dwDisplayType;
        public int dwUsage;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpLocalName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpRemoteName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpComment;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpProvider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WNetAddConnection2W(ref NETRESOURCEW lpNetResource, string? lpPassword, string? lpUsername, int dwFlags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WNetCancelConnection2W(string lpName, int dwFlags, [MarshalAs(UnmanagedType.Bool)] bool fForce);
}
