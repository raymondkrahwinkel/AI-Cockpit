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

    [Theory]
    // Stacking vertically is the transpose of the default: cap at two rows, grow columns, fill column by
    // column. Two panes share one column; the third starts a second column; four make a 2×2.
    [InlineData(1, 1, 1)]
    [InlineData(2, 1, 2)] // two sessions stack one above the other in a single column
    [InlineData(3, 2, 2)] // third starts a second column
    [InlineData(4, 2, 2)]
    [InlineData(5, 3, 2)]
    public void Dimensions_WhenStackingVertically_CapTwoRowsAndGrowColumns(int visibleCount, int expectedColumns, int expectedRows)
    {
        var (columns, rows) = SessionTilePanel.Dimensions(visibleCount, stackVertically: true);

        columns.Should().Be(expectedColumns);
        rows.Should().Be(expectedRows);
    }

    [Fact]
    public void PlaceInCells_OntoAHole_MovesAndLeavesAHoleBehind()
    {
        var cells = new List<object?> { "a", "b", "c" };

        // Drop "a" onto the empty 4th cell (index 3): it lands there, its old cell becomes a hole.
        var changed = SessionTilePanel.PlaceInCells(cells, "a", 3);

        changed.Should().BeTrue();
        cells.Should().Equal(new object?[] { null, "b", "c", "a" });
    }

    [Fact]
    public void PlaceInCells_OntoAnOccupiedCell_Swaps()
    {
        var cells = new List<object?> { "a", "b", "c" };

        SessionTilePanel.PlaceInCells(cells, "a", 2).Should().BeTrue();

        cells.Should().Equal("c", "b", "a");
    }

    [Fact]
    public void PlaceInCells_TrimsTrailingHolesButKeepsInteriorOnes()
    {
        var cells = new List<object?> { "a", "b" };

        // Drop "b" onto index 3 (past the end): it pads with holes, lands at 3, and its old cell 1 stays a
        // hole; the pad at index 2 also stays a hole because "b" sits after it.
        SessionTilePanel.PlaceInCells(cells, "b", 3);

        cells.Should().Equal(new object?[] { "a", null, null, "b" });
    }

    [Fact]
    public void PlaceInCells_OntoOwnCell_NoChange()
    {
        var cells = new List<object?> { "a", "b" };

        SessionTilePanel.PlaceInCells(cells, "a", 0).Should().BeFalse();
        cells.Should().Equal("a", "b");
    }
}
