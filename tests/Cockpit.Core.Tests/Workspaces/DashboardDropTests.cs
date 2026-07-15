using Cockpit.Core.Workspaces;
using FluentAssertions;

namespace Cockpit.Core.Tests.Workspaces;

/// <summary>
/// Dragging a widget across the dashboard (F0, Raymond 2026-07-15: "ga verder met F0 zodat widgets sleepbaar
/// zijn"). Free placement with holes, the same as the session grid: an empty cell takes the pane, an occupied
/// one swaps. The whole arrangement comes back at once so a swap can never half-land and stack two widgets on
/// one cell.
/// </summary>
public class DashboardDropTests
{
    [Fact]
    public void Drop_OnAnEmptyCell_MovesThePane_AndLeavesAHoleBehind()
    {
        var panes = _Panes(("a", 0, 0), ("b", 1, 0));

        var arranged = DashboardGridMath.Drop(panes, "a", (1, 1), _Grid);

        _CellOf(arranged, "a").Should().Be(new GridCell(1, 1));
        _CellOf(arranged, "b").Should().Be(new GridCell(1, 0), "the pane that was not dragged does not move");
    }

    [Fact]
    public void Drop_OnAnOccupiedCell_SwapsTheTwo()
    {
        var panes = _Panes(("a", 0, 0), ("b", 1, 0));

        var arranged = DashboardGridMath.Drop(panes, "a", (1, 0), _Grid);

        _CellOf(arranged, "a").Should().Be(new GridCell(1, 0));
        _CellOf(arranged, "b").Should().Be(new GridCell(0, 0));
    }

    [Fact]
    public void Drop_OnItsOwnCell_ChangesNothing()
    {
        var panes = _Panes(("a", 0, 0), ("b", 1, 0));

        DashboardGridMath.Drop(panes, "a", (0, 0), _Grid).Should().BeSameAs(panes);
    }

    [Fact]
    public void Drop_AnUnknownPane_IsRefused()
    {
        var panes = _Panes(("a", 0, 0));

        DashboardGridMath.Drop(panes, "gone", (1, 1), _Grid).Should().BeNull();
    }

    [Fact]
    public void Drop_KeepsTheDraggedPanesSpan()
    {
        List<(string, GridCell)> panes = [("a", new GridCell(0, 0, 2, 1))];

        DashboardGridMath.Drop(panes, "a", (0, 1), _Grid).Should().ContainSingle()
            .Which.Cell.Should().Be(new GridCell(0, 1, 2, 1));
    }

    [Fact]
    public void Drop_NeverStacksTwoPanesOnOneCell()
    {
        var panes = _Panes(("a", 0, 0), ("b", 1, 0), ("c", 0, 1));

        var arranged = DashboardGridMath.Drop(panes, "c", (1, 0), _Grid);

        _NoneOverlap(arranged).Should().BeTrue();
    }

    /// <summary>
    /// The rectangles are what may not collide, not the origins. Asserting on origins passed while every pane in
    /// the test was one cell — which is the whole trick: it read as "nothing is stacked" and meant "nothing has
    /// the same top-left". A pane spanning two columns sat on its neighbour with a different origin and the
    /// assertion had nothing to say about it.
    /// </summary>
    [Fact]
    public void Drop_AWidePane_OverTwoNarrowOnes_IsRefused_RatherThanLeavingOneUnderneath()
    {
        List<(string, GridCell)> panes = [("a", new GridCell(0, 0, 2, 1)), ("b", new GridCell(2, 0)), ("c", new GridCell(3, 0))];

        // Landing at column 2 covers both b and c. A swap has one answer, and giving it to b left c stacked
        // under a — persisted, and drawn one on top of the other ever after.
        DashboardGridMath.Drop(panes, "a", (2, 0), _Grid).Should().BeNull();
    }

