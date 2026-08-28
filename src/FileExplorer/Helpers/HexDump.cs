using System.Text;

namespace FileExplorer.Helpers;

/// Classic `xxd`-style hex dump used by the preview pane for files no other previewer handles.
public static class HexDump
{
    private const int BytesPerRow = 16;

    /// Renders <paramref name="bytes"/> as offset / hex / ASCII rows. Bytes are split 8+8 per row
    /// with a gap in the middle; non-printable bytes show as '.' in the ASCII column.
    public static string Format(ReadOnlySpan<byte> bytes, long baseOffset = 0)
    {
        var sb = new StringBuilder(bytes.Length / BytesPerRow * 78 + 16);

        for (var rowStart = 0; rowStart < bytes.Length; rowStart += BytesPerRow)
        {
            sb.Append((baseOffset + rowStart).ToString("X8"));
            sb.Append("  ");

            for (var col = 0; col < BytesPerRow; col++)
            {
                if (col == BytesPerRow / 2)
                {
                    sb.Append(' ');
                }

                var i = rowStart + col;
                sb.Append(i < bytes.Length ? bytes[i].ToString("X2") : "  ");
                sb.Append(' ');
            }

            sb.Append(' ');
            for (var col = 0; col < BytesPerRow; col++)
            {
                var i = rowStart + col;
                if (i >= bytes.Length)
                {
                    break;
                }

                var b = bytes[i];
                sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }
}
