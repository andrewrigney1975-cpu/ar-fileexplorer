using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;
using FileExplorer.Models;
using Microsoft.Win32.SafeHandles;

namespace FileExplorer.Services;

/// Sequential/random read/write throughput benchmark, CrystalDiskMark-style. Uses unbuffered I/O
/// (FILE_FLAG_NO_BUFFERING) so results reflect real disk speed instead of Windows' file cache -
/// falls back to ordinary buffered FileStream I/O if that flag is ever rejected for a given drive.
public static class DiskBenchmarkService
{
    private const int SectorSize = 4096;
    private const int SequentialChunkSize = 1024 * 1024;
    private const int RandomBlockSize = SectorSize;
    private const int RandomIterations = 512;

    private static readonly (string Label, long Bytes)[] Sizes =
    {
        ("4 MB", 4L * 1024 * 1024),
        ("64 MB", 64L * 1024 * 1024),
        ("1 GB", 1024L * 1024 * 1024),
    };

    public static async Task RunBenchmarkAsync(string driveRoot, IProgress<BenchmarkResult> progress, CancellationToken token)
    {
        var testFolder = ResolveWritableTestFolder(driveRoot);

        foreach (var (label, sizeBytes) in Sizes)
        {
            token.ThrowIfCancellationRequested();
            var testFile = Path.Combine(testFolder, $".arexx-benchmark-{Guid.NewGuid():N}.tmp");

            try
            {
                var seqWrite = await Task.Run(() => RunOne(testFile, sizeBytes, isWrite: true, isRandom: false, token), token);
                progress.Report(new BenchmarkResult(label, sizeBytes, "Sequential", "Write", seqWrite.Mbps, seqWrite.Ms, seqWrite.Unbuffered, seqWrite.FallbackReason));

                var seqRead = await Task.Run(() => RunOne(testFile, sizeBytes, isWrite: false, isRandom: false, token), token);
                progress.Report(new BenchmarkResult(label, sizeBytes, "Sequential", "Read", seqRead.Mbps, seqRead.Ms, seqRead.Unbuffered, seqRead.FallbackReason));

                var randWrite = await Task.Run(() => RunOne(testFile, sizeBytes, isWrite: true, isRandom: true, token), token);
                progress.Report(new BenchmarkResult(label, sizeBytes, "Random", "Write", randWrite.Mbps, randWrite.Ms, randWrite.Unbuffered, randWrite.FallbackReason));

                var randRead = await Task.Run(() => RunOne(testFile, sizeBytes, isWrite: false, isRandom: true, token), token);
                progress.Report(new BenchmarkResult(label, sizeBytes, "Random", "Read", randRead.Mbps, randRead.Ms, randRead.Unbuffered, randRead.FallbackReason));
            }
            finally
            {
                try
                {
                    File.SetAttributes(testFile, FileAttributes.Normal);
                    File.Delete(testFile);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    /// The root of the system drive is protected against unelevated writes on modern Windows
    /// (UAC virtualization/ACLs), so a benchmark run against it fails outright otherwise. The
    /// user's temp folder is always on that same physical drive, so redirecting there for the
    /// system drive specifically still measures that drive's real speed. Other drives keep writing
    /// to their own root - redirecting them to system temp would silently benchmark the wrong disk.
    private static string ResolveWritableTestFolder(string driveRoot)
    {
        var systemDriveRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        return string.Equals(Path.GetPathRoot(driveRoot), systemDriveRoot, StringComparison.OrdinalIgnoreCase)
            ? Path.GetTempPath()
            : driveRoot;
    }

    private static (double Mbps, double Ms, bool Unbuffered, string? FallbackReason) RunOne(string path, long sizeBytes, bool isWrite, bool isRandom, CancellationToken token)
    {
        try
        {
            var (mbps, ms) = RunUnbuffered(path, sizeBytes, isWrite, isRandom, token);
            return (mbps, ms, true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var (mbps, ms) = RunBuffered(path, sizeBytes, isWrite, isRandom, token);
            return (mbps, ms, false, ex.Message);
        }
    }

    // ----- Unbuffered (real disk speed) path -----

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x1;
    private const uint CreateAlways = 2;
    private const uint OpenExisting = 3;
    private const uint FileFlagNoBuffering = 0x20000000;
    private const uint FileFlagWriteThrough = 0x80000000;
    private const uint FileAttributeHidden = 0x2;
    private const uint FileAttributeTemporary = 0x100;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    private static unsafe (double Mbps, double Ms) RunUnbuffered(string path, long sizeBytes, bool isWrite, bool isRandom, CancellationToken token)
    {
        var creation = isWrite && !isRandom ? CreateAlways : OpenExisting;
        var flags = FileFlagNoBuffering | FileFlagWriteThrough | FileAttributeTemporary;
        if (isWrite && !isRandom)
        {
            flags |= FileAttributeHidden;
        }

        using var handle = CreateFileW(path, isWrite ? GenericWrite : GenericRead, FileShareRead, IntPtr.Zero, creation, flags, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new IOException($"CreateFile failed (Win32 error {Marshal.GetLastWin32Error()})");
        }

        var blockSize = isRandom ? RandomBlockSize : SequentialChunkSize;
        var buffer = (byte*)NativeMemory.AlignedAlloc((nuint)blockSize, SectorSize);

        try
        {
            var span = new Span<byte>(buffer, blockSize);
            if (isWrite)
            {
                Random.Shared.NextBytes(span);
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            long totalBytes;

            if (isRandom)
            {
                var maxBlockIndex = Math.Max(1, (sizeBytes - RandomBlockSize) / SectorSize);
                var rng = new Random();
                for (var i = 0; i < RandomIterations; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var offset = rng.NextInt64(maxBlockIndex) * SectorSize;
                    if (isWrite) RandomAccess.Write(handle, span, offset); else RandomAccess.Read(handle, span, offset);
                }

                totalBytes = (long)RandomIterations * RandomBlockSize;
            }
            else
            {
                long offset = 0;
                while (offset < sizeBytes)
                {
                    token.ThrowIfCancellationRequested();
                    if (isWrite) RandomAccess.Write(handle, span, offset); else RandomAccess.Read(handle, span, offset);
                    offset += SequentialChunkSize;
                }

                totalBytes = sizeBytes;
            }

            sw.Stop();
            return ToResult(totalBytes, sw.Elapsed);
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
        }
    }

    // ----- Buffered fallback -----

    private static (double Mbps, double Ms) RunBuffered(string path, long sizeBytes, bool isWrite, bool isRandom, CancellationToken token)
    {
        var blockSize = isRandom ? RandomBlockSize : SequentialChunkSize;
        var buffer = new byte[blockSize];
        if (isWrite)
        {
            Random.Shared.NextBytes(buffer);
        }

        using var fs = new FileStream(
            path,
            isWrite && !isRandom ? FileMode.Create : FileMode.OpenOrCreate,
            isWrite ? FileAccess.Write : FileAccess.Read,
            FileShare.Read);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        long totalBytes;

        if (isRandom)
        {
            var maxBlockIndex = Math.Max(1, (sizeBytes - RandomBlockSize) / SectorSize);
            var rng = new Random();
            for (var i = 0; i < RandomIterations; i++)
            {
                token.ThrowIfCancellationRequested();
                fs.Position = rng.NextInt64(maxBlockIndex) * SectorSize;
                if (isWrite) fs.Write(buffer); else fs.ReadExactly(buffer);
            }

            totalBytes = (long)RandomIterations * RandomBlockSize;
        }
        else
        {
            long offset = 0;
            while (offset < sizeBytes)
            {
                token.ThrowIfCancellationRequested();
                if (isWrite) fs.Write(buffer); else fs.ReadExactly(buffer);
                offset += SequentialChunkSize;
            }

            totalBytes = sizeBytes;
        }

        sw.Stop();
        return ToResult(totalBytes, sw.Elapsed);
    }

    private static (double Mbps, double Ms) ToResult(long totalBytes, TimeSpan elapsed)
    {
        var seconds = Math.Max(elapsed.TotalSeconds, 0.001);
        return (totalBytes / 1_000_000.0 / seconds, elapsed.TotalMilliseconds);
    }

    // ----- Hardware info (WMI) -----

    public static DriveHardwareInfo GetDriveHardwareInfo(string driveRoot)
    {
        var deviceId = driveRoot.TrimEnd('\\').TrimEnd(':') + ":";
        string? manufacturer = null;
        string? model = null;
        string? fileSystem = null;
        string? interfaceType = null;
        long capacityBytes = 0;

        try
        {
            using var logicalDiskSearcher = new ManagementObjectSearcher(
                $"SELECT * FROM Win32_LogicalDisk WHERE DeviceID = '{deviceId}'");

            foreach (ManagementObject logicalDisk in logicalDiskSearcher.Get())
            {
                fileSystem = logicalDisk["FileSystem"] as string;
                if (logicalDisk["Size"] is { } size)
                {
                    capacityBytes = Convert.ToInt64(size);
                }

                using var partitionSearcher = new ManagementObjectSearcher(
                    $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{deviceId}'}} WHERE AssocClass = Win32_LogicalDiskToPartition");

                foreach (ManagementObject partition in partitionSearcher.Get())
                {
                    using var diskSearcher = new ManagementObjectSearcher(
                        $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partition["DeviceID"]}'}} WHERE AssocClass = Win32_DiskDriveToDiskPartition");

                    foreach (ManagementObject disk in diskSearcher.Get())
                    {
                        manufacturer = (disk["Manufacturer"] as string)?.Trim();
                        model = (disk["Model"] as string)?.Trim();
                        interfaceType = disk["InterfaceType"] as string;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException)
        {
        }

        return new DriveHardwareInfo(manufacturer, model, capacityBytes, fileSystem, interfaceType, ApproximateInterfaceSpeed(interfaceType, model));
    }

    /// WMI's classic Win32_DiskDrive reports SATA as "IDE" and NVMe as "SCSI" - genuine interface
    /// speed isn't reliably queryable without deeper storage-namespace WMI classes, so this is a
    /// best-effort label, not a measured value. NVMe drives almost universally include "NVMe" in
    /// their WMI Model string, which is a more reliable signal here than InterfaceType alone.
    private static string? ApproximateInterfaceSpeed(string? interfaceType, string? model)
    {
        if (model?.Contains("NVMe", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "NVMe (PCIe)";
        }

        return interfaceType switch
        {
            "IDE" => "SATA (~6 Gb/s)",
            "USB" => "USB",
            "SCSI" => "SCSI",
            "1394" => "FireWire",
            null => null,
            _ => interfaceType,
        };
    }

    // ----- Export -----

    private sealed record BenchmarkExport(string Drive, string ExportedAt, DriveHardwareInfo HardwareInfo, IReadOnlyList<BenchmarkResult> Results);

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public static string Export(string driveRoot, DriveHardwareInfo info, IReadOnlyList<BenchmarkResult> results)
    {
        var export = new BenchmarkExport(driveRoot, DateTimeOffset.Now.ToString("o"), info, results);

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var driveLabel = driveRoot.TrimEnd('\\').Replace(':', '_');
        var fileName = $"DiskBenchmark_{driveLabel}_{DateTime.Now:yyyyMMddHHmmss}.json";
        var exportPath = Path.Combine(documents, fileName);

        File.WriteAllText(exportPath, JsonSerializer.Serialize(export, SerializerOptions));
        return exportPath;
    }
}