    [Fact]
    public void Drop_PastTheLastColumn_IsRefused_SoAWidePaneCannotLeaveTheGrid()
    {
        List<(string, GridCell)> panes = [("a", new GridCell(0, 0, 2, 1))];

        // CellAt clamps the pointer to the last column, so a two-wide pane dropped against the right edge asks
        // for columns 3 and 4 of a four-column grid. Resize refuses exactly this; Drop used to write it down.
        DashboardGridMath.Drop(panes, "a", (_Grid.Columns - 1, 0), _Grid).Should().BeNull();
        DashboardGridMath.Drop(panes, "a", (_Grid.Columns - 2, 0), _Grid).Should().NotBeNull("flush with the edge still fits");
    }

    [Fact]
    public void Drop_PastTheLastRow_IsAllowed_BecauseRowsGrow()
    {
        var panes = _Panes(("a", 0, 0));

        DashboardGridMath.Drop(panes, "a", (0, _Grid.Rows + 2), _Grid).Should().NotBeNull("rows are a starting height, not a cap");
    }

    [Fact]
    public void Drop_ASwapTheOccupantCannotFit_IsRefused()
    {
        // b is two wide; sending it to a's origin at column 3 would reach past the grid. The swap has to be
        // refused as a whole — moving a and leaving b where it was is not a swap, it is a stack.
        List<(string, GridCell)> panes = [("a", new GridCell(3, 0)), ("b", new GridCell(0, 0, 2, 1))];

        DashboardGridMath.Drop(panes, "a", (0, 0), _Grid).Should().BeNull();
    }

    [Fact]
    public void Drop_ASwapThatWouldLandTheOccupantOnAThirdPane_IsRefused()
    {
        // Swapping a and b would put b (two wide) at a's origin, across c. The pane being traded with is not the
        // only one that has to fit.
        List<(string, GridCell)> panes = [("a", new GridCell(0, 0)), ("b", new GridCell(2, 0, 2, 1)), ("c", new GridCell(1, 0))];

        DashboardGridMath.Drop(panes, "a", (2, 0), _Grid).Should().BeNull();
    }

    [Fact]
    public void Drop_EveryArrangementItReturns_IsFreeOfOverlaps()
    {
        List<(string, GridCell)> panes = [("a", new GridCell(0, 0, 2, 1)), ("b", new GridCell(2, 0)), ("c", new GridCell(0, 1, 1, 2))];
        string[] ids = ["a", "b", "c"];

        // Whatever it accepts, it accepts wholly: every cell of the grid it hands back holds at most one pane.
        for (var column = 0; column < _Grid.Columns; column++)
        {
            for (var row = 0; row < _Grid.Rows; row++)
            {
                foreach (var id in ids)
                {
                    if (DashboardGridMath.Drop(panes, id, (column, row), _Grid) is { } arranged)
                    {
                        _NoneOverlap(arranged).Should().BeTrue($"dropping {id} on ({column},{row}) was accepted");
                        arranged.Should().OnlyContain(pane => pane.Cell.ColumnEnd <= _Grid.Columns, "and stays on the grid");
                    }
                }
            }
        }
    }

    private static bool _NoneOverlap(IReadOnlyList<(string Id, GridCell Cell)>? panes) =>
        _Accepted(panes).All(pane => _Accepted(panes).Where(other => other.Id != pane.Id).All(other => !other.Cell.Overlaps(pane.Cell)));

    /// <summary>The arrangement a drop came back with, or a failure that says the drop was refused — which reads better than a null-reference three frames down.</summary>
    private static IReadOnlyList<(string Id, GridCell Cell)> _Accepted(IReadOnlyList<(string Id, GridCell Cell)>? arranged) =>
        arranged ?? throw new InvalidOperationException("the drop was refused, so there is no arrangement to look at");

    [Theory]
    [InlineData(10, 10, 0, 0)]
    [InlineData(190, 10, 1, 0)]
    [InlineData(10, 190, 0, 1)]
    [InlineData(190, 190, 1, 1)]
    public void CellAt_MapsAPositionToItsCell(double x, double y, int column, int row)
    {
        DashboardGridMath.CellAt(x, y, 200, 200, columns: 2, rows: 2).Should().Be((column, row));
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(10, -1)]
    [InlineData(201, 10)]
    [InlineData(10, 201)]
    public void CellAt_OutsideTheGrid_IsNoCell_SoADragOffTheEdgeDropsNothing(double x, double y)
    {
        DashboardGridMath.CellAt(x, y, 200, 200, columns: 2, rows: 2).Should().BeNull();
    }

