using FluentAssertions;
using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.Core.Tests.Screenshots;

/// <summary>
/// The arrow's shape (AC-360): where its head sits, which way it faces, how big that head is allowed to get, and
/// what happens to the whole thing at the edge of the crop.
/// </summary>
/// <remarks>
/// Held here rather than where it is drawn because two libraries draw this arrow — the surface's preview and the
/// imaging library that burns it in — and both take the shape from the mark. If the shape is right, the only way
/// the two can disagree is in the drawing, which is what the render harness is for.
/// </remarks>
public class ArrowMarkTests
{
    private const uint Accent = 0xFF3B82F6;
    private const int Thickness = 6;

    /// <summary>
    /// The point of the arrow is where the drag ended, not the middle of anything. This is the tool's one promise:
    /// you let go on the thing you mean.
    /// </summary>
    [Theory]
    [InlineData(400, 300)]
    [InlineData(100, 300)]
    [InlineData(250, 90)]
    [InlineData(250, 500)]
    [InlineData(370, 420)]
    public void TheHeadSitsWhereTheDragEnded(int toX, int toY)
    {
        var arrow = _Arrow(250, 300, toX, toY);

        arrow.Silhouette().Should().Contain(new MarkPoint(toX, toY));
    }

    /// <summary>
    /// The head turns with the drag. Drawn at a fixed angle it would still be an arrow, still fill, still pass any
    /// test that only asked whether something was painted — and would point the wrong way for every direction but
    /// one. So the barbs are checked against the direction rather than against a picture.
    /// </summary>
    [Fact]
    public void TheHeadTurnsWithTheDrag_RatherThanBeingDrawnAtAFixedAngle()
    {
        var rightwards = _Barbs(_Arrow(100, 100, 400, 100));
        var downwards = _Barbs(_Arrow(100, 100, 100, 400));

        rightwards.First.X.Should().BeApproximately(rightwards.Second.X, 0.001,
            "an arrow pointing along x has its barbs abreast of each other");
        rightwards.First.Y.Should().NotBeApproximately(rightwards.Second.Y, 1);

        downwards.First.Y.Should().BeApproximately(downwards.Second.Y, 0.001,
            "and one pointing along y has them abreast the other way");
        downwards.First.X.Should().NotBeApproximately(downwards.Second.X, 1);
    }

    /// <summary>
    /// A diagonal is the case a fixed angle passes by accident, so it is worth its own measurement: the barbs sit
    /// across the direction of travel, whatever that direction is.
    /// </summary>
    [Fact]
    public void OnADiagonal_TheBarbsSitSquareAcrossTheDirectionOfTravel()
    {
        var arrow = _Arrow(100, 100, 400, 400);
        var (first, second) = _Barbs(arrow);

        var acrossX = second.X - first.X;
        var acrossY = second.Y - first.Y;

        // The line between the barbs runs at a right angle to the shaft, so the two directions' dot product is
        // zero. Stated as arithmetic rather than as coordinates because the coordinates are the thing under test.
        ((acrossX * 300) + (acrossY * 300)).Should().BeApproximately(0, 0.001);
    }

    /// <summary>
    /// The whole arrow scales, never the head on its own. This is the ticket's "decide how the head scales" and
    /// the answer is that it does not get to: head and shaft keep one proportion at every length, so a long arrow
    /// and a short one are one drawing at two sizes.
    /// </summary>
    /// <remarks>
    /// Written as a ratio rather than as two measurements because the numbers are not the point and would have to
    /// be rewritten the moment the arrow is tuned. The proportion is the point: the first attempt at this grew the
    /// head with the length and left the shaft where it was, and rendered a triangle on the end of a thread.
    /// </remarks>
    [Fact]
    public void TheWholeArrowScales_SoTheHeadKeepsItsProportionToTheShaft()
    {
        var shortOne = _Arrow(100, 100, 400, 100);
        var longOne = _Arrow(100, 100, 1100, 100);

        longOne.Weight.Should().BeGreaterThan(shortOne.Weight, "a longer arrow is drawn heavier");
        (_HeadLengthOf(longOne) / longOne.Weight).Should().BeApproximately(
            _HeadLengthOf(shortOne) / shortOne.Weight, 0.001,
            "and its head grows by exactly as much, so the shape is unchanged");
    }

    /// <summary>
    /// A short arrow is drawn at the thickness it was given and no thinner. Purely proportional it would come to a
    /// couple of pixels and the mark would read as a scratch, which points at nothing.
    /// </summary>
    [Fact]
    public void AShortArrowIsDrawnNoThinnerThanTheThicknessItWasGiven()
    {
        _Arrow(100, 100, 140, 100).Weight.Should().Be(Thickness);
    }

    /// <summary>
    /// And a long one stops thickening. Left proportional, an arrow across a large capture ends up a bar lying
    /// over the very thing it was drawn to point at.
    /// </summary>
    [Fact]
    public void ALongArrowStopsThickening()
    {
        _Arrow(100, 100, 5100, 100).Weight.Should().Be(Thickness * 4);
    }

