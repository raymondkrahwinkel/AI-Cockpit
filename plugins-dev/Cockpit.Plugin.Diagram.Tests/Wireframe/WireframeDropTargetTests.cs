using Avalonia;
using Cockpit.Plugin.Diagram.Wireframe;

namespace Cockpit.Plugin.Diagram.Tests.Wireframe;

// AC-904's arithmetic check: the gap a drop lands in, counted the way a move's `position` counts children. An
// off-by-one here drops a component past the wrong neighbour, which is the whole reason this is not inline.
public class WireframeDropTargetTests
{
    private static readonly Rect Column = new(0, 0, 100, 300);
    private static readonly Rect Row = new(0, 0, 300, 100);

    private static readonly Rect[] Stacked =
        [new(0, 0, 100, 100), new(0, 100, 100, 100), new(0, 200, 100, 100)];

    private static readonly Rect[] SideBySide =
        [new(0, 0, 100, 100), new(100, 0, 100, 100), new(200, 0, 100, 100)];

    [Theory]
    [InlineData(10, 0)]
    [InlineData(120, 1)]
    [InlineData(220, 2)]
    [InlineData(290, 3)]
    public void InAColumn_ThePointerLandsBeforeEveryChildItHasNotPassedTheCentreOf(double y, int expected)
    {
        Assert.Equal(expected, WireframeDropTarget.Resolve(Stacked, Column, new Point(50, y)).Index);
    }

    [Theory]
    [InlineData(10, 0)]
    [InlineData(120, 1)]
    [InlineData(290, 3)]
    public void InARow_TheSameCountRunsSideways(double x, int expected)
    {
        Assert.Equal(expected, WireframeDropTarget.Resolve(SideBySide, Row, new Point(x, 50)).Index);
    }

    // With one child there are no two centres to read the axis off, so it comes from the child's share of the
    // container: a column's child spans the full width, a row's the full height.
    [Theory]
    [InlineData(20, 0)]
    [InlineData(80, 1)]
    public void WithASingleChild_AColumnStillSplitsTopFromBottom(double y, int expected)
    {
        Assert.Equal(expected, WireframeDropTarget.Resolve([new Rect(0, 0, 100, 100)], Column, new Point(50, y)).Index);
    }

    [Theory]
    [InlineData(20, 0)]
    [InlineData(80, 1)]
    public void WithASingleChild_ARowStillSplitsLeftFromRight(double x, int expected)
    {
        Assert.Equal(expected, WireframeDropTarget.Resolve([new Rect(0, 0, 100, 100)], Row, new Point(x, 50)).Index);
    }

    [Fact]
    public void TheLineBetweenTwoChildren_LiesOnTheirSharedEdge_AcrossTheWholeContainer()
    {
        var line = WireframeDropTarget.Resolve(Stacked, Column, new Point(50, 120)).Line;

        Assert.Equal(100, line.Center.Y);
        Assert.Equal(Column.X, line.X);
        Assert.Equal(Column.Width, line.Width);
    }

    // Inserting first or last is drawn just inside the container, so the indicator never hangs half outside it.
    [Theory]
    [InlineData(10)]
    [InlineData(290)]
    public void TheLineAtEitherEnd_StaysInsideTheContainer(double y)
    {
        var line = WireframeDropTarget.Resolve(Stacked, Column, new Point(50, y)).Line;

        Assert.True(line.Y >= Column.Y, $"line at {line.Y} starts above the container");
        Assert.True(line.Bottom <= Column.Bottom, $"line at {line.Bottom} runs past the container");
    }
}
