using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using FileExplorer.Models;

namespace FileExplorer.Services;

/// UI-side: launches an elevated instance of the app to run a backup job (VSS + backup-mode reads
/// need admin), then tails its status file so the caller can show progress.
public static class ElevatedBackupLauncher
{
    private const int SW_HIDE = 0;
    private const uint SEE_MASK_NOCLOSEPROCESS = 0x00000040;
    private const uint SEE_MASK_NO_CONSOLE = 0x00008000;
    private const int ERROR_CANCELLED = 1223;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string lpVerb;
        public string lpFile;
        public string lpParameters;
        public string lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    public sealed record Result(bool Started, bool DeclinedElevation, int ExitCode, BackupProgress? Final);

    /// The command-line tail an elevated instance is launched with. Parsed by App on startup.
    public const string ArgSwitch = "--backup-run";

    public static async Task<Result> RunAsync(BackupJob job, BackupRunMode mode, Action<BackupProgress> onProgress, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "docket-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var jobPath = Path.Combine(dir, "job.json");
        var statusPath = Path.Combine(dir, "status.jsonl");

        await File.WriteAllTextAsync(jobPath, JsonSerializer.Serialize(job), ct);
        await File.WriteAllTextAsync(statusPath, string.Empty, ct);

        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Couldn't resolve the running exe.");
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = SEE_MASK_NOCLOSEPROCESS | SEE_MASK_NO_CONSOLE,
            lpVerb = "runas",
            lpFile = exe,
            lpParameters = $"{ArgSwitch} \"{jobPath}\" {mode} \"{statusPath}\"",
            nShow = SW_HIDE,
        };

        if (!ShellExecuteEx(ref info))
        {
            var err = Marshal.GetLastWin32Error();
            TryCleanup(dir);
            return new Result(false, err == ERROR_CANCELLED, -1, null);
        }

        BackupProgress? final = null;
        var seenLines = 0;

        void Drain()
        {
            var (fresh, total) = ReadNew(statusPath, seenLines);
            seenLines = total;
            foreach (var progress in fresh)
            {
                onProgress(progress);
                if (progress.Finished)
                {
                    final = progress;
                }
            }
        }

        try
        {
            while (true)
            {
                await Task.Delay(400, ct);
                Drain();

                if (WaitForSingleObject(info.hProcess, 0) == 0) // WAIT_OBJECT_0 - process exited
                {
                    Drain();
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The elevated process keeps running to a safe stopping point; we just stop tailing.
        }

        GetExitCodeProcess(info.hProcess, out var exitCode);
        CloseHandle(info.hProcess);
        TryCleanup(dir);

        return new Result(true, false, (int)exitCode, final);
    }

    private static (List<BackupProgress> New, int TotalLines) ReadNew(string statusPath, int seenLines)
    {
        var result = new List<BackupProgress>();
        string[] lines;
        try
        {
            using var stream = new FileStream(statusPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (IOException)
        {
            return (result, seenLines);
        }

        for (var i = seenLines; i < lines.Length; i++)
        {
            try
            {
                if (JsonSerializer.Deserialize<BackupProgress>(lines[i].Trim()) is { } parsed)
                {
                    result.Add(parsed);
                }
            }
            catch (JsonException)
            {
            }
        }

        return (result, lines.Length);
    }

    private static void TryCleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }
}
