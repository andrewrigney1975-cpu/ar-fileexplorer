using System.Runtime.InteropServices;
using LibHeifSharp;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace FileExplorer.Services;

public sealed record ImageMetadata(
    int Width,
    int Height,
    string Format,
    string BitDepth,
    string ColorModel,
    IReadOnlyList<(string Label, string Value)> Exif,
    double? Latitude = null,
    double? Longitude = null,
    double? Heading = null);

/// Reads an image's own technical metadata (as opposed to ThumbnailCacheService, which only cares
/// about producing a small preview bitmap) - dimensions, pixel format, and EXIF/camera properties,
/// for the Preview pane's "Image Info" section.
public static class ImageMetadataService
{
    // Windows Property System canonical names - the same identifiers Explorer's own "Details" tab
    // reads, retrievable from WIC-backed formats (JPEG/PNG/TIFF/BMP/GIF/HEIC) with no extra parsing.
    private static readonly (string Key, string Label)[] ExifProperties =
    {
        ("System.Photo.CameraManufacturer", "Camera make"),
        ("System.Photo.CameraModel", "Camera model"),
        ("System.Photo.LensModel", "Lens"),
        ("System.Photo.DateTaken", "Date taken"),
        ("System.Photo.ExposureTime", "Exposure time"),
        ("System.Photo.FNumber", "F-number"),
        ("System.Photo.FocalLength", "Focal length"),
        ("System.Photo.ISOSpeed", "ISO speed"),
        ("System.Photo.Orientation", "Orientation"),
        ("System.Photo.Flash", "Flash"),
        ("System.GPS.Latitude", "GPS latitude"),
        ("System.GPS.Longitude", "GPS longitude"),
    };

    // Fetched alongside ExifProperties but not shown as their own raw rows - used to compute the
    // signed decimal-degree coordinate and camera heading for the map, and to build the friendlier
    // "GPS location"/"Camera heading" rows below.
    private static readonly string[] GpsSupportKeys =
    {
        "System.GPS.LatitudeRef",
        "System.GPS.LongitudeRef",
        "System.GPS.ImgDirection",
        "System.GPS.ImgDirectionRef",
    };

    public static async Task<ImageMetadata?> ReadAsync(string path, string extension)
    {
        return string.Equals(extension, ".avif", StringComparison.OrdinalIgnoreCase)
            ? ReadAvif(path)
            : await ReadViaWicAsync(path).ConfigureAwait(false);
    }

