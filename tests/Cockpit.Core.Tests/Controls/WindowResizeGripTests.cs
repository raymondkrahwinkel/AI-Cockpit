using Avalonia;
using Avalonia.Controls;
using Cockpit.App.Controls;

namespace Cockpit.Core.Tests.Controls;

/// <summary>
/// AC-678: which of the eight resize zones (if any) a point falls in, on a window of a given size. Pure —
/// no window has to be shown to hold this shut.
/// </summary>
public class WindowResizeGripTests
{
    private static readonly Size WindowSize = new(400, 300);

    [Fact]
    public void APointWellInsideTheWindow_IsNotInAnyZone() =>
        Assert.Null(WindowResizeGrip.EdgeAt(WindowSize, new Point(200, 150)));

    [Theory]
    [InlineData(200, 0, WindowEdge.North)]
    [InlineData(200, 299, WindowEdge.South)]
    [InlineData(0, 150, WindowEdge.West)]
    [InlineData(399, 150, WindowEdge.East)]
    public void APointOnAPlainEdge_AwayFromEveryCorner_IsThatEdge(double x, double y, WindowEdge expected) =>
        Assert.Equal(expected, WindowResizeGrip.EdgeAt(WindowSize, new Point(x, y)));

    [Theory]
    [InlineData(0, 0, WindowEdge.NorthWest)]
    [InlineData(399, 0, WindowEdge.NorthEast)]
    [InlineData(0, 299, WindowEdge.SouthWest)]
    [InlineData(399, 299, WindowEdge.SouthEast)]
    public void APointInACorner_IsTheDiagonalEdge_NotThePlainOnesItOverlaps(double x, double y, WindowEdge expected) =>
        Assert.Equal(expected, WindowResizeGrip.EdgeAt(WindowSize, new Point(x, y)));

    [Fact]
    public void APointJustOutsideTheBand_IsNotInAnyZone()
    {
        // One DIP past the band on every side — the boundary itself (AtTheBandsOwnEdge_IsStillInTheZone below)
        // stays in the zone; this is the first point that must not be.
        Assert.Null(WindowResizeGrip.EdgeAt(WindowSize, new Point(WindowResizeGrip.BorderThickness + 1, 150), WindowResizeGrip.BorderThickness));
        Assert.Null(WindowResizeGrip.EdgeAt(WindowSize, new Point(200, WindowResizeGrip.BorderThickness + 1), WindowResizeGrip.BorderThickness));
    }

    [Fact]
    public void AtTheBandsOwnEdge_IsStillInTheZone()
    {
        Assert.Equal(WindowEdge.West, WindowResizeGrip.EdgeAt(WindowSize, new Point(WindowResizeGrip.BorderThickness, 150), WindowResizeGrip.BorderThickness));
        Assert.Equal(WindowEdge.North, WindowResizeGrip.EdgeAt(WindowSize, new Point(200, WindowResizeGrip.BorderThickness), WindowResizeGrip.BorderThickness));
    }

    [Fact]
    public void ANarrowerBand_ShrinksTheZoneWithIt()
    {
        // A custom thickness (rather than the default) still governs the calculation — the parameter is not
        // decorative.
        Assert.Null(WindowResizeGrip.EdgeAt(WindowSize, new Point(4, 150), thickness: 2));
        Assert.Equal(WindowEdge.West, WindowResizeGrip.EdgeAt(WindowSize, new Point(1, 150), thickness: 2));
    }
}
