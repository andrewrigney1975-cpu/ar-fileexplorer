using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace FileExplorer.Services;

/// Produces and caches small (96px) thumbnail bitmaps two ways: an in-memory dictionary for the
/// current session, and a single hidden per-folder file (".arexx-thumbs.cache") so thumbnails
/// survive across app restarts instead of every folder visit re-decoding full-size images.
/// Entries are keyed by filename + last-write-time, so an edited file gets a fresh thumbnail.
public static class ThumbnailCacheService
{
    private const string CacheFileName = ".arexx-thumbs.cache";
    private const int FlushDelayMs = 800;
    private const int MaxFolderScanDepth = 3;
    private const int MaxFolderScanEntries = 500;

    /// User-configurable (Control Centre > Thumbnails), default 192 (2x the Gallery view's 184px
    /// tile so it doesn't look upscaled/blurry). Every on-disk cache file also stores the size it
    /// was generated at (see Magic below) - a folder whose cache was written at a different size
    /// than the current setting is treated as stale and regenerated at the new size.
    private static uint MaxDimension => (uint)Math.Clamp(SettingsService.Current.ThumbnailSize, 32, 1024);

    // Bumped to ATC3 to add the per-file MaxDimension header this size check relies on - without a
    // magic bump, pre-existing ATC2 files (fixed-192, no size header) would misparse as ATC3.
    private static readonly byte[] Magic = { (byte)'A', (byte)'T', (byte)'C', (byte)'3' };

    private static uint _lastKnownMaxDimension = MaxDimension;

    static ThumbnailCacheService()
    {
        SettingsService.Changed += (_, _) =>
        {
            var size = MaxDimension;
            if (size == _lastKnownMaxDimension)
            {
                return;
            }

            _lastKnownMaxDimension = size;

            // On-disk caches are left alone (lazily invalidated per-folder by the size header
            // check in LoadFolderIndex/below) - only the in-memory state needs clearing now, so
            // already-open panes re-decode at the new size instead of keeping stale bitmaps.
            lock (Sync)
            {
                MemoryCache.Clear();
                FolderIndexes.Clear();
                foreach (var timer in FlushTimers.Values)
                {
                    timer.Dispose();
                }
                FlushTimers.Clear();
            }
        };
    }

    private sealed record DiskEntry(long ModifiedTicks, byte[] Png);

    // Guards every read/write of FolderIndexes/the per-folder dictionaries/FlushTimers. The
    // debounced save below runs on a ThreadPool timer thread while new thumbnails keep being added
    // from the UI thread as the user scrolls - Dictionary<> is not safe for that without a lock
    // (this was the actual bug behind "thumbnails stop generating after scrolling a bit": the
    // background save was enumerating the same dictionary the UI thread was still writing to).
    private static readonly object Sync = new();

    private static readonly Dictionary<string, BitmapImage> MemoryCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Dictionary<string, DiskEntry>> FolderIndexes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Timer> FlushTimers = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<BitmapImage?> GetOrCreateAsync(string fullPath, DateTimeOffset modified, bool isDirectory = false)
    {
        var memoryKey = $"{fullPath}|{modified.UtcTicks}";
        if (MemoryCache.TryGetValue(memoryKey, out var cached))
        {
            return cached;
        }

        var folder = Path.GetDirectoryName(fullPath);
        var name = Path.GetFileName(fullPath);
        if (folder is null)
        {
            return null;
        }

        var index = LoadFolderIndex(folder);

        byte[]? png;
        lock (Sync)
        {
            png = index.TryGetValue(name, out var entry) && entry.ModifiedTicks == modified.UtcTicks
                ? entry.Png
                : null;
        }

        if (png is null)
        {
            // A folder's own "thumbnail" is just a downscaled copy of the first image found inside
            // it (recursing into subfolders, breadth-first, if it has none directly) - the cache
            // entry is still keyed by the folder's own path/modified time, not the found image's.
            var sourceImagePath = isDirectory ? await Task.Run(() => FindFirstImage(fullPath)) : fullPath;
            if (sourceImagePath is null)
            {
                return null;
            }

            png = await EncodeThumbnailAsync(sourceImagePath);
            if (png is null)
            {
                return null;
            }

            lock (Sync)
            {
                index[name] = new DiskEntry(modified.UtcTicks, png);
            }

            ScheduleFlush(folder, index);
        }

        var bitmap = new BitmapImage();
        using var stream = new MemoryStream(png);
        await bitmap.SetSourceAsync(stream.AsRandomAccessStream());

        MemoryCache[memoryKey] = bitmap;
        return bitmap;
    }

    /// Looks for a representative image inside a folder: files directly in it first (alphabetical),
    /// then each subfolder in turn, up to MaxFolderScanDepth deep. Bounded by MaxFolderScanEntries
    /// total filesystem entries so a huge, image-less tree can't make browsing a folder feel stuck.
    private static string? FindFirstImage(string folderPath)
    {
        var budget = MaxFolderScanEntries;
        return FindFirstImage(folderPath, MaxFolderScanDepth, ref budget);
    }

