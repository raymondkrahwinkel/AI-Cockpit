using Cockpit.App.Controls;

namespace Cockpit.Core.Tests.Controls;

/// <summary>
/// The pure geometry behind the miniature rail (AC-443): rail width alone picks the column count, rail
/// height alone picks how many rows show before a scrollbar is needed, and a tile always keeps the
/// focus pane's own aspect ratio — all exercised without a visual tree.
/// </summary>
public class RailLayoutMathTests
{
    [Fact]
    public void Compute_NarrowRail_StaysOneColumn()
    {
        // Mockup scene 1: a 280px rail, six 16:10-ish tiles, minimum tile width 206.
        var geometry = RailLayoutMath.Compute(railWidth: 280, railHeight: 600, tileCount: 6, minTileWidth: 206, focusAspectRatio: 1.5625, gutter: 8);

        Assert.Equal(1, geometry.Columns);
        Assert.Equal(280, geometry.TileWidth);
        Assert.Equal(6, geometry.Rows);
    }

    [Fact]
    public void Compute_RailAtTwiceMinimumWidth_FoldsToTwoColumns()
    {
        // Mockup scene 2: divider dragged left, rail widens to 420px (>= 2x the 206px minimum) -> two columns.
        var geometry = RailLayoutMath.Compute(railWidth: 420, railHeight: 600, tileCount: 6, minTileWidth: 206, focusAspectRatio: 1.5625, gutter: 8);

        Assert.Equal(2, geometry.Columns);
        // (420 - 1*8) / 2 = 206.
        Assert.Equal(206, geometry.TileWidth);
        Assert.Equal(3, geometry.Rows);
    }

    [Fact]
    public void Compute_JustBelowTwiceMinimumWidth_StaysOneColumn()
    {
        var geometry = RailLayoutMath.Compute(railWidth: 411, railHeight: 600, tileCount: 6, minTileWidth: 206, focusAspectRatio: 1.5625, gutter: 8);

        Assert.Equal(1, geometry.Columns);
    }

    [Fact]
    public void Compute_TileHeight_FollowsTheFocusPaneAspectRatio()
    {
        // A 2:1 focus pane -> every tile is half as tall as it is wide, not a fixed shape. minTileWidth
        // keeps this to one column so TileWidth is simply the rail width.
        var geometry = RailLayoutMath.Compute(railWidth: 300, railHeight: 600, tileCount: 3, minTileWidth: 250, focusAspectRatio: 2.0, gutter: 0);

        Assert.Equal(300, geometry.TileWidth);
        Assert.Equal(150, geometry.TileHeight);
    }

    [Fact]
    public void Compute_ColumnsNeverExceedTileCount()
    {
        // One tile in a rail wide enough for three columns must not spread into two empty ones.
        var geometry = RailLayoutMath.Compute(railWidth: 600, railHeight: 400, tileCount: 1, minTileWidth: 100, focusAspectRatio: 1.0, gutter: 8);

        Assert.Equal(1, geometry.Columns);
        Assert.Equal(600, geometry.TileWidth);
    }

    [Fact]
    public void Compute_MoreTilesThanFit_OverflowsAndReportsVisibleCount()
    {
        // One column, 100px tiles + 8px gutter = 108px per row; a 250px-tall rail fits 2 full rows (216px).
        var geometry = RailLayoutMath.Compute(railWidth: 100, railHeight: 250, tileCount: 6, minTileWidth: 100, focusAspectRatio: 1.0, gutter: 8);

        Assert.Equal(1, geometry.Columns);
        Assert.Equal(6, geometry.Rows);
        Assert.Equal(2, geometry.VisibleRows);
        Assert.Equal(2, geometry.VisibleCount);
        Assert.True(geometry.Overflows);
    }

    [Fact]
    public void Compute_EverythingFits_DoesNotOverflow()
    {
        var geometry = RailLayoutMath.Compute(railWidth: 100, railHeight: 1000, tileCount: 3, minTileWidth: 100, focusAspectRatio: 1.0, gutter: 8);

        Assert.False(geometry.Overflows);
        Assert.Equal(3, geometry.VisibleCount);
    }

    [Theory]
    [InlineData(0, 100)]   // no tiles
    [InlineData(3, 0)]     // no width
    public void Compute_DegenerateInput_ReturnsEmptyGeometry(int tileCount, double railWidth)
    {
        var geometry = RailLayoutMath.Compute(railWidth, railHeight: 200, tileCount, minTileWidth: 100, focusAspectRatio: 1.0, gutter: 8);

        Assert.Equal(0, geometry.Columns);
        Assert.Equal(0, geometry.TileWidth);
    }

    [Fact]
    public void Compute_InfiniteRailHeight_DoesNotThrow()
    {
        // The measure pass of a vertical-scrolling ScrollViewer hands its content PositiveInfinity for height.
        var geometry = RailLayoutMath.Compute(railWidth: 200, railHeight: double.PositiveInfinity, tileCount: 4, minTileWidth: 100, focusAspectRatio: 1.0, gutter: 8);

        Assert.Equal(0, geometry.VisibleRows);
        Assert.True(geometry.Overflows);
    }

    [Fact]
    public void TileOrigin_FillsRowMajor_LeftToRightThenNextRow()
    {
        var geometry = RailLayoutMath.Compute(railWidth: 220, railHeight: 600, tileCount: 4, minTileWidth: 100, focusAspectRatio: 1.0, gutter: 20);

        Assert.Equal(2, geometry.Columns);
        Assert.Equal((0.0, 0.0), RailLayoutMath.TileOrigin(0, geometry, gutter: 20));
        Assert.Equal((geometry.TileWidth + 20, 0.0), RailLayoutMath.TileOrigin(1, geometry, gutter: 20));
        Assert.Equal((0.0, geometry.TileHeight + 20), RailLayoutMath.TileOrigin(2, geometry, gutter: 20));
    }

    [Fact]
    public void TileOrigin_NoColumns_ReturnsOrigin() =>
        Assert.Equal((0.0, 0.0), RailLayoutMath.TileOrigin(0, default, gutter: 8));
}