    [Fact]
    public void CellAt_ADegenerateGrid_IsNoCell_RatherThanDividingByZero()
    {
        DashboardGridMath.CellAt(10, 10, 200, 200, columns: 0, rows: 2).Should().BeNull();
        DashboardGridMath.CellAt(10, 10, 0, 200, columns: 2, rows: 2).Should().BeNull();
    }

    [Fact]
    public void CellAt_TheFarEdge_ClampsInsideTheGrid()
    {
        DashboardGridMath.CellAt(199.9, 199.9, 200, 200, columns: 2, rows: 2).Should().Be((1, 1));
    }

    [Fact]
    public void Resize_DraggingTheCornerOut_GrowsThePaneToThatCell()
    {
        var panes = _Panes(("a", 0, 0));

        DashboardGridMath.Resize(panes, "a", (2, 1), _Grid).Should().Be(new GridCell(0, 0, 3, 2));
    }

    [Fact]
    public void Resize_BackToItsOrigin_IsASingleCell()
    {
        List<(string, GridCell)> panes = [("a", new GridCell(0, 0, 4, 3))];

        DashboardGridMath.Resize(panes, "a", (0, 0), _Grid).Should().Be(new GridCell(0, 0, 1, 1));
    }

    [Fact]
    public void Resize_AboveOrLeftOfItsOwnOrigin_IsRefused_RatherThanInverting()
    {
        List<(string, GridCell)> panes = [("a", new GridCell(2, 2, 2, 2))];

        DashboardGridMath.Resize(panes, "a", (1, 2), _Grid).Should().BeNull();
        DashboardGridMath.Resize(panes, "a", (2, 1), _Grid).Should().BeNull();
    }

    [Fact]
    public void Resize_PastTheLastColumn_IsRefused_SoAPaneCannotLeaveTheGrid()
    {
        var panes = _Panes(("a", 0, 0));

        DashboardGridMath.Resize(panes, "a", (_Grid.Columns, 0), _Grid).Should().BeNull();
    }

    [Fact]
    public void Resize_OntoANeighbour_IsRefused_SoThePaneStopsAtTheObstacle()
    {
        var panes = _Panes(("a", 0, 0), ("b", 2, 0));

        DashboardGridMath.Resize(panes, "a", (1, 0), _Grid).Should().Be(new GridCell(0, 0, 2, 1), "up to the neighbour is fine");
        DashboardGridMath.Resize(panes, "a", (2, 0), _Grid).Should().BeNull("onto it is not");
    }

    [Fact]
    public void Resize_PastTheLastRow_IsAllowed_BecauseRowsGrow()
    {
        var panes = _Panes(("a", 0, 0));

        DashboardGridMath.Resize(panes, "a", (0, _Grid.Rows), _Grid)
            .Should().Be(new GridCell(0, 0, 1, _Grid.Rows + 1), "rows are a starting height, not a cap");
    }

    [Fact]
    public void Resize_AnUnknownPane_IsRefused()
    {
        DashboardGridMath.Resize(_Panes(("a", 0, 0)), "gone", (1, 1), _Grid).Should().BeNull();
    }

    private static readonly DashboardLayout _Grid = new() { Columns = 4, Rows = 4 };

    private static IReadOnlyList<(string Id, GridCell Cell)> _Panes(params (string Id, int Column, int Row)[] panes) =>
        [.. panes.Select(pane => (pane.Id, new GridCell(pane.Column, pane.Row)))];

    private static GridCell _CellOf(IReadOnlyList<(string Id, GridCell Cell)>? panes, string id) =>
        _Accepted(panes).First(pane => pane.Id == id).Cell;
}
