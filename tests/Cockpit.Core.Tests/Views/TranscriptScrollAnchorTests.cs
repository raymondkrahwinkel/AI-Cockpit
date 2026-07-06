using Cockpit.App.Views;
using FluentAssertions;

namespace Cockpit.Core.Tests.Views;

public class TranscriptScrollAnchorTests
{
    [Fact]
    public void IsAtBottom_WhenParkedAtTheBottom_IsTrue()
    {
        // extent 1000, viewport 300 -> max offset 700; offset exactly at 700.
        TranscriptScrollAnchor.IsAtBottom(offsetY: 700, extentHeight: 1000, viewportHeight: 300).Should().BeTrue();
    }

    [Fact]
    public void IsAtBottom_WithinTolerance_IsTrue()
    {
        // 1px short of the bottom still counts as the bottom (sub-pixel layout rounding).
        TranscriptScrollAnchor.IsAtBottom(offsetY: 699, extentHeight: 1000, viewportHeight: 300).Should().BeTrue();
    }

    [Fact]
    public void IsAtBottom_WhenScrolledUp_IsFalse()
    {
        TranscriptScrollAnchor.IsAtBottom(offsetY: 400, extentHeight: 1000, viewportHeight: 300).Should().BeFalse();
    }

    [Fact]
    public void IsAtBottom_WhenContentFitsInTheViewport_IsTrue()
    {
        // Nothing to scroll (extent <= viewport): always counts as the bottom so new rows keep following.
        TranscriptScrollAnchor.IsAtBottom(offsetY: 0, extentHeight: 200, viewportHeight: 300).Should().BeTrue();
    }
}
