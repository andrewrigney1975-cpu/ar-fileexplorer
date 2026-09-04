using System.Text.Json;
using FileExplorer.Models;

namespace FileExplorer.Services;

/// Entry point for the elevated "--backup-run" instance: reads the serialized job, runs
/// BackupRunner, and returns its exit code. No WinUI - this path never creates a window.
public static class BackupHost
{
    public static int Run(string jobPath, string modeArg, string statusPath)
    {
        try
        {
            var job = JsonSerializer.Deserialize<BackupJob>(File.ReadAllText(jobPath))
                ?? throw new InvalidOperationException("Couldn't read the backup job file.");
            var mode = Enum.TryParse<BackupRunMode>(modeArg, ignoreCase: true, out var m) ? m : BackupRunMode.Auto;

            // The set folders (each with its set.json) are the source of truth for what exists; the
            // UI derives "last run / last full" from them and from the launcher result.
            return BackupRunner.RunAsync(job, mode, statusPath, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            try
            {
                File.AppendAllText(statusPath,
                    JsonSerializer.Serialize(new BackupProgress("Failed", ex.Message, 0, 0, true, true, ex.Message)) + Environment.NewLine);
            }
            catch (IOException)
            {
            }

            return 1;
        }
    }
}
