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

    // The whole zone map on a 400x300 window at the default band: the middle belongs to nobody, each plain edge
    // away from a corner is that edge, and a corner is the diagonal rather than either plain edge it overlaps.
    [Theory]
    [InlineData(200, 150, null)]
    [InlineData(200, 0, WindowEdge.North)]
    [InlineData(200, 299, WindowEdge.South)]
    [InlineData(0, 150, WindowEdge.West)]
    [InlineData(399, 150, WindowEdge.East)]
    [InlineData(0, 0, WindowEdge.NorthWest)]
    [InlineData(399, 0, WindowEdge.NorthEast)]
    [InlineData(0, 299, WindowEdge.SouthWest)]
    [InlineData(399, 299, WindowEdge.SouthEast)]
    public void EdgeAt_MapsEachPointToItsZone_WithCornersBeatingThePlainEdgesTheyOverlap(double x, double y, WindowEdge? expected) =>
        Assert.Equal(expected, WindowResizeGrip.EdgeAt(WindowSize, new Point(x, y)));

    // Where the band ends. The boundary itself is still in the zone and one DIP past it is not — and a custom
    // thickness governs the same calculation, so the parameter is not decorative.
    [Theory]
    [InlineData(WindowResizeGrip.BorderThickness, 150, WindowResizeGrip.BorderThickness, WindowEdge.West)]
    [InlineData(200, WindowResizeGrip.BorderThickness, WindowResizeGrip.BorderThickness, WindowEdge.North)]
    [InlineData(WindowResizeGrip.BorderThickness + 1, 150, WindowResizeGrip.BorderThickness, null)]
    [InlineData(200, WindowResizeGrip.BorderThickness + 1, WindowResizeGrip.BorderThickness, null)]
    [InlineData(1, 150, 2, WindowEdge.West)]
    [InlineData(4, 150, 2, null)]
    public void EdgeAt_TheBandReachesItsOwnEdgeAndNoFurther(double x, double y, double thickness, WindowEdge? expected) =>
        Assert.Equal(expected, WindowResizeGrip.EdgeAt(WindowSize, new Point(x, y), thickness));

    // AC-755: on macOS None leaves NSWindowStyleMaskResizable off and BeginResizeDrag does nothing. AC-934: on
    // Windows None strips WS_CAPTION/WS_THICKFRAME/WS_MAXIMIZEBOX, which Aero Snap needs. Same trade both times;
    // Linux alone wears no OS decoration at all.
    [Theory]
    [InlineData(true, false, WindowDecorations.BorderOnly)]
    [InlineData(false, true, WindowDecorations.BorderOnly)]
    [InlineData(false, false, WindowDecorations.None)]
    public void DecorationsFor_KeepsThePlatformsOwnResizeBorder_WhereOursCannotReplaceIt(
        bool isMacOs, bool isWindows, WindowDecorations expected) =>
        Assert.Equal(expected, WindowResizeGrip.DecorationsFor(isMacOs, isWindows));
}
