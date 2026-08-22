namespace FileExplorer.Services;

/// Pure pointer/rectangle math for PaneView's drag-rectangle multi-select (the "marquee"), pulled
/// out of the code-behind so it's unit-testable without a live visual tree. Deliberately works in
/// plain doubles rather than Windows.Foundation.Rect/Point, since those WinRT-projected types
/// aren't available to a WinUI-free test project.
public static class MarqueeGeometry
{
    /// Normalizes a drag start/current point pair into a top-left + size rectangle - the drag can
    /// go in any of the four directions from the start point, but a rectangle needs a top-left corner.
    public static (double X, double Y, double Width, double Height) ComputeRect(
        double startX, double startY, double currentX, double currentY)
    {
        var x = Math.Min(startX, currentX);
        var y = Math.Min(startY, currentY);
        var width = Math.Abs(currentX - startX);
        var height = Math.Abs(currentY - startY);
        return (x, y, width, height);
    }

    /// True once the pointer has moved far enough from its down-position that this should become
    /// an active marquee drag rather than (say) a stray sub-pixel jitter on a plain click.
    public static bool ExceedsDragThreshold(double startX, double startY, double currentX, double currentY, double threshold = 4)
    {
        return Math.Abs(currentX - startX) >= threshold || Math.Abs(currentY - startY) >= threshold;
    }

    /// Standard axis-aligned rectangle intersection test, each rectangle given as (x, y, width, height).
    public static bool Intersects(
        double aX, double aY, double aWidth, double aHeight,
        double bX, double bY, double bWidth, double bHeight)
    {
        return aX < bX + bWidth && aX + aWidth > bX && aY < bY + bHeight && aY + aHeight > bY;
    }
}