    private static string? FindFirstImage(string folderPath, int depthRemaining, ref int budget)
    {
        if (budget <= 0)
        {
            return null;
        }

        List<string> files;
        List<string> subfolders;

        try
        {
            files = Directory.EnumerateFiles(folderPath)
                .Where(f => IconHelper.IsPreviewableImage(Path.GetExtension(f)))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            subfolders = depthRemaining > 0
                ? Directory.EnumerateDirectories(folderPath)
                    .Where(d => !File.GetAttributes(d).HasFlag(System.IO.FileAttributes.Hidden))
                    .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : new List<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        budget -= files.Count + subfolders.Count;

        if (files.Count > 0)
        {
            return files[0];
        }

        foreach (var subfolder in subfolders)
        {
            var found = FindFirstImage(subfolder, depthRemaining - 1, ref budget);
            if (found is not null)
            {
                return found;
            }

            if (budget <= 0)
            {
                return null;
            }
        }

        return null;
    }

    private static async Task<byte[]?> EncodeThumbnailAsync(string sourcePath)
    {
        // WIC/BitmapDecoder can't read AVIF without a separate OS codec install - libheif handles
        // the decode instead, converging back to the same PNG-bytes result either way.
        if (string.Equals(Path.GetExtension(sourcePath), ".avif", StringComparison.OrdinalIgnoreCase))
        {
            var avifPng = await AvifImageService.DecodeToPngAsync(sourcePath, MaxDimension);
            if (avifPng is null)
            {
                LogFailure(sourcePath, new InvalidOperationException("AVIF decode failed."));
            }
            return avifPng;
        }

        try
        {
            using var sourceStream = await FileRandomAccessStream.OpenAsync(sourcePath, FileAccessMode.Read);
            var decoder = await BitmapDecoder.CreateAsync(sourceStream);

            var scale = Math.Min(1.0, (double)MaxDimension / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
            var width = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale));
            var height = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale));

            var pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                new BitmapTransform { ScaledWidth = width, ScaledHeight = height, InterpolationMode = BitmapInterpolationMode.Fant },
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage);

            using var outputStream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outputStream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, width, height, 96, 96, pixelData.DetachPixelData());
            await encoder.FlushAsync();

            var bytes = new byte[outputStream.Size];
            outputStream.Seek(0);
            await outputStream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
            return bytes;
        }
        catch (Exception ex)
        {
            LogFailure(sourcePath, ex);
            return null;
        }
    }

    private static void LogFailure(string sourcePath, Exception ex) =>
        LoggingService.LogWarning($"ThumbnailCacheService: {sourcePath}", ex);

    private static Dictionary<string, DiskEntry> LoadFolderIndex(string folder)
    {
        lock (Sync)
        {
            if (FolderIndexes.TryGetValue(folder, out var cached))
            {
                return cached;
            }
        }

        var index = new Dictionary<string, DiskEntry>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(folder, CacheFileName);

        try
        {
            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                using var reader = new BinaryReader(stream);

                if (reader.ReadBytes(4).SequenceEqual(Magic) && reader.ReadUInt32() == MaxDimension)
                {
                    var count = reader.ReadInt32();
                    for (var i = 0; i < count; i++)
                    {
                        var name = reader.ReadString();
                        var modifiedTicks = reader.ReadInt64();
                        var length = reader.ReadInt32();
                        var data = reader.ReadBytes(length);
                        index[name] = new DiskEntry(modifiedTicks, data);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            index.Clear();
        }

        lock (Sync)
        {
            // Another call may have loaded (and possibly already started mutating) this folder's
            // index while the file read above was in flight - keep whichever came first.
            if (FolderIndexes.TryGetValue(folder, out var existing))
            {
                return existing;
            }

            FolderIndexes[folder] = index;
            return index;
        }
    }

    /// Debounces writes: a folder visit can generate many new thumbnails in quick succession
    /// (ListView realizing a burst of items), and rewriting the whole cache file after every single
    /// one would multiply the total bytes written many times over for no benefit.
    private static void ScheduleFlush(string folder, Dictionary<string, DiskEntry> index)
    {
        lock (Sync)
        {
            if (FlushTimers.TryGetValue(folder, out var existing))
            {
                existing.Change(FlushDelayMs, Timeout.Infinite);
                return;
            }

            FlushTimers[folder] = new Timer(
                _ =>
                {
                    // Snapshot under the lock, then write the copy outside it - SaveFolderIndex must
                    // never enumerate the live dictionary the UI thread can still be writing to.
                    Dictionary<string, DiskEntry> snapshot;
                    lock (Sync)
                    {
                        snapshot = new Dictionary<string, DiskEntry>(index, StringComparer.OrdinalIgnoreCase);
                        FlushTimers.Remove(folder);
                    }

                    SaveFolderIndex(folder, snapshot);
                },
                null,
                FlushDelayMs,
                Timeout.Infinite);
        }
    }

    private static void SaveFolderIndex(string folder, Dictionary<string, DiskEntry> index)
    {
        var path = Path.Combine(folder, CacheFileName);

        try
        {
            using (var stream = File.Create(path))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Magic);
                writer.Write(MaxDimension);
                writer.Write(index.Count);
                foreach (var (name, entry) in index)
                {
                    writer.Write(name);
                    writer.Write(entry.ModifiedTicks);
                    writer.Write(entry.Png.Length);
                    writer.Write(entry.Png);
                }
            }

            File.SetAttributes(path, File.GetAttributes(path) | System.IO.FileAttributes.Hidden);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
