using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using LibHeifSharp;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace FileExplorer.Services;

/// Decodes AVIF images via libheif (LibHeifSharp/LibHeif.Native.win-x64) - WIC/BitmapDecoder can't
/// read AVIF without a separate OS codec install, so this is the one format that needs its own path
/// instead of going through the normal WinRT BitmapDecoder pipeline.
public static class AvifImageService
{
    /// Decodes to PNG bytes, optionally downscaled (preserving aspect ratio) to fit within
    /// maxDimension on the longer side. Pass null for a full-resolution decode (preview pane use).
    public static async Task<byte[]?> DecodeToPngAsync(string path, uint? maxDimension)
    {
        try
        {
            using var context = new HeifContext(path);
            using var handle = context.GetPrimaryImageHandle();
            using var heifImage = handle.Decode(HeifColorspace.Rgb, HeifChroma.InterleavedRgba32);

            var width = heifImage.Width;
            var height = heifImage.Height;
            var plane = heifImage.GetPlane(HeifChannel.Interleaved);

            var tightStride = width * 4;
            var buffer = new byte[tightStride * height];

            if (plane.Stride == tightStride)
            {
                Marshal.Copy(plane.Scan0, buffer, 0, buffer.Length);
            }
            else
            {
                // libheif's row stride can include trailing padding - copy row by row into a
                // tightly-packed buffer instead of assuming Stride == width * 4.
                for (var y = 0; y < height; y++)
                {
                    Marshal.Copy(IntPtr.Add(plane.Scan0, y * plane.Stride), buffer, y * tightStride, tightStride);
                }
            }

            using var softwareBitmap = SoftwareBitmap.CreateCopyFromBuffer(
                buffer.AsBuffer(), BitmapPixelFormat.Rgba8, width, height, BitmapAlphaMode.Straight);

            using var outputStream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outputStream);
            encoder.SetSoftwareBitmap(softwareBitmap);

            if (maxDimension is { } max)
            {
                var scale = Math.Min(1.0, (double)max / Math.Max(width, height));
                encoder.BitmapTransform.ScaledWidth = (uint)Math.Max(1, Math.Round(width * scale));
                encoder.BitmapTransform.ScaledHeight = (uint)Math.Max(1, Math.Round(height * scale));
                encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
            }

            await encoder.FlushAsync();

            var bytes = new byte[outputStream.Size];
            outputStream.Seek(0);
            await outputStream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
            return bytes;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
