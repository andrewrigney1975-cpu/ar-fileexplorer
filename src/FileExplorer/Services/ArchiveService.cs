using System.IO.Compression;
using FileExplorer.Models;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace FileExplorer.Services;

/// Pure zip-create / archive-extract logic, pulled out of PaneView's code-behind so it can be
/// unit-testable and reused without needing a XamlRoot or any UI state.
public static class ArchiveService
{
    public static void CreateZip(string zipPath, IReadOnlyList<FileSystemItem> items)
    {
        try
        {
            using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            foreach (var item in items)
            {
                if (item.IsDirectory)
                {
                    AddDirectoryToZip(archive, item.FullPath, item.Name);
                }
                else
                {
                    archive.CreateEntryFromFile(item.FullPath, item.Name, CompressionLevel.Optimal);
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void AddDirectoryToZip(ZipArchive archive, string sourceDir, string entryPrefix)
    {
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file).Replace('\\', '/');
            archive.CreateEntryFromFile(file, $"{entryPrefix}/{relative}", CompressionLevel.Optimal);
        }
    }

    /// Extracts .zip/.rar/.7z/.tar/.gz/.tgz/.bz2/.xz - SharpCompress auto-detects the actual format
    /// from content (so e.g. "backup.tgz" or "logs.tar.gz" work the same as a plain .tar).
    public static void Extract(string archivePath, string destinationPath)
    {
        using var archive = ArchiveFactory.OpenArchive(archivePath);
        archive.WriteToDirectory(destinationPath, new ExtractionOptions
        {
            ExtractFullPath = true,
            Overwrite = true,
        });
    }
}
