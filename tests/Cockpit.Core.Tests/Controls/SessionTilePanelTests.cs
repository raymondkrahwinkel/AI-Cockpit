using Cockpit.App.Controls;
using Cockpit.Core.Shortcuts;
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
    public void DropClosedCells_ClosingAMiddlePane_CompactsSoTwoLeftFallBackToTheMinimalGrid()
    {
        // Three panes fill a 2×2's first three cells; close the middle one. The survivor's cells must compact to
        // two entries so Dimensions gives 2×1 (or 1×2 stacked) — not stay length-3 and render a 2×2 with a gap
        // (Raymond, 2026-07-21).
        var cells = new List<object?> { "a", "b", "c" };

        var removed = SessionTilePanel.DropClosedCells(cells, new HashSet<object> { "a", "c" });

        removed.Should().BeTrue();
        cells.Should().Equal("a", "c");
        SessionTilePanel.Dimensions(cells.Count).Should().Be((2, 1));
    }

    [Fact]
    public void DropClosedCells_KeepsADeliberateFreePlacementHole()
    {
        // The null here is a hole a drag left behind (a pane dropped onto an empty cell), tied to no pane — it is
        // not a closed session and must survive the reconcile.
        var cells = new List<object?> { "a", null, "b" };

        SessionTilePanel.DropClosedCells(cells, new HashSet<object> { "a", "b" }).Should().BeFalse();
        cells.Should().Equal(new object?[] { "a", null, "b" });
    }

    [Fact]
    public void DropClosedCells_RemovesAClosedPaneButKeepsAnInteriorHole()
    {
        // A deliberate hole at index 1 and a live pane "b" at index 2; closing "b" removes only its cell and
        // leaves the drag-hole where it is.
        var cells = new List<object?> { "a", null, "b", "c" };

        SessionTilePanel.DropClosedCells(cells, new HashSet<object> { "a", "c" }).Should().BeTrue();
        cells.Should().Equal(new object?[] { "a", null, "c" });
    }

    [Fact]
    public void PlaceInCells_OntoOwnCell_NoChange()
    {
        var cells = new List<object?> { "a", "b" };

        SessionTilePanel.PlaceInCells(cells, "a", 0).Should().BeFalse();
        cells.Should().Equal("a", "b");
    }

    // Spatial pane navigation (AC-31): NeighbourCell walks the grid from a cell in a direction and returns the
    // first occupied cell, or null at the edge. Layout of a full 2×2 (row-major):
    //   0 1
    //   2 3
    [Fact]
    public void NeighbourCell_InAFull2x2_MovesToTheAdjacentPane()
    {
        var full = new[] { true, true, true, true };

        SessionTilePanel.NeighbourCell(full, 0, PaneDirection.Right, stackVertically: false).Should().Be(1);
        SessionTilePanel.NeighbourCell(full, 0, PaneDirection.Down, stackVertically: false).Should().Be(2);
        SessionTilePanel.NeighbourCell(full, 3, PaneDirection.Left, stackVertically: false).Should().Be(2);
        SessionTilePanel.NeighbourCell(full, 3, PaneDirection.Up, stackVertically: false).Should().Be(1);
    }

    [Fact]
    public void NeighbourCell_AtAGridEdge_HasNoNeighbour()
    {
        var full = new[] { true, true, true, true };

        SessionTilePanel.NeighbourCell(full, 0, PaneDirection.Left, stackVertically: false).Should().BeNull();
        SessionTilePanel.NeighbourCell(full, 0, PaneDirection.Up, stackVertically: false).Should().BeNull();
        SessionTilePanel.NeighbourCell(full, 3, PaneDirection.Right, stackVertically: false).Should().BeNull();
        SessionTilePanel.NeighbourCell(full, 3, PaneDirection.Down, stackVertically: false).Should().BeNull();
    }

    [Fact]
    public void NeighbourCell_WithThreePanes_TreatsTheEmptyFourthCellAsNoNeighbour()
    {
        // Three panes fill a 2×2's first three cells; the fourth (1,1) is empty.
        //   0 1
        //   2 .
        var three = new[] { true, true, true };

        SessionTilePanel.NeighbourCell(three, 2, PaneDirection.Right, stackVertically: false).Should().BeNull();
        SessionTilePanel.NeighbourCell(three, 1, PaneDirection.Down, stackVertically: false).Should().BeNull();
        SessionTilePanel.NeighbourCell(three, 0, PaneDirection.Down, stackVertically: false).Should().Be(2);
    }

    [Fact]
    public void NeighbourCell_SkipsAHoleAndLandsOnTheNextPane()
    {
        // A three-row column pair with the (0,1) cell emptied — moving down from (0,0) skips the hole to (0,2).
        //   0 1
        //   . 3
        //   4 5
        var withHole = new[] { true, true, false, true, true, true };

        SessionTilePanel.NeighbourCell(withHole, 0, PaneDirection.Down, stackVertically: false).Should().Be(4);
    }

    [Fact]
    public void NeighbourCell_WhenStackingVertically_FollowsTheTransposedAxes()
    {
        // Column-major fill: 0 and 1 share the first column, 2 and 3 the next.
        //   0 2
        //   1 3
        var full = new[] { true, true, true, true };

        SessionTilePanel.NeighbourCell(full, 0, PaneDirection.Down, stackVertically: true).Should().Be(1);
        SessionTilePanel.NeighbourCell(full, 0, PaneDirection.Right, stackVertically: true).Should().Be(2);
    }

    [Fact]
    public void NeighbourCell_WithASinglePane_HasNoNeighbourInAnyDirection()
    {
        var one = new[] { true };

        foreach (var direction in Enum.GetValues<PaneDirection>())
        {
            SessionTilePanel.NeighbourCell(one, 0, direction, stackVertically: false).Should().BeNull();
        }
    }
}
