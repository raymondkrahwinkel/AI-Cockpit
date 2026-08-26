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

    /// <summary>
    /// AC-1113: one arriving row, then nothing but the follow's own corrections — the chain has to stop.
    /// </summary>
    [Fact]
    public void ARowArrivingThenNothingButOwnCorrections_StopsFollowing()
    {
        // Avalonia cuts a frame at 153 layout passes; each follow here would be one of them.
        var corrected = false;
        var follows = 0;

        // The row arriving: the extent grew, so this is a real change and the follow answers it.
        var real = TranscriptScrollAnchor.IsOwnCorrection(extentDelta: 240, viewportDelta: 0);
        Assert.False(real);
        corrected = false;
        follows++;

        // Every pass after it carries an offset delta and nothing else, which is the follow's own move coming
        // back at it. Before AC-1113 the handler answered all 200 of these.
        for (var pass = 0; pass < 200; pass++)
        {
            var own = TranscriptScrollAnchor.IsOwnCorrection(extentDelta: 0, viewportDelta: 0);
            Assert.True(own);

            if (TranscriptScrollAnchor.MayFollow(own, corrected))
            {
                corrected = true;
                follows++;
            }
        }

        Assert.Equal(2, follows);
    }

    [Fact]
    public void IsSettled_WhenTheTargetIsWhereTheViewportAlreadySits_IsTrue()
    {
        // A sub-pixel "correction" is still a layout invalidation, so the follow must not write it.
        Assert.True(TranscriptScrollAnchor.IsSettled(currentOffsetY: 3747.2, targetOffsetY: 3747.0));
        Assert.False(TranscriptScrollAnchor.IsSettled(currentOffsetY: 3747.0, targetOffsetY: 3760.0));
    }
}
