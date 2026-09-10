using System.Runtime.InteropServices;

namespace FileExplorer.Services;

/// Empties the Windows Recycle Bin via the shell. Passing a null root path to SHEmptyRecycleBinW
/// tells the shell to empty the bins on every drive at once, so there's no need to enumerate drives
/// here. Runs with no confirmation/progress/sound UI - the caller owns the confirmation prompt.
public static class RecycleBinService
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBinW(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    private const uint SherbNoConfirmation = 0x00000001;
    private const uint SherbNoProgressUi = 0x00000002;
    private const uint SherbNoSound = 0x00000004;

    /// Empties every drive's Recycle Bin. Returns null on success or a message describing the failure.
    public static Task<string?> EmptyAllAsync()
        => Task.Run(() =>
        {
            try
            {
                // S_OK (0) = emptied; a non-zero HRESULT here is usually just "already empty" on one
                // of the drives (E_UNEXPECTED / -2147418113), which isn't a real failure.
                var hr = SHEmptyRecycleBinW(IntPtr.Zero, null,
                    SherbNoConfirmation | SherbNoProgressUi | SherbNoSound);

                return hr is 0 or unchecked((int)0x8000FFFF)
                    ? null
                    : Marshal.GetExceptionForHR(hr)?.Message ?? $"Shell returned HRESULT 0x{hr:X8}.";
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        });
}
