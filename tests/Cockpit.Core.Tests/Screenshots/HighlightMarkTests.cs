using FluentAssertions;
using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.Core.Tests.Screenshots;

/// <summary>
/// The wash itself (AC-361): how pale it is drawn, which way round, and what happens to it at the edge of the
/// crop. What it then does to the pixels is the editor's and is measured there.
/// </summary>
public class HighlightMarkTests
{
    private const uint Accent = 0xFF3B82F6;

    /// <summary>
    /// The colour is made pale before it is blended. At full strength a saturated ink stops being a wash over the
    /// text and becomes a coat of paint on it — which is the tool next to this one.
    /// </summary>
    [Fact]
    public void AWashThatDarkens_IsMixedTowardsWhite()
    {
        var wash = new HighlightMark(new CaptureRect(0, 0, 10, 10), Accent, HighlightBlend.Darken).Wash;

        _Red(wash).Should().BeGreaterThan(_Red(Accent));
        _Blue(wash).Should().BeGreaterThan(_Blue(Accent));
        wash.Should().NotBe(0xFFFFFFFF, "pale, not gone — a wash of nothing marks nothing");
    }

    /// <summary>
    /// And one that lifts is mixed the other way. Screened onto a dark background, a near-white ink would blow the
    /// band out to white and take the text with it; the same colour taken towards black lifts it just enough.
    /// </summary>
    [Fact]
    public void AWashThatLifts_IsMixedTowardsBlackInstead()
    {
        var wash = new HighlightMark(new CaptureRect(0, 0, 10, 10), Accent, HighlightBlend.Lighten).Wash;

        _Red(wash).Should().BeLessThan(_Red(Accent));
        _Blue(wash).Should().BeLessThan(_Blue(Accent));
        wash.Should().NotBe(0xFF000000, "still the colour, only quieter");
    }

    /// <summary>
    /// A wash shrinks to the crop, the way the box that hides does. It is an area rather than a shape: the part
    /// that falls outside the picture is not part of the picture, and what is left is still exactly the band the
    /// operator drew over what remains.
    /// </summary>
    [Fact]
    public void AWashRunningOffTheRegion_IsShrunkToWhatSurvives()
    {
        var clipped = new HighlightMark(new CaptureRect(450, 150, 200, 100), Accent, HighlightBlend.Darken)
            .ClipTo(new CaptureRect(100, 100, 500, 400));

        clipped.Should().BeOfType<HighlightMark>().Which
            .Area.Should().Be(new CaptureRect(350, 50, 150, 100), "only the part that is being sent is washed");
    }

    /// <summary>A band over something that is not being sent emphasises nothing, so it does not travel either.</summary>
    [Fact]
    public void AWashOutsideTheRegion_IsNotCarried()
    {
        new HighlightMark(new CaptureRect(700, 700, 50, 50), Accent, HighlightBlend.Darken)
            .ClipTo(new CaptureRect(0, 0, 500, 500))
            .Should().BeNull();
    }

    private static int _Red(uint colour) => (int)((colour >> 16) & 0xFF);

    private static int _Blue(uint colour) => (int)(colour & 0xFF);
}
