using Cockpit.App.Views;

namespace Cockpit.Core.Tests.Views;

public class TranscriptScrollAnchorTests
{
    // Extent 1000 against a 300 viewport leaves a maximum offset of 700. Exactly there is the bottom, and so is
    // one pixel short of it (sub-pixel layout rounding). Scrolled up is not, and content that fits in the viewport
    // always is, so new rows keep following.
    [Theory]
    [InlineData(700, 1000, 300, true)]
    [InlineData(699, 1000, 300, true)]
    [InlineData(400, 1000, 300, false)]
    [InlineData(0, 200, 300, true)]
    public void IsAtBottom_CountsTheBottomAndTheLastPixelShortOfIt(
        double offsetY, double extentHeight, double viewportHeight, bool expected)
    {
        Assert.Equal(expected, TranscriptScrollAnchor.IsAtBottom(offsetY, extentHeight, viewportHeight));
    }

    [Fact]
    public void IsSettled_WhenTheTargetIsWhereTheViewportAlreadySits_IsTrue()
    {
        // A sub-pixel "correction" is still a layout invalidation, so the follow must not write it.
        Assert.True(TranscriptScrollAnchor.IsSettled(currentOffsetY: 3747.2, targetOffsetY: 3747.0));
        Assert.False(TranscriptScrollAnchor.IsSettled(currentOffsetY: 3747.0, targetOffsetY: 3760.0));
    }
}
