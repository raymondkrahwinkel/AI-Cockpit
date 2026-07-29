using Cockpit.Core.Workspaces;

namespace Cockpit.Core.Tests.Workspaces;

/// <summary>
/// <see cref="DashboardGridMath"/> — where a newly added widget lands, and how tall the dashboard has to be.
/// The interesting case is the one Raymond asked about: what a "2x2" does with a fifth widget.
/// </summary>
public class DashboardGridMathTests
{
    private static readonly DashboardLayout TwoByTwo = new() { Columns = 2, Rows = 2 };

    [Fact]
    public void PlaceNext_EmptyDashboard_TakesTheTopLeftCell()
    {
        Assert.Equal(new GridCell(0, 0), DashboardGridMath.PlaceNext([], TwoByTwo));
    }

    [Fact]
    public void PlaceNext_FillsRowMajor_BeforeStartingANewRow()
    {
        var placed = new List<GridCell>();
        for (var i = 0; i < 4; i++)
        {
            placed.Add(DashboardGridMath.PlaceNext(placed, TwoByTwo));
        }

        Assert.Equal(
            new[]
            {
                new GridCell(0, 0), new GridCell(1, 0),
                new GridCell(0, 1), new GridCell(1, 1),
            },
            placed);
    }

    [Fact]
    public void PlaceNext_FifthWidgetInATwoByTwo_GrowsARowInsteadOfRefusing()
    {
        // Raymond's question. A hard 2x2 cap would leave "Add widget" silently doing nothing once the fourth
        // cell is taken; rows grow instead, columns stay fixed — that is what carries the "2x2" shape.
        List<GridCell> full = [new(0, 0), new(1, 0), new(0, 1), new(1, 1)];

        Assert.Equal(new GridCell(0, 2), DashboardGridMath.PlaceNext(full, TwoByTwo));
    }

    [Fact]
    public void PlaceNext_ReusesAHoleLeftByDragging_RatherThanAppendingAtTheBottom()
    {
        // Free placement with holes is the existing grid's behaviour; first-fit keeps it useful.
        List<GridCell> withHole = [new(0, 0), new(1, 1)];

        Assert.Equal(new GridCell(1, 0), DashboardGridMath.PlaceNext(withHole, TwoByTwo));
    }

    [Fact]
    public void PlaceNext_NeverOverlapsAWiderNeighbour()
    {
        List<GridCell> occupied = [new(0, 0, ColumnSpan: 2)];

        Assert.Equal(new GridCell(0, 1), DashboardGridMath.PlaceNext(occupied, TwoByTwo));
    }

    [Fact]
    public void PlaceNext_SpanWiderThanTheGrid_IsClampedToTheColumnCount()
    {
        Assert.Equal(new GridCell(0, 0, ColumnSpan: 2), DashboardGridMath.PlaceNext([], TwoByTwo, columnSpan: 5));
    }

    [Fact]
    public void PlaceNext_HonoursAMultiRowSpan()
    {
        Assert.Equal(new GridCell(0, 0, 1, 2), DashboardGridMath.PlaceNext([], TwoByTwo, rowSpan: 2));
    }

    [Fact]
    public void PlaceNext_ZeroSpans_AreClampedToASingleCell()
    {
        Assert.Equal(new GridCell(0, 0), DashboardGridMath.PlaceNext([], TwoByTwo, columnSpan: 0, rowSpan: 0));
    }

    [Fact]
    public void RequiredRows_EmptyDashboard_IsTheConfiguredHeight()
    {
        Assert.Equal(2, DashboardGridMath.RequiredRows([], TwoByTwo));
    }

    [Fact]
    public void RequiredRows_ContentPastTheConfiguredHeight_GrowsToFitIt()
    {
        List<GridCell> occupied = [new(0, 0), new(0, 2)];

        Assert.Equal(3, DashboardGridMath.RequiredRows(occupied, TwoByTwo));
    }

    [Fact]
    public void RequiredRows_ContentShorterThanTheConfiguredHeight_KeepsTheConfiguredHeight()
    {
        List<GridCell> occupied = [new(0, 0)];

        Assert.Equal(2, DashboardGridMath.RequiredRows(occupied, TwoByTwo));
    }
}
