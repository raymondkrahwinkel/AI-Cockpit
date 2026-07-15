using Cockpit.Core.Workspaces;
using FluentAssertions;

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
        DashboardGridMath.PlaceNext([], TwoByTwo).Should().Be(new GridCell(0, 0));
    }

    [Fact]
    public void PlaceNext_FillsRowMajor_BeforeStartingANewRow()
    {
        var placed = new List<GridCell>();
        for (var i = 0; i < 4; i++)
        {
            placed.Add(DashboardGridMath.PlaceNext(placed, TwoByTwo));
        }

        placed.Should().Equal(
            new GridCell(0, 0), new GridCell(1, 0),
            new GridCell(0, 1), new GridCell(1, 1));
    }

    [Fact]
    public void PlaceNext_FifthWidgetInATwoByTwo_GrowsARowInsteadOfRefusing()
    {
        // Raymond's question. A hard 2x2 cap would leave "Add widget" silently doing nothing once the fourth
        // cell is taken; rows grow instead, columns stay fixed — that is what carries the "2x2" shape.
        List<GridCell> full = [new(0, 0), new(1, 0), new(0, 1), new(1, 1)];

        DashboardGridMath.PlaceNext(full, TwoByTwo).Should().Be(new GridCell(0, 2));
    }

    [Fact]
    public void PlaceNext_ReusesAHoleLeftByDragging_RatherThanAppendingAtTheBottom()
    {
        // Free placement with holes is the existing grid's behaviour; first-fit keeps it useful.
        List<GridCell> withHole = [new(0, 0), new(1, 1)];

        DashboardGridMath.PlaceNext(withHole, TwoByTwo).Should().Be(new GridCell(1, 0));
    }

    [Fact]
    public void PlaceNext_NeverOverlapsAWiderNeighbour()
    {
        List<GridCell> occupied = [new(0, 0, ColumnSpan: 2)];

        DashboardGridMath.PlaceNext(occupied, TwoByTwo).Should().Be(new GridCell(0, 1));
    }

    [Fact]
    public void PlaceNext_SpanWiderThanTheGrid_IsClampedToTheColumnCount()
    {
        DashboardGridMath.PlaceNext([], TwoByTwo, columnSpan: 5).Should().Be(new GridCell(0, 0, ColumnSpan: 2));
    }

    [Fact]
    public void PlaceNext_HonoursAMultiRowSpan()
    {
        DashboardGridMath.PlaceNext([], TwoByTwo, rowSpan: 2).Should().Be(new GridCell(0, 0, 1, 2));
    }

    [Fact]
    public void PlaceNext_ZeroSpans_AreClampedToASingleCell()
    {
        DashboardGridMath.PlaceNext([], TwoByTwo, columnSpan: 0, rowSpan: 0).Should().Be(new GridCell(0, 0));
    }

    [Fact]
    public void RequiredRows_EmptyDashboard_IsTheConfiguredHeight()
    {
        DashboardGridMath.RequiredRows([], TwoByTwo).Should().Be(2);
    }

    [Fact]
    public void RequiredRows_ContentPastTheConfiguredHeight_GrowsToFitIt()
    {
        List<GridCell> occupied = [new(0, 0), new(0, 2)];

        DashboardGridMath.RequiredRows(occupied, TwoByTwo).Should().Be(3);
    }

    [Fact]
    public void RequiredRows_ContentShorterThanTheConfiguredHeight_KeepsTheConfiguredHeight()
    {
        List<GridCell> occupied = [new(0, 0)];

        DashboardGridMath.RequiredRows(occupied, TwoByTwo).Should().Be(2);
    }
}
