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

        Assert.Contains(new MarkPoint(toX, toY), arrow.Silhouette());
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

        Assert.True(Math.Abs(rightwards.First.X - rightwards.Second.X) <= 0.001,
            "an arrow pointing along x has its barbs abreast of each other");
        Assert.True(Math.Abs(rightwards.First.Y - rightwards.Second.Y) > 1);

        Assert.True(Math.Abs(downwards.First.Y - downwards.Second.Y) <= 0.001,
            "and one pointing along y has them abreast the other way");
        Assert.True(Math.Abs(downwards.First.X - downwards.Second.X) > 1);
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
        Assert.True(Math.Abs((acrossX * 300) + (acrossY * 300)) <= 0.001);
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

        Assert.True(longOne.Weight > shortOne.Weight, "a longer arrow is drawn heavier");
        Assert.True(
            Math.Abs((_HeadLengthOf(longOne) / longOne.Weight) - (_HeadLengthOf(shortOne) / shortOne.Weight)) <= 0.001,
            "and its head grows by exactly as much, so the shape is unchanged");
    }

    /// <summary>
    /// A short arrow is drawn at the thickness it was given and no thinner. Purely proportional it would come to a
    /// couple of pixels and the mark would read as a scratch, which points at nothing.
    /// </summary>
    [Fact]
    public void AShortArrowIsDrawnNoThinnerThanTheThicknessItWasGiven()
    {
        Assert.Equal(Thickness, _Arrow(100, 100, 140, 100).Weight);
    }

    /// <summary>
    /// And a long one stops thickening. Left proportional, an arrow across a large capture ends up a bar lying
    /// over the very thing it was drawn to point at.
    /// </summary>
    [Fact]
    public void ALongArrowStopsThickening()
    {
        Assert.Equal(Thickness * 4, _Arrow(100, 100, 5100, 100).Weight);
    }

    /// <summary>
    /// Where the two rules disagree — a drag so short that the smallest useful head would be longer than the
    /// arrow — the arrow wins. Past that it stops being an arrow and becomes a triangle with a stub behind it.
    /// </summary>
    [Fact]
    public void OnADragTooShortForItsOwnHead_TheHeadIsCappedToPartOfTheArrow()
    {
        Assert.True(Math.Abs(_HeadLengthOf(_Arrow(100, 100, 120, 100)) - (20 * 0.6)) <= 0.001);
    }

    /// <summary>A press that never moved has no direction, so there is no arrow to draw and nothing to carry.</summary>
    [Fact]
    public void ADragThatWentNowhere_IsNotAnArrow()
    {
        var arrow = _Arrow(200, 200, 200, 200);

        Assert.Empty(arrow.Silhouette());
        Assert.Null(arrow.Bounds());
        Assert.Null(arrow.ClipTo(new CaptureRect(0, 0, 1000, 1000)));
    }

    /// <summary>
    /// Moved into the crop's space whole, like a frame and for the same reason: trimmed at the tip it would end in
    /// a flat cut where the operator drew a point, and trimmed at the tail it would start where they did not.
    /// </summary>
    [Fact]
    public void AnArrowRunningOffTheRegion_IsMovedWholeRatherThanTrimmed()
    {
        var clipped = _Arrow(150, 180, 700, 260).ClipTo(new CaptureRect(100, 100, 500, 400));

        var arrowClipped = Assert.IsType<ArrowMark>(clipped);
        Assert.Equivalent(new
        {
            From = new CapturePoint(50, 80),
            To = new CapturePoint(600, 160),
        }, arrowClipped);
    }

    /// <summary>An arrow that cannot reach the region points at something nobody is being sent.</summary>
    [Fact]
    public void AnArrowThatCannotReachTheRegion_IsNotCarried()
    {
        Assert.Null(_Arrow(700, 700, 900, 900).ClipTo(new CaptureRect(0, 0, 500, 500)));
    }

    /// <summary>
    /// The bounding box covers every corner of the shape, or an arrow at the very edge of a crop would be dropped
    /// while its ink was still in the picture.
    /// </summary>
    [Fact]
    public void TheBoundsCoverTheWholeShape()
    {
        var arrow = _Arrow(100, 100, 400, 100);
        var bounds = arrow.Bounds()!.Value;

        Assert.True(bounds.X <= (int)arrow.Silhouette().Min(corner => corner.X));
        Assert.True(bounds.Bottom >= (int)arrow.Silhouette().Max(corner => corner.Y));
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
