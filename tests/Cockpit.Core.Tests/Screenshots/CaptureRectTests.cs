using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.Core.Tests.Screenshots;

/// <summary>
/// <see cref="CaptureRect.Contains"/> at its own edges (AC-567). The double-click that confirms a selection
/// trusts this at the exact pixels an operator's second click is likeliest to land on — the border — so this is
/// pinned down on its own rather than only ever exercised in passing by some other feature's test.
/// </summary>
public class CaptureRectTests
{
    private static readonly CaptureRect Region = new(10, 10, 20, 20);

    [Theory]
    [InlineData(10, 10)] // top-left corner: inside
    [InlineData(29, 10)] // top-right-most pixel still inside (Right is 30, exclusive)
    [InlineData(10, 29)] // bottom-left-most pixel still inside (Bottom is 30, exclusive)
    [InlineData(29, 29)] // bottom-right-most pixel still inside
    [InlineData(20, 20)] // dead centre
    public void APixelOnOrInsideTheNearEdge_IsContained(int x, int y) =>
        Assert.True(Region.Contains(new CapturePoint(x, y)));

    [Theory]
    [InlineData(9, 10)] // one pixel left of the left edge
    [InlineData(10, 9)] // one pixel above the top edge
    [InlineData(30, 10)] // Right itself: one past the last column, and therefore outside
    [InlineData(10, 30)] // Bottom itself: one past the last row
    [InlineData(30, 30)] // the far corner, one past both
    public void APixelOnOrPastTheFarEdge_IsNotContained(int x, int y) =>
        Assert.False(Region.Contains(new CapturePoint(x, y)));
}