    private static async Task<ImageMetadata?> ReadViaWicAsync(string path)
    {
        try
        {
            using var stream = await FileRandomAccessStream.OpenAsync(path, FileAccessMode.Read);
            var decoder = await BitmapDecoder.CreateAsync(stream);

            var (bitDepth, colorModel) = DescribeFormat(decoder.BitmapPixelFormat, decoder.BitmapAlphaMode);
            var exif = new List<(string, string)>();
            double? latitude = null;
            double? longitude = null;
            double? heading = null;

            try
            {
                var props = await decoder.BitmapProperties.GetPropertiesAsync(
                    ExifProperties.Select(p => p.Key).Concat(GpsSupportKeys));

                foreach (var (key, label) in ExifProperties)
                {
                    if (key is "System.GPS.Latitude" or "System.GPS.Longitude")
                    {
                        // Shown below as a single friendlier decimal-degree "GPS location" row instead
                        // of raw degrees/minutes/seconds arrays.
                        continue;
                    }

                    if (props.TryGetValue(key, out var typed) && typed.Value is not null &&
                        FormatPropertyValue(key, typed.Value) is { Length: > 0 } formatted)
                    {
                        exif.Add((label, formatted));
                    }
                }

                latitude = ToDecimalDegrees(
                    props.TryGetValue("System.GPS.Latitude", out var lat) ? lat.Value : null,
                    props.TryGetValue("System.GPS.LatitudeRef", out var latRef) ? latRef.Value as string : null,
                    negativeRef: 'S');

                longitude = ToDecimalDegrees(
                    props.TryGetValue("System.GPS.Longitude", out var lon) ? lon.Value : null,
                    props.TryGetValue("System.GPS.LongitudeRef", out var lonRef) ? lonRef.Value as string : null,
                    negativeRef: 'W');

                if (latitude is not null && longitude is not null)
                {
                    exif.Add(("GPS location", $"{latitude:0.00000}, {longitude:0.00000}"));
                }

                if (props.TryGetValue("System.GPS.ImgDirection", out var dir) && dir.Value is not null)
                {
                    heading = Convert.ToDouble(dir.Value);
                    exif.Add(("Camera heading", $"{heading:0.#}°"));
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Property store access can fail per-format (e.g. GIF/BMP have little/no metadata) -
                // dimensions/pixel format above are still worth showing even with no EXIF at all.
            }

            return new ImageMetadata(
                (int)decoder.PixelWidth,
                (int)decoder.PixelHeight,
                decoder.DecoderInformation.CodecId == BitmapDecoder.JpegDecoderId ? "JPEG"
                    : decoder.DecoderInformation.CodecId == BitmapDecoder.PngDecoderId ? "PNG"
                    : decoder.DecoderInformation.CodecId == BitmapDecoder.GifDecoderId ? "GIF"
                    : decoder.DecoderInformation.CodecId == BitmapDecoder.BmpDecoderId ? "BMP"
                    : decoder.DecoderInformation.CodecId == BitmapDecoder.TiffDecoderId ? "TIFF"
                    : decoder.DecoderInformation.FriendlyName,
                bitDepth,
                colorModel,
                exif,
                latitude,
                longitude,
                heading);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
        {
            return null;
        }
    }

    /// AVIF has no WIC codec installed by default, so dimensions/bit depth/alpha come straight from
    /// libheif's image handle instead - no full pixel decode needed just for this. EXIF extraction
    /// from AVIF's embedded metadata block would need manual TIFF parsing libheif doesn't do for us,
    /// so that list is left empty rather than guessed at.
    private static ImageMetadata? ReadAvif(string path)
    {
        try
        {
            using var context = new HeifContext(path);
            using var handle = context.GetPrimaryImageHandle();

            var colorModel = handle.HasAlphaChannel ? "RGBA" : "RGB";
            return new ImageMetadata(
                handle.Width,
                handle.Height,
                "AVIF",
                $"{handle.BitDepth}-bit",
                colorModel,
                Array.Empty<(string, string)>());
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static (string BitDepth, string ColorModel) DescribeFormat(BitmapPixelFormat format, BitmapAlphaMode alpha)
    {
        var hasAlpha = alpha != BitmapAlphaMode.Ignore;

        return format switch
        {
            BitmapPixelFormat.Rgba16 => ("64-bit (16 bits/channel)", hasAlpha ? "RGBA" : "RGB"),
            BitmapPixelFormat.Rgba8 => ("32-bit (8 bits/channel)", hasAlpha ? "RGBA" : "RGB"),
            BitmapPixelFormat.Bgra8 => ("32-bit (8 bits/channel)", hasAlpha ? "RGBA" : "RGB"),
            BitmapPixelFormat.Gray16 => ("16-bit", "Grayscale"),
            BitmapPixelFormat.Gray8 => ("8-bit", "Grayscale"),
            BitmapPixelFormat.Nv12 => ("12-bit", "YUV 4:2:0"),
            BitmapPixelFormat.Yuy2 => ("16-bit", "YUV 4:2:2"),
            BitmapPixelFormat.P010 => ("24-bit", "YUV 4:2:0 (10-bit)"),
            _ => ("Unknown", format.ToString()),
        };
    }

    /// GPS.Latitude/Longitude come back as a 3-element [degrees, minutes, seconds] array with a
    /// separate single-letter ref ("N"/"S" or "E"/"W") giving the sign - not a signed value itself.
    private static double? ToDecimalDegrees(object? dmsValue, string? refLetter, char negativeRef)
    {
        if (dmsValue is not Array arr || arr.Length < 3)
        {
            return null;
        }

        var degrees = Convert.ToDouble(arr.GetValue(0)) + Convert.ToDouble(arr.GetValue(1)) / 60.0 + Convert.ToDouble(arr.GetValue(2)) / 3600.0;

        if (!string.IsNullOrWhiteSpace(refLetter) && refLetter.Trim().Equals(negativeRef.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            degrees = -degrees;
        }

        return degrees;
    }

    private static string? FormatPropertyValue(string key, object value)
    {
        switch (value)
        {
            case string s:
                return s.Trim();
            case DateTimeOffset dto:
                return dto.ToString("yyyy-MM-dd HH:mm:ss");
            case double d when key == "System.Photo.ExposureTime":
                return d > 0 && d < 1 ? $"1/{Math.Round(1 / d)}s" : $"{d:0.###}s";
            case double d when key == "System.Photo.FNumber":
                return $"f/{d:0.#}";
            case double d when key == "System.Photo.FocalLength":
                return $"{d:0.#}mm";
            case double d:
                return d.ToString("0.###");
            case Array arr when arr.Length > 0:
                var items = arr.Cast<object>().Select(o => o?.ToString() ?? string.Empty).Where(s => s.Length > 0);
                return string.Join(", ", items);
            case null:
                return null;
            default:
                var text = value.ToString();
                return string.IsNullOrWhiteSpace(text) ? null : text;
        }
    }
}
