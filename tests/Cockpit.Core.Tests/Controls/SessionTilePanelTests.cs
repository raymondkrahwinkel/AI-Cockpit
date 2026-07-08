using Cockpit.App.Controls;
using FluentAssertions;

namespace Cockpit.Core.Tests.Controls;

/// <summary>
/// The session tile panel sizes itself from the count of <b>visible</b> panels, so single-pane mode
/// (one visible) fills the area and multi-session mode tiles — the fix for the selected session being
/// stranded in its index's row (top/bottom half) by UniformGrid's per-index cells.
/// </summary>
public class SessionTilePanelTests
{
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 1, 1)] // one visible session fills the whole area
    [InlineData(2, 2, 1)]
    [InlineData(3, 2, 2)] // 3–4 form a 2×2
    [InlineData(4, 2, 2)]
    [InlineData(5, 2, 3)]
    public void Dimensions_TileVisibleChildren(int visibleCount, int expectedColumns, int expectedRows)
    {
        var (columns, rows) = SessionTilePanel.Dimensions(visibleCount);

        columns.Should().Be(expectedColumns);
        rows.Should().Be(expectedRows);
    }
}
