using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace FileExplorer.Services;

/// Raises <see cref="Changed"/> when a volume arrives or goes away (USB stick plugged/unplugged,
/// card-reader media inserted/ejected, network drive mapped/disconnected). Windows broadcasts
/// WM_DEVICECHANGE with DBT_DEVTYP_VOLUME to every top-level window without any
/// RegisterDeviceNotification call, so this just subclasses the main window's HWND to see it.
/// Changed fires on the UI thread, debounced - a multi-partition stick sends one message per volume.
public static class DriveChangeService
{
    private const uint WmDeviceChange = 0x0219;
    private const int DbtDeviceArrival = 0x8000;
    private const int DbtDeviceRemoveComplete = 0x8004;
    private const int DbtDevTypVolume = 0x00000002;
    private const nuint SubclassId = 0x44524956; // "DRIV"

    private delegate nint SubclassProc(nint hWnd, uint msg, nint wParam, nint lParam, nuint idSubclass, nuint refData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(nint hWnd, SubclassProc proc, nuint idSubclass, nuint refData);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hWnd, uint msg, nint wParam, nint lParam);

    // Held in a static field so the GC can't collect the delegate while native code still calls it.
    private static SubclassProc? _proc;
    private static DispatcherQueueTimer? _debounce;

    public static event EventHandler? Changed;

    public static void Start(nint hwnd, DispatcherQueue dispatcherQueue)
    {
        if (_proc is not null)
        {
            return;
        }

        _debounce = dispatcherQueue.CreateTimer();
        _debounce.Interval = TimeSpan.FromMilliseconds(750);
        _debounce.IsRepeating = false;
        _debounce.Tick += (_, _) => Changed?.Invoke(null, EventArgs.Empty);

        _proc = WindowProc;
        if (!SetWindowSubclass(hwnd, _proc, SubclassId, 0))
        {
            LoggingService.LogWarning("DriveChangeService.Start", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }
    }

    private static nint WindowProc(nint hWnd, uint msg, nint wParam, nint lParam, nuint idSubclass, nuint refData)
    {
        if (msg == WmDeviceChange
            && (wParam == DbtDeviceArrival || wParam == DbtDeviceRemoveComplete)
            && lParam != 0
            && Marshal.ReadInt32(lParam, 4) == DbtDevTypVolume) // DEV_BROADCAST_HDR.dbch_devicetype
        {
            // Restart rather than start, so a burst of messages collapses into one refresh.
            _debounce?.Stop();
            _debounce?.Start();
        }

        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }
}
