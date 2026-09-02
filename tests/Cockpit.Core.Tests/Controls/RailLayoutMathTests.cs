using Cockpit.App.Controls;

namespace Cockpit.Core.Tests.Controls;

/// <summary>
/// The pure geometry behind the miniature rail (AC-443): rail width alone picks the column count, rail
/// height alone picks how many rows show before a scrollbar is needed, and a tile always keeps the
/// focus pane's own aspect ratio — all exercised without a visual tree.
/// </summary>
public class RailLayoutMathTests
{
    // Six 16:10-ish tiles with a 206px minimum, at the three rail widths that matter: the mockup's narrow
    // 280px rail, one pixel short of the fold, and the 420px the divider reaches when dragged left — where
    // (420 - 1*8) / 2 lands exactly on the minimum and the second column opens.
    [Theory]
    [InlineData(280, 1, 280, 6)]
    [InlineData(411, 1, 411, 6)]
    [InlineData(420, 2, 206, 3)]
    public void Compute_FoldsToASecondColumn_OnlyAtTwiceTheMinimumTileWidth(
        double railWidth, int expectedColumns, double expectedTileWidth, int expectedRows)
    {
        var geometry = RailLayoutMath.Compute(railWidth, railHeight: 600, tileCount: 6, minTileWidth: 206, focusAspectRatio: 1.5625, gutter: 8);

        Assert.Equal(expectedColumns, geometry.Columns);
        Assert.Equal(expectedTileWidth, geometry.TileWidth);
        Assert.Equal(expectedRows, geometry.Rows);
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

    // One column, 100px tiles + 8px gutter = 108px per row. A 250px-tall rail fits two full rows (216px) and
    // says so; a 1000px one fits everything and reports no overflow at all.
    [Theory]
    [InlineData(250, 6, 6, 2, 2, true)]
    [InlineData(1000, 3, 3, 3, 3, false)]
    public void Compute_ReportsWhatFitsBeforeAScrollbarIsNeeded(
        double railHeight, int tileCount, int expectedRows, int expectedVisibleRows, int expectedVisibleCount, bool expectedOverflows)
    {
        var geometry = RailLayoutMath.Compute(railWidth: 100, railHeight, tileCount, minTileWidth: 100, focusAspectRatio: 1.0, gutter: 8);

        Assert.Equal(1, geometry.Columns);
        Assert.Equal(expectedRows, geometry.Rows);
        Assert.Equal(expectedVisibleRows, geometry.VisibleRows);
        Assert.Equal(expectedVisibleCount, geometry.VisibleCount);
        Assert.Equal(expectedOverflows, geometry.Overflows);
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
