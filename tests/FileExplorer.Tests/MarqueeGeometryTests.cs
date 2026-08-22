using FileExplorer.Services;

namespace FileExplorer.Tests;

public class MarqueeGeometryTests
{
    [Fact]
    public void ComputeRect_DragDownRight_TopLeftIsStartPoint()
    {
        var (x, y, w, h) = MarqueeGeometry.ComputeRect(10, 10, 50, 40);

        Assert.Equal(10, x);
        Assert.Equal(10, y);
        Assert.Equal(40, w);
        Assert.Equal(30, h);
    }

    [Fact]
    public void ComputeRect_DragUpLeft_NormalizesToPositiveRect()
    {
        var (x, y, w, h) = MarqueeGeometry.ComputeRect(50, 40, 10, 10);

        Assert.Equal(10, x);
        Assert.Equal(10, y);
        Assert.Equal(40, w);
        Assert.Equal(30, h);
    }

    [Fact]
    public void ComputeRect_DragUpRight_NormalizesCorrectly()
    {
        var (x, y, w, h) = MarqueeGeometry.ComputeRect(10, 40, 50, 10);

        Assert.Equal(10, x);
        Assert.Equal(10, y);
        Assert.Equal(40, w);
        Assert.Equal(30, h);
    }

    [Fact]
    public void ComputeRect_NoMovement_ReturnsZeroSize()
    {
        var (x, y, w, h) = MarqueeGeometry.ComputeRect(10, 10, 10, 10);

        Assert.Equal(10, x);
        Assert.Equal(10, y);
        Assert.Equal(0, w);
        Assert.Equal(0, h);
    }

    [Theory]
    [InlineData(0, 0, 3, 3, false)]
    [InlineData(0, 0, 4, 0, true)]
    [InlineData(0, 0, 0, 4, true)]
    [InlineData(0, 0, 5, 5, true)]
    public void ExceedsDragThreshold_UsesDefaultFourPixelThreshold(double sx, double sy, double cx, double cy, bool expected)
    {
        Assert.Equal(expected, MarqueeGeometry.ExceedsDragThreshold(sx, sy, cx, cy));
    }

    [Fact]
    public void ExceedsDragThreshold_CustomThreshold_IsRespected()
    {
        Assert.False(MarqueeGeometry.ExceedsDragThreshold(0, 0, 8, 0, threshold: 10));
        Assert.True(MarqueeGeometry.ExceedsDragThreshold(0, 0, 12, 0, threshold: 10));
    }

    [Fact]
    public void Intersects_OverlappingRects_ReturnsTrue()
    {
        Assert.True(MarqueeGeometry.Intersects(0, 0, 10, 10, 5, 5, 10, 10));
    }

    [Fact]
    public void Intersects_SeparateRects_ReturnsFalse()
    {
        Assert.False(MarqueeGeometry.Intersects(0, 0, 10, 10, 20, 20, 10, 10));
    }

    [Fact]
    public void Intersects_TouchingEdgesOnly_ReturnsFalse()
    {
        // Adjacent, sharing only a boundary line - not a real overlap.
        Assert.False(MarqueeGeometry.Intersects(0, 0, 10, 10, 10, 0, 10, 10));
    }

    [Fact]
    public void Intersects_OneRectFullyInsideAnother_ReturnsTrue()
    {
        Assert.True(MarqueeGeometry.Intersects(0, 0, 100, 100, 40, 40, 10, 10));
    }
}
