using Cockpit.App.Views;

namespace Cockpit.Core.Tests.Views;

public class TranscriptScrollAnchorTests
{
    [Fact]
    public void IsAtBottom_WhenParkedAtTheBottom_IsTrue()
    {
        // extent 1000, viewport 300 -> max offset 700; offset exactly at 700.
        Assert.True(TranscriptScrollAnchor.IsAtBottom(offsetY: 700, extentHeight: 1000, viewportHeight: 300));
    }

    [Fact]
    public void IsAtBottom_WithinTolerance_IsTrue()
    {
        // 1px short of the bottom still counts as the bottom (sub-pixel layout rounding).
        Assert.True(TranscriptScrollAnchor.IsAtBottom(offsetY: 699, extentHeight: 1000, viewportHeight: 300));
    }

    [Fact]
    public void IsAtBottom_WhenScrolledUp_IsFalse()
    {
        Assert.False(TranscriptScrollAnchor.IsAtBottom(offsetY: 400, extentHeight: 1000, viewportHeight: 300));
    }

    [Fact]
    public void IsAtBottom_WhenContentFitsInTheViewport_IsTrue()
    {
        // Nothing to scroll (extent <= viewport): always counts as the bottom so new rows keep following.
        Assert.True(TranscriptScrollAnchor.IsAtBottom(offsetY: 0, extentHeight: 200, viewportHeight: 300));
    }

    [Fact]
    public void IsSettled_WhenTheTargetIsWhereTheViewportAlreadySits_IsTrue()
    {
        // A sub-pixel "correction" is still a layout invalidation, so the follow must not write it.
        Assert.True(TranscriptScrollAnchor.IsSettled(currentOffsetY: 3747.2, targetOffsetY: 3747.0));
        Assert.False(TranscriptScrollAnchor.IsSettled(currentOffsetY: 3747.0, targetOffsetY: 3760.0));
    }
}
