using FluentAssertions;
using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.Core.Tests.Screenshots;

/// <summary>
/// The freehand line (AC-362): which of the pointer's positions are worth keeping, what shape they make, and what
/// happens to the whole of it at the edge of the crop.
/// </summary>
public class StrokeMarkTests
{
    private const uint Accent = 0xFF3B82F6;
    private const int Thickness = 6;

    /// <summary>
    /// Positions too close together say nothing about the gesture and a great deal about how the hand shook and
    /// how often the window was asked where the pointer was.
    /// </summary>
    [Fact]
    public void PositionsTooCloseTogether_AreThinnedAway()
    {
        var crawl = Enumerable.Range(0, 40).Select(step => new CapturePoint(100 + step, 100)).ToList();

        var kept = _Stroke(crawl).Thinned();

        kept.Should().HaveCountLessThan(crawl.Count / 2, "a pixel at a time is the sampling, not the gesture");
        kept[0].Should().Be(new CapturePoint(100, 100), "a stroke starts where the operator put the pen down");
        kept[^1].Should().Be(new CapturePoint(139, 100), "and ends where they lifted it");
    }

    /// <summary>
    /// The line bends through the points rather than between them. Joined with straight segments, a circle drawn
    /// quickly comes out a polygon — the samples are tens of pixels apart at speed, and that is visible.
    /// </summary>
    [Fact]
    public void TheLineBendsThroughItsPoints_RatherThanRunningStraightBetweenThem()
    {
        var corner = _Stroke([new(0, 0), new(100, 0), new(100, 100)]);

        var first = corner.Curve()[0];

        // On a straight run from (0,0) to (100,0) every control point sits on that line. The turn coming up pulls
        // this one well off it, which is what makes the corner a bend rather than a hinge. Measured as a distance
        // rather than as "not zero": a curve that has been flattened to a straight line still misses zero by a
        // rounding error, and an assertion that accepts that accepts a straight line.
        Math.Abs(first.SecondControl.Y).Should().BeGreaterThan(5);
    }

    /// <summary>A press that never moved is not a gesture, so there is nothing to draw and nothing to carry.</summary>
    [Fact]
    public void AStrokeThatWentNowhere_IsNotAMark()
    {
        var still = _Stroke([new(50, 50)]);

        still.Curve().Should().BeEmpty();
        still.Start().Should().BeNull();
        still.Bounds().Should().BeNull();
        still.ClipTo(new CaptureRect(0, 0, 500, 500)).Should().BeNull();
    }

    /// <summary>
    /// Moved into the crop's space whole, like the arrow and the frame. Trimmed at the edge a stroke would end
    /// where the operator did not lift their hand, and the ends of a gesture are most of what it says — a ring
    /// cut open is not a ring.
    /// </summary>
    [Fact]
    public void AStrokeRunningOffTheRegion_IsMovedWholeRatherThanTrimmed()
    {
        var clipped = _Stroke([new(150, 180), new(400, 260), new(700, 300)])
            .ClipTo(new CaptureRect(100, 100, 500, 400));

        clipped.Should().BeOfType<StrokeMark>().Which
            .Points.Should().Equal(
                new CapturePoint(50, 80), new CapturePoint(300, 160), new CapturePoint(600, 200));
    }

    /// <summary>A line drawn over something that is not being sent points at nothing, so it does not travel either.</summary>
    [Fact]
    public void AStrokeThatCannotReachTheRegion_IsNotCarried()
    {
        _Stroke([new(700, 700), new(800, 800)])
            .ClipTo(new CaptureRect(0, 0, 500, 500))
            .Should().BeNull();
    }

    /// <summary>The ring is the other colour from the line, so whatever the line crosses, one of the two contrasts with it.</summary>
    [Theory]
    [InlineData(0xFF3B82F6, 0xFFFFFFFFu)]
    [InlineData(0xFFF4C150, 0xFF000000u)]
    public void TheRingIsTheOppositeOfTheLine(uint colour, uint expected)
    {
        new StrokeMark([new(0, 0), new(50, 50)], colour, Thickness).Halo.Should().Be(expected);
    }

    /// <summary>
    /// The ring is wide enough to be seen once the capture has been scaled down onto a window. A couple of pixels
    /// wider than the line puts well under a pixel outside it, and a ring nobody can see is a ring that is not
    /// there — which was true of the first version of this and only showed up in a render.
    /// </summary>
    [Fact]
    public void TheRingIsWideEnoughToShowOutsideTheLine()
    {
        var stroke = new StrokeMark([new(0, 0), new(50, 50)], Accent, Thickness);

        ((stroke.HaloThickness - stroke.Thickness) / 2).Should().BeGreaterThanOrEqualTo(
            Thickness / 2.0, "half a line's width shows on either side");
    }

    private static StrokeMark _Stroke(IReadOnlyList<CapturePoint> points) => new(points, Accent, Thickness);
}
