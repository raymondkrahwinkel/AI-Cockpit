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

        var arranged = DashboardGridMath.Drop(panes, "a", (1, 1));

        _CellOf(arranged, "a").Should().Be(new GridCell(1, 1));
        _CellOf(arranged, "b").Should().Be(new GridCell(1, 0), "the pane that was not dragged does not move");
    }

    [Fact]
    public void Drop_OnAnOccupiedCell_SwapsTheTwo()
    {
        var panes = _Panes(("a", 0, 0), ("b", 1, 0));

        var arranged = DashboardGridMath.Drop(panes, "a", (1, 0));

        _CellOf(arranged, "a").Should().Be(new GridCell(1, 0));
        _CellOf(arranged, "b").Should().Be(new GridCell(0, 0));
    }

    [Fact]
    public void Drop_OnItsOwnCell_ChangesNothing()
    {
        var panes = _Panes(("a", 0, 0), ("b", 1, 0));

        DashboardGridMath.Drop(panes, "a", (0, 0)).Should().BeSameAs(panes);
    }

    [Fact]
    public void Drop_AnUnknownPane_ChangesNothing()
    {
        var panes = _Panes(("a", 0, 0));

        DashboardGridMath.Drop(panes, "gone", (1, 1)).Should().BeSameAs(panes);
    }

    [Fact]
    public void Drop_KeepsTheDraggedPanesSpan()
    {
        List<(string, GridCell)> panes = [("a", new GridCell(0, 0, 2, 1))];

        DashboardGridMath.Drop(panes, "a", (0, 1)).Should().ContainSingle()
            .Which.Cell.Should().Be(new GridCell(0, 1, 2, 1));
    }

    [Fact]
    public void Drop_NeverStacksTwoPanesOnOneCell()
    {
        var panes = _Panes(("a", 0, 0), ("b", 1, 0), ("c", 0, 1));

        var arranged = DashboardGridMath.Drop(panes, "c", (1, 0));

        arranged.Select(pane => (pane.Cell.Column, pane.Cell.Row)).Should().OnlyHaveUniqueItems();
    }

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

    private static IReadOnlyList<(string Id, GridCell Cell)> _Panes(params (string Id, int Column, int Row)[] panes) =>
        [.. panes.Select(pane => (pane.Id, new GridCell(pane.Column, pane.Row)))];

    private static GridCell _CellOf(IReadOnlyList<(string Id, GridCell Cell)> panes, string id) =>
        panes.First(pane => pane.Id == id).Cell;
}
