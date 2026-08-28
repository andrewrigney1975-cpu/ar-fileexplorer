namespace FileExplorer.Helpers;

/// Renders a 0-5 rating as a filled/empty star string (e.g. 3.5 -> "★★★★☆", rounded).
public static class RatingFormat
{
    public static string? ToStars(double? value)
    {
        if (value is not { } v || v <= 0)
        {
            return null;
        }

        var filled = (int)Math.Round(Math.Clamp(v, 0, 5), MidpointRounding.AwayFromZero);
        return new string('★', filled) + new string('☆', 5 - filled);
    }
}