    /// <summary>
    /// Where the two rules disagree — a drag so short that the smallest useful head would be longer than the
    /// arrow — the arrow wins. Past that it stops being an arrow and becomes a triangle with a stub behind it.
    /// </summary>
    [Fact]
    public void OnADragTooShortForItsOwnHead_TheHeadIsCappedToPartOfTheArrow()
    {
        _HeadLengthOf(_Arrow(100, 100, 120, 100)).Should().BeApproximately(20 * 0.6, 0.001);
    }

    /// <summary>A press that never moved has no direction, so there is no arrow to draw and nothing to carry.</summary>
    [Fact]
    public void ADragThatWentNowhere_IsNotAnArrow()
    {
        var arrow = _Arrow(200, 200, 200, 200);

        arrow.Silhouette().Should().BeEmpty();
        arrow.Bounds().Should().BeNull();
        arrow.ClipTo(new CaptureRect(0, 0, 1000, 1000)).Should().BeNull();
    }

    /// <summary>
    /// Moved into the crop's space whole, like a frame and for the same reason: trimmed at the tip it would end in
    /// a flat cut where the operator drew a point, and trimmed at the tail it would start where they did not.
    /// </summary>
    [Fact]
    public void AnArrowRunningOffTheRegion_IsMovedWholeRatherThanTrimmed()
    {
        var clipped = _Arrow(150, 180, 700, 260).ClipTo(new CaptureRect(100, 100, 500, 400));

        clipped.Should().BeOfType<ArrowMark>().Which.Should().BeEquivalentTo(new
        {
            From = new CapturePoint(50, 80),
            To = new CapturePoint(600, 160),
        }, "both ends move by the same amount, so the arrow keeps its length and its direction");
    }

    /// <summary>An arrow that cannot reach the region points at something nobody is being sent.</summary>
    [Fact]
    public void AnArrowThatCannotReachTheRegion_IsNotCarried()
    {
        _Arrow(700, 700, 900, 900).ClipTo(new CaptureRect(0, 0, 500, 500)).Should().BeNull();
    }

    /// <summary>
    /// The ring is the other colour from the body, so that whatever the arrow lies on, one of the two contrasts
    /// with it. A screenshot has no single background — the same arrow crosses a terminal and a document.
    /// </summary>
    [Theory]
    [InlineData(0xFF3B82F6, 0xFFFFFFFFu)]
    [InlineData(0xFF000000, 0xFFFFFFFFu)]
    [InlineData(0xFFF4C150, 0xFF000000u)]
    [InlineData(0xFFFFFFFF, 0xFF000000u)]
    public void TheRingIsTheOppositeOfTheBody(uint body, uint expected)
    {
        new ArrowMark(new CapturePoint(0, 0), new CapturePoint(10, 10), body, Thickness)
            .Halo.Should().Be(expected);
    }

    /// <summary>
    /// Brightness is weighted the way an eye weights it. A saturated green and a saturated blue have the same
    /// arithmetic mean and are nowhere near equally bright, so an unweighted test would put a white ring around
    /// the brightest colour on the screen.
    /// </summary>
    [Fact]
    public void BrightnessIsWeighted_SoASaturatedGreenCountsAsLight()
    {
        var green = new ArrowMark(new CapturePoint(0, 0), new CapturePoint(10, 10), 0xFF00FF00, Thickness);
        var blue = new ArrowMark(new CapturePoint(0, 0), new CapturePoint(10, 10), 0xFF0000FF, Thickness);

        green.Halo.Should().Be(0xFF000000, "green carries most of what the eye reads as brightness");
        blue.Halo.Should().Be(0xFFFFFFFF, "and blue carries least of it");
    }

    /// <summary>
    /// The bounding box has to cover the ring as well as the body, or an arrow at the very edge of a crop would
    /// be dropped while half its ink was still in the picture.
    /// </summary>
    [Fact]
    public void TheBoundsCoverTheRingAndNotOnlyTheBody()
    {
        var arrow = _Arrow(100, 100, 400, 100);
        var bounds = arrow.Bounds()!.Value;
        var margin = arrow.HaloThickness / 2;

        bounds.X.Should().BeLessThanOrEqualTo((int)(arrow.Silhouette().Min(corner => corner.X) - margin));
        bounds.Bottom.Should().BeGreaterThanOrEqualTo((int)(arrow.Silhouette().Max(corner => corner.Y) + margin));
    }

    private static ArrowMark _Arrow(int fromX, int fromY, int toX, int toY) =>
        new(new CapturePoint(fromX, fromY), new CapturePoint(toX, toY), Accent, Thickness);

    /// <summary>The two outer corners of the head — third and fifth of the seven, either side of the tip.</summary>
    private static (MarkPoint First, MarkPoint Second) _Barbs(ArrowMark arrow)
    {
        var corners = arrow.Silhouette();

        return (corners[2], corners[4]);
    }

    /// <summary>
    /// How far the head reaches back from the tip. The shoulders either side of the shaft straddle the point the
    /// head begins at, so their midpoint is that point.
    /// </summary>
    private static double _HeadLengthOf(ArrowMark arrow)
    {
        var corners = arrow.Silhouette();
        var neckX = (corners[1].X + corners[5].X) / 2;
        var neckY = (corners[1].Y + corners[5].Y) / 2;

        return Math.Sqrt(Math.Pow(arrow.To.X - neckX, 2) + Math.Pow(arrow.To.Y - neckY, 2));
    }
}
