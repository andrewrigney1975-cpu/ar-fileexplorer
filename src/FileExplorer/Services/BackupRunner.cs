using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FileExplorer.Models;

namespace FileExplorer.Services;

/// Executes one backup run: optional VSS snapshot -> robocopy mirror into a new set folder
/// (differential sets are hard-link-seeded from the latest full so unchanged files cost no space)
/// -> manifest -> snapshot cleanup -> retention prune. Progress is appended as JSON lines to a
/// status file the UI tails.
///
/// Runs in an elevated instance of the app (App detects "--backup-run"); VSS and backup-mode file
/// reads both need admin. A non-VSS job against a folder the user can already read would work
/// unelevated too, but the launcher elevates unconditionally for simplicity.
public static class BackupRunner
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    public static async Task<int> RunAsync(BackupJob job, BackupRunMode requestedMode, string statusPath, CancellationToken ct)
    {
        void Report(string phase, string detail, long files = 0, long bytes = 0) =>
            Append(statusPath, new BackupProgress(phase, detail, files, bytes, false, false, null));

        string? exposedDrive = null;

        try
        {
            var mode = BackupService.ResolveMode(job, requestedMode, DateTimeOffset.UtcNow);
            Report("Preparing", $"{mode} backup of {job.SourceRoot}");

            var jobDir = BackupService.JobDirectory(job);
            Directory.CreateDirectory(jobDir);

            var now = DateTimeOffset.UtcNow;
            var setType = mode == BackupRunMode.Full ? BackupSetType.Full : BackupSetType.Differential;
            var setFolder = Path.Combine(jobDir, BackupService.NewSetFolderName(setType, now));
            Directory.CreateDirectory(setFolder);

            BackupSet? baseFull = setType == BackupSetType.Differential ? BackupService.LatestCompletedFull(job) : null;
            if (setType == BackupSetType.Differential && baseFull is null)
            {
                Report("Preparing", "No full set to base a differential on - promoting to a full backup");
                setType = BackupSetType.Full;
            }

            // Where we read from.
            string sourcePath = job.SourceRoot;
            if (job.UseVolumeShadowCopy)
            {
                Report("Snapshot", "Creating a Volume Shadow Copy...");
                var volume = Path.GetPathRoot(Path.GetFullPath(job.SourceRoot))
                    ?? throw new InvalidOperationException("Couldn't resolve the source volume.");
                exposedDrive = PickFreeDriveLetter();
                await CreateShadowAsync(volume, exposedDrive, ct);

                var relative = Path.GetRelativePath(volume, Path.GetFullPath(job.SourceRoot));
                sourcePath = relative is "." or "" ? exposedDrive + "\\" : Path.Combine(exposedDrive + "\\", relative);
            }

            if (setType == BackupSetType.Differential && baseFull is not null)
            {
                Report("Linking", $"Hard-linking unchanged files from {baseFull.FolderName}...");
                var linked = HardLinkClone(baseFull.FolderPath, setFolder, ct);
                Report("Linking", $"{linked:N0} files linked");

                // robocopy overwrites a changed file in place, which would write straight through the
                // shared inode into the base full set. Unlink every seeded file whose source now
                // differs (size or write time) so robocopy lays down a brand-new file instead.
                var unlinked = BreakChangedLinks(sourcePath, setFolder, ct);
                Report("Linking", $"{unlinked:N0} changed file(s) unlinked for a fresh copy");
            }

            Report("Copying", "Running robocopy...");
            var (roboExit, files, bytes, failedFiles) = await RunRobocopyAsync(sourcePath, setFolder, job, statusPath, ct);

            var completed = roboExit < 8;
            BackupService.WriteManifest(setFolder, new BackupSetManifest(
                setType, now, job.SourceRoot,
                setType == BackupSetType.Differential ? baseFull?.FolderName : null,
                completed ? DateTimeOffset.UtcNow : null,
                files, bytes));

            if (job.UseVolumeShadowCopy && exposedDrive is not null)
            {
                Report("Snapshot", "Removing the shadow copy...");
                await DeleteShadowAsync(exposedDrive, ct);
                exposedDrive = null;
            }

            if (completed)
            {
                Report("Retention", "Pruning old sets...");
                foreach (var stale in BackupService.SetsToPrune(job))
                {
                    TryDeleteTree(stale);
                }
            }

            var summary = completed
                ? $"{setType} backup finished: {files:N0} files, {FormatBytes(bytes)}" +
                  (failedFiles > 0 ? $" ({failedFiles} file(s) could not be read)" : string.Empty)
                : $"robocopy reported a fatal error (exit {roboExit}); this set is incomplete";

            Append(statusPath, new BackupProgress("Done", summary, files, bytes, true, !completed, completed ? null : summary));
            return completed ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            Append(statusPath, new BackupProgress("Cancelled", "Backup cancelled", 0, 0, true, true, "Cancelled"));
            return 2;
        }
        catch (Exception ex)
        {
            LoggingService.LogWarning("BackupRunner.RunAsync", ex);
            Append(statusPath, new BackupProgress("Failed", ex.Message, 0, 0, true, true, ex.Message));
            return 1;
        }
        finally
        {
            if (exposedDrive is not null)
            {
                try { await DeleteShadowAsync(exposedDrive, CancellationToken.None); } catch { /* best effort */ }
            }
        }
    }

    // ----- VSS via diskshadow.exe (built into Windows) -----

    private static async Task CreateShadowAsync(string volume, string exposeAs, CancellationToken ct)
    {
        var script = $"""
            set context persistent nowriters
            set verbose on
            begin backup
            add volume {volume.TrimEnd('\\')} alias BkSrc
            create
            expose %BkSrc% {exposeAs}
            end backup
            """;

        var (exit, output) = await RunDiskShadowAsync(script, ct);
        if (exit != 0 || !Directory.Exists(exposeAs + "\\"))
        {
            throw new InvalidOperationException($"Volume Shadow Copy creation failed (diskshadow exit {exit}).\n{output}");
        }
    }

    private static async Task DeleteShadowAsync(string exposedDrive, CancellationToken ct) =>
        await RunDiskShadowAsync($"delete shadows exposed {exposedDrive}", ct);

    private static async Task<(int Exit, string Output)> RunDiskShadowAsync(string script, CancellationToken ct)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"docket-vss-{Guid.NewGuid():N}.dsh");
        await File.WriteAllTextAsync(scriptPath, script, ct);
        try
        {
            return await RunProcessAsync("diskshadow.exe", $"/s \"{scriptPath}\"", null, ct);
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { /* best effort */ }
        }
    }

    // ----- robocopy -----

    private static async Task<(int Exit, long Files, long Bytes, long Failed)> RunRobocopyAsync(
        string source, string dest, BackupJob job, string statusPath, CancellationToken ct)
    {
        var excludes = new List<string> { "\"System Volume Information\"", "\"$RECYCLE.BIN\"", "\"$Recycle.Bin\"" };
        excludes.AddRange(job.ExcludeDirectories.Where(d => d.Length > 0).Select(d => $"\"{d}\""));
        // Never recurse the destination into itself if it lives on the source volume.
        excludes.Add($"\"{dest}\"");

        // /COPYALL (= /COPY:DATSOU) needs SeSecurityPrivilege/SeRestorePrivilege for the audit + owner
        // parts - fine in the elevated backup process, not in an unelevated one.
        var copyFlags = IsElevated() ? "/COPYALL /ZB" : "/COPY:DAT";

        var args = new StringBuilder();
        args.Append($"\"{source.TrimEnd('\\')}\" \"{dest.TrimEnd('\\')}\" ");
        args.Append($"/MIR {copyFlags} /DCOPY:DAT /SL /XJD /XJF /R:1 /W:1 /MT:16 /BYTES /NP /NDL /NC ");
        args.Append("/XF pagefile.sys hiberfil.sys swapfile.sys ");
        args.Append("/XD " + string.Join(" ", excludes));

        long files = 0, bytes = 0, failed = 0;
        var lastReport = DateTime.MinValue;

        var (exit, output) = await RunProcessAsync("robocopy.exe", args.ToString(), line =>
        {
            // robocopy prints "<tab><size><tab><path>" for each copied file. Locale-proof enough:
            // count lines that end in a path and carry a leading size token.
            var m = Regex.Match(line, @"^\s*(\d+)\s+(.+)$");
            if (m.Success && (m.Groups[2].Value.Contains('\\') || m.Groups[2].Value.Contains(':')))
            {
                Interlocked.Increment(ref files);
                Interlocked.Add(ref bytes, long.TryParse(m.Groups[1].Value, out var b) ? b : 0);
            }

            if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 750)
            {
                lastReport = DateTime.UtcNow;
                Append(statusPath, new BackupProgress("Copying", line.Trim(), Interlocked.Read(ref files), Interlocked.Read(ref bytes), false, false, null));
            }
        }, ct);

        // Final summary block: "Files : total copied skipped mismatch FAILED extras"
        var summary = Regex.Match(output, @"(?im)^\s*\S+\s*:\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s*$");
        foreach (Match line in Regex.Matches(output, @"(?im)^\s*\S+\s*:\s+\d+\s+\d+\s+\d+\s+\d+\s+(\d+)\s+\d+\s*$"))
        {
            failed += long.TryParse(line.Groups[1].Value, out var f) ? f : 0;
        }

        return (exit, files, bytes, failed);
    }

    // ----- helpers -----

    private static long HardLinkClone(string sourceTree, string destTree, CancellationToken ct)
    {
        long count = 0;
        foreach (var dir in Directory.EnumerateDirectories(sourceTree, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(dir.Replace(sourceTree, destTree));
        }

        foreach (var file in Directory.EnumerateFiles(sourceTree, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if (Path.GetFileName(file).Equals("set.json", StringComparison.OrdinalIgnoreCase) &&
                Path.GetDirectoryName(file)!.Equals(sourceTree, StringComparison.OrdinalIgnoreCase))
            {
                continue; // don't carry the base full's manifest into the diff set
            }

            var target = file.Replace(sourceTree, destTree);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (CreateHardLinkW(target, file, IntPtr.Zero) || TryCopy(file, target))
            {
                count++;
            }
        }

        return count;
    }

    /// Walks the hard-link-seeded diff set and deletes any file whose source counterpart is gone or
    /// has changed (size or last-write-time). Deleting one hard link never touches the other, so the
    /// base full set keeps the old content; robocopy then copies the changed files fresh.
    private static long BreakChangedLinks(string sourceTree, string destTree, CancellationToken ct)
    {
        long unlinked = 0;
        foreach (var target in Directory.EnumerateFiles(destTree, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if (Path.GetFileName(target).Equals("set.json", StringComparison.OrdinalIgnoreCase) &&
                Path.GetDirectoryName(target)!.Equals(destTree, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var source = target.Replace(destTree, sourceTree);
            var t = new FileInfo(target);
            var s = new FileInfo(source);

            var changed = !s.Exists
                || s.Length != t.Length
                || Math.Abs((s.LastWriteTimeUtc - t.LastWriteTimeUtc).TotalSeconds) > 2;

            if (changed)
            {
                try { t.Delete(); unlinked++; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* robocopy will retry the overwrite */ }
            }
        }

        return unlinked;
    }

    private static bool TryCopy(string source, string dest)
    {
        try
        {
            File.Copy(source, dest, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string PickFreeDriveLetter()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        for (var c = 'Z'; c >= 'F'; c--)
        {
            if (!used.Contains(c))
            {
                return c + ":";
            }
        }

        throw new InvalidOperationException("No free drive letter to expose the shadow copy.");
    }

    private static async Task<(int Exit, string Output)> RunProcessAsync(string fileName, string arguments, Action<string>? onLine, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            },
        };

        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            output.AppendLine(e.Data);
            onLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct);
        return (process.ExitCode, output.ToString());
    }

    private static readonly object AppendLock = new();

    private static void Append(string statusPath, BackupProgress progress)
    {
        try
        {
            lock (AppendLock)
            {
                File.AppendAllText(statusPath, JsonSerializer.Serialize(progress) + Environment.NewLine);
            }
        }
        catch (IOException)
        {
            // best effort - a missed progress line doesn't matter
        }
    }

    private static void TryDeleteTree(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoggingService.LogWarning($"BackupRunner.TryDeleteTree: {path}", ex);
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:0.#} {units[unit]}";
    }
}
