using System.Management;

namespace FileExplorer.Services;

public static class DiskActivityMonitorService
{
    public sealed record DiskActivitySample(string DriveLetter, double ReadMBps, double WriteMBps);

    private const double BytesPerMB = 1024.0 * 1024.0;

    /// One-shot read of every logical disk's current read/write throughput. WMI's "formatted" perf
    /// counters (unlike the raw ones) already return a computed per-second rate, so a single query is
    /// enough - no need to sample twice ourselves and compute the delta.
    public static List<DiskActivitySample> Sample()
    {
        var result = new List<DiskActivitySample>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DiskReadBytesPersec, DiskWriteBytesPersec FROM Win32_PerfFormattedData_PerfDisk_LogicalDisk");
            using var queryResults = searcher.Get();

            foreach (ManagementObject mo in queryResults)
            {
                var name = mo["Name"] as string;
                if (string.IsNullOrEmpty(name) || name == "_Total")
                {
                    continue;
                }

                var readBytes = Convert.ToDouble(mo["DiskReadBytesPersec"] ?? 0);
                var writeBytes = Convert.ToDouble(mo["DiskWriteBytesPersec"] ?? 0);
                result.Add(new DiskActivitySample(name, readBytes / BytesPerMB, writeBytes / BytesPerMB));
            }
        }
        catch (ManagementException ex)
        {
            LoggingService.LogWarning("DiskActivityMonitorService.Sample", ex);
        }

        return result;
    }
}
