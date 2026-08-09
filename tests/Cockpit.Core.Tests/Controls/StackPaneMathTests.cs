using Cockpit.App.Controls;

namespace Cockpit.Core.Tests.Controls;

/// <summary>
/// The pure geometry behind the resizable/reorderable vertical stack: proportional layout with gutters,
/// gutter hit-testing, min-clamped splitter drags, and reorder drop-index selection — all exercised
/// without a visual tree.
/// </summary>
public class StackPaneMathTests
{
    [Fact]
    public void Layout_EqualWeights_SplitsHeightMinusGutters()
    {
        var slots = StackPaneMath.Layout([1, 1, 1], totalHeight: 320, gutter: 10);

        // 320 - 2*10 gutters = 300 content / 3 = 100 each.
        Assert.Equal(3, System.Linq.Enumerable.Count(slots));
        Assert.Equal(new StackPaneMath.Slot(0, 100), slots[0]);
        Assert.Equal(new StackPaneMath.Slot(110, 100), slots[1]);
        Assert.Equal(new StackPaneMath.Slot(220, 100), slots[2]);
    }

    [Fact]
    public void Layout_UnequalWeights_SplitProportionally()
    {
        var slots = StackPaneMath.Layout([1, 3], totalHeight: 210, gutter: 10);

        // content = 200, split 1:3 -> 50 / 150.
        Assert.True(Math.Abs(slots[0].Height - 50) < 1e-9);
        Assert.True(Math.Abs(slots[1].Top - 60) < 1e-9);
        Assert.True(Math.Abs(slots[1].Height - 150) < 1e-9);
    }

    [Fact]
    public void Layout_EmptyOrTooShort_DoesNotThrowOrGoNegative()
    {
        Assert.Empty(StackPaneMath.Layout([], 100, 10));

        // Gutters alone exceed the height: fall back to equal, non-negative slices.
        var slots = StackPaneMath.Layout([1, 1], totalHeight: 5, gutter: 10);
        Assert.All(slots, s => Assert.True(s.Height >= 0));
    }

    [Theory]
    // Layout [1,1,1]/320/gutter 10 -> panes [0,100],[110,100],[220,100]; gutter centres 105 and 215,
    // grab band = gutter/2 + grab = 11 either side.
    [InlineData(105, 0)]   // dead on the first gutter centre
    [InlineData(100, 0)]   // within the grab band (105 ± 11)
    [InlineData(215, 1)]   // second gutter centre
    [InlineData(50, -1)]   // over pane content, not a gutter
    public void GutterAt_FindsTheBandUnderThePointer(double y, int expected)
    {
        var slots = StackPaneMath.Layout([1, 1, 1], totalHeight: 320, gutter: 10);

        Assert.Equal(expected, StackPaneMath.GutterAt(slots, y, gutter: 10, grab: 6));
    }

    [Fact]
    public void Resize_MovesHeightAcrossGutter_KeepingNeighboursTotal()
    {
        double[] weights = [1, 1, 1];
        // content height with 3 panes: pick 300 so each starts at 100px.
        var result = StackPaneMath.Resize(weights, gutterIndex: 0, pixelDelta: 30, contentHeight: 300, minPixels: 40);

        // Pair (0,1) shared 200px; upper grows to 130, lower shrinks to 70; pane 2 untouched.
        var sum = result[0] + result[1] + result[2];
        Assert.True(Math.Abs((300 * result[0] / sum) - 130) < 1e-6);
        Assert.True(Math.Abs((300 * result[1] / sum) - 70) < 1e-6);
        Assert.True(Math.Abs(result[2] - weights[2]) < 1e-9);
    }

    [Fact]
    public void Resize_ClampsSoAPaneCannotBeDraggedShut()
    {
        double[] weights = [1, 1];
        // Try to yank 500px across a 200px pair with a 40px minimum.
        var result = StackPaneMath.Resize(weights, gutterIndex: 0, pixelDelta: 500, contentHeight: 200, minPixels: 40);

        var sum = result[0] + result[1];
        Assert.True(Math.Abs((200 * result[1] / sum) - 40) < 1e-6); // lower pinned at the minimum
        Assert.True(Math.Abs((200 * result[0] / sum) - 160) < 1e-6);
    }

    [Fact]
    public void Resize_AsymmetricMinimums_ClampsEachSideToItsOwn()
    {
        double[] weights = [1, 1];

        // Pair shares 200px; dragging hard toward pane 0 must still leave pane 1 (min 80) its own minimum,
        // not pane 0's smaller one.
        var result = StackPaneMath.Resize(weights, gutterIndex: 0, pixelDelta: 500, contentHeight: 200, minPixelsA: 40, minPixelsB: 80);

        var sum = result[0] + result[1];
        Assert.True(Math.Abs((200 * result[1] / sum) - 80) < 1e-6);
        Assert.True(Math.Abs((200 * result[0] / sum) - 120) < 1e-6);
    }

    [Fact]
    public void Resize_AsymmetricMinimums_PairTooSmallForBoth_SplitsProportionally()
    {
        double[] weights = [1, 1];

        // Pair only has 60px total but the minimums (40 + 80) ask for 120 — split 1:2, the minimums' own ratio.
        var result = StackPaneMath.Resize(weights, gutterIndex: 0, pixelDelta: 0, contentHeight: 60, minPixelsA: 40, minPixelsB: 80);

        var sum = result[0] + result[1];
        Assert.True(Math.Abs((60 * result[0] / sum) - 20) < 1e-6);
        Assert.True(Math.Abs((60 * result[1] / sum) - 40) < 1e-6);
    }

    [Theory]
    [InlineData(50, 0)]    // inside the first slot
    [InlineData(150, 1)]   // inside the second slot
    [InlineData(295, 2)]   // inside the last slot
    [InlineData(-10, 0)]   // before the first clamps to 0
    [InlineData(999, 2)]   // past the last clamps to the last
    public void SlotAt_FindsTheCellUnderThePointer(double pos, int expected)
    {
        var slots = StackPaneMath.Layout([1, 1, 1], totalHeight: 300, gutter: 0); // 100 each

        Assert.Equal(expected, StackPaneMath.SlotAt(slots, pos));
    }

    [Fact]
    public void SlotAt_EmptyIsZero() => Assert.Equal(0, StackPaneMath.SlotAt([], 42));

    [Fact]
    public void ReorderTarget_DraggingDownPastANeighbourCentre_MovesAfterIt()
    {
        var slots = StackPaneMath.Layout([1, 1, 1], totalHeight: 300, gutter: 0); // 100 each

        // Dragging pane 0 down to y=160 -> past pane 1's centre (150), not past pane 2's (250) -> index 1.
        Assert.Equal(1, StackPaneMath.ReorderTarget(slots, draggedIndex: 0, pointerY: 160));
        // Down to the bottom -> last slot.
        Assert.Equal(2, StackPaneMath.ReorderTarget(slots, draggedIndex: 0, pointerY: 295));
        // Held in place -> unchanged.
        Assert.Equal(1, StackPaneMath.ReorderTarget(slots, draggedIndex: 1, pointerY: 120));
    }
}
