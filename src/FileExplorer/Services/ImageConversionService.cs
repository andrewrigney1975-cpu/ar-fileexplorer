using FileExplorer.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Pbm;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Qoi;
using SixLabors.ImageSharp.Formats.Tga;
using SixLabors.ImageSharp.Formats.Tiff;
using SixLabors.ImageSharp.Formats.Webp;

namespace FileExplorer.Services;

/// Image format conversion for the "Convert To..." context-menu action. Pure-managed via
/// SixLabors.ImageSharp; AVIF/HEIC/HEIF sources are decoded through the existing libheif path
/// (AvifImageService) first, then re-encoded to the chosen target.
public static class ImageConversionService
{
    /// Formats ImageSharp can read directly, plus the HEIF family handled via AvifImageService.
    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".png", ".gif", ".bmp", ".webp",
        ".tif", ".tiff", ".tga", ".pbm", ".qoi",
        ".avif", ".heic", ".heif",
    };

    private static readonly HashSet<string> HeifExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avif", ".heic", ".heif",
    };

    public static IReadOnlyList<ConversionFormat> Targets { get; } = new[]
    {
        new ConversionFormat(".png", "PNG", false),
        new ConversionFormat(".jpg", "JPEG", true),
        new ConversionFormat(".webp", "WebP", true),
        new ConversionFormat(".bmp", "BMP", false),
        new ConversionFormat(".gif", "GIF", false),
        new ConversionFormat(".tiff", "TIFF", false),
        new ConversionFormat(".tga", "TGA", false),
        new ConversionFormat(".qoi", "QOI", false),
    };

    /// Decodes a HEIF/AVIF/HEIC file to PNG bytes. Wired to AvifImageService.DecodeToPngAsync at
    /// app startup; injectable (and null-safe) so this service carries no WinUI dependency and can
    /// be unit-tested.
    public static Func<string, Task<byte[]?>>? HeifDecoder { get; set; }

    /// Sends a file to the Recycle Bin. Wired to the VB FileIO helper at app startup; injectable so
    /// this service depends only on plain BCL types.
    public static Action<string> RecycleFile { get; set; } = File.Delete;

    public static bool IsConvertibleImage(string extension) => SourceExtensions.Contains(extension);

    /// Flattens a mixed file/folder selection into the concrete list of image files to convert.
    /// A folder contributes its images at the requested depth; a loose file is included as-is.
    public static List<string> ResolveSources(IEnumerable<string> selectionPaths, FolderScanDepth depth)
    {
        var option = depth == FolderScanDepth.Recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in selectionPaths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    foreach (var file in Directory.EnumerateFiles(path, "*", option))
                    {
                        if (IsConvertibleImage(Path.GetExtension(file)) && seen.Add(file))
                        {
                            result.Add(file);
                        }
                    }
                }
                else if (File.Exists(path) && IsConvertibleImage(Path.GetExtension(path)) && seen.Add(path))
                {
                    result.Add(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogWarning($"ImageConversionService.ResolveSources: {path}", ex);
            }
        }

        return result;
    }

    /// Converts one file and then runs the post-conversion action on the original. Never throws -
    /// every failure comes back as a ConversionOutcome so a batch run can carry on.
    public static async Task<ConversionOutcome> ConvertAsync(string sourcePath, ConversionOptions options, CancellationToken ct)
    {
        var targetExt = options.Target.Extension;

        if (string.Equals(Path.GetExtension(sourcePath), targetExt, StringComparison.OrdinalIgnoreCase))
        {
            return new ConversionOutcome(sourcePath, ConversionStatus.Skipped, null, "Already in the target format");
        }

        var destination = FileOperationService.MakeUniqueDestination(
            Path.Combine(Path.GetDirectoryName(sourcePath)!, Path.GetFileNameWithoutExtension(sourcePath) + targetExt));

        try
        {
            await Task.Run(async () =>
            {
                using var image = await LoadAsync(sourcePath, ct);
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await image.SaveAsync(output, EncoderFor(options.Target, options.Quality), ct);
            }, ct);
        }
        catch (OperationCanceledException)
        {
            TryDeletePartial(destination);
            throw;
        }
        catch (Exception ex)
        {
            TryDeletePartial(destination);
            LoggingService.LogWarning($"ImageConversionService.ConvertAsync: {sourcePath}", ex);
            return new ConversionOutcome(sourcePath, ConversionStatus.Failed, null, ex.Message);
        }

        try
        {
            ApplyPostAction(sourcePath, options.PostAction);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoggingService.LogWarning($"ImageConversionService.ApplyPostAction: {sourcePath}", ex);
            return new ConversionOutcome(sourcePath, ConversionStatus.Converted, destination,
                $"Converted, but couldn't {PostActionVerb(options.PostAction)} the original: {ex.Message}");
        }

        return new ConversionOutcome(sourcePath, ConversionStatus.Converted, destination, null);
    }

    private static async Task<Image> LoadAsync(string path, CancellationToken ct)
    {
        if (!HeifExtensions.Contains(Path.GetExtension(path)))
        {
            return await Image.LoadAsync(path, ct);
        }

        if (HeifDecoder is null)
        {
            throw new NotSupportedException("HEIF/AVIF decoding is not available.");
        }

        var png = await HeifDecoder(path)
            ?? throw new NotSupportedException("Couldn't decode this HEIF/AVIF file.");
        using var stream = new MemoryStream(png);
        return await Image.LoadAsync(stream, ct);
    }

    private static SixLabors.ImageSharp.Formats.IImageEncoder EncoderFor(ConversionFormat target, int quality) => target.Extension switch
    {
        ".png" => new PngEncoder(),
        ".jpg" or ".jpeg" => new JpegEncoder { Quality = quality },
        ".webp" => new WebpEncoder { Quality = quality },
        ".bmp" => new BmpEncoder(),
        ".gif" => new GifEncoder(),
        ".tiff" => new TiffEncoder(),
        ".tga" => new TgaEncoder(),
        ".qoi" => new QoiEncoder(),
        ".pbm" => new PbmEncoder(),
        _ => new PngEncoder(),
    };

    private static void ApplyPostAction(string sourcePath, PostConversionAction action)
    {
        switch (action)
        {
            case PostConversionAction.KeepOriginal:
                return;

            case PostConversionAction.DeleteOriginal:
                RecycleFile(sourcePath);
                return;

            case PostConversionAction.MoveToOriginals:
                var originalsDir = Path.Combine(Path.GetDirectoryName(sourcePath)!, "Originals");
                Directory.CreateDirectory(originalsDir);
                var moveTarget = FileOperationService.MakeUniqueDestination(
                    Path.Combine(originalsDir, Path.GetFileName(sourcePath)));
                File.Move(sourcePath, moveTarget);
                return;
        }
    }

    private static string PostActionVerb(PostConversionAction action) => action switch
    {
        PostConversionAction.DeleteOriginal => "recycle",
        PostConversionAction.MoveToOriginals => "move",
        _ => "handle",
    };

    private static void TryDeletePartial(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort - a stray partial file is harmless
        }
    }
}
