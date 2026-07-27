using FluentAssertions;
using SkiaSharp;
using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Infrastructure.Screenshots;

namespace Cockpit.Infrastructure.Tests.Screenshots;

/// <summary>
/// Burning an arrow into the picture (AC-360). Read off the decoded bytes rather than off the mark, because the
/// mark being right is a different claim from the picture being right — and only one of the two is sent.
/// </summary>
public class SkiaArrowTests
{
    /// <summary>Dark enough to be ringed in white, which is what makes the ring visible against a black picture.</summary>
    private const uint Blue = 0xFF0000FF;

    /// <summary>Heavy enough that the shaft and the ring are several rows each, so a count of rows can tell the head from the tail.</summary>
    private const int Thickness = 12;

    /// <summary>The arrow is in the bytes, along the line it was dragged and nowhere past either end of it.</summary>
    [Fact]
    public void AnArrow_IsInTheReturnedBytes_AlongTheLineItWasDragged()
    {
        using var image = _Burn(_Arrow(40, 100, 260, 100));

        image.GetPixel(150, 100).Should().Be(new SKColor(0, 0, 255), "the shaft runs through the middle");
        image.GetPixel(280, 100).Should().Be(SKColors.Black, "nothing is drawn past the point");
        image.GetPixel(20, 100).Should().Be(SKColors.Black, "and nothing behind the tail");
    }

    /// <summary>
    /// The head is at the end you dragged to. Measured as how deep the ink runs near each end rather than by
    /// looking for a shape: an arrow drawn at a fixed angle, or one drawn back to front, paints a picture too —
    /// it just paints it the wrong way round, and only the width at each end tells the two apart.
    /// </summary>
    [Fact]
    public void TheHeadIsAtTheEndYouDraggedTo_NotTheOneYouStartedFrom()
    {
        using var pointingRight = _Burn(_Arrow(40, 100, 260, 100));
        using var pointingLeft = _Burn(_Arrow(260, 100, 40, 100));

        var nearTheRightEnd = _InkDepth(pointingRight, 230);
        var sameColumnDrawnBackwards = _InkDepth(pointingLeft, 230);

        sameColumnDrawnBackwards.Should().BeGreaterThan(0, "the shaft still crosses that column either way round");
        nearTheRightEnd.Should().BeGreaterThan(
            sameColumnDrawnBackwards * 3 / 2,
            "the head is at the end that was dragged to, so that column is head one way round and shaft the other");
    }

    /// <summary>
    /// The ring is burnt in around the body. Without it a dark arrow on a dark terminal is a mark nobody can see,
    /// which is the same as no mark — and this is the one thing about the tool that a picture of a light desktop
    /// would never show going wrong.
    /// </summary>
    [Fact]
    public void TheRingIsBurntAroundTheBody_SoTheArrowSurvivesADarkBackground()
    {
        using var image = _Burn(_Arrow(40, 100, 260, 100));

        var besideTheShaft = image.GetPixel(150, 93);

        besideTheShaft.Red.Should().BeGreaterThan(200);
        besideTheShaft.Green.Should().BeGreaterThan(200);
        besideTheShaft.Blue.Should().BeGreaterThan(200);
    }

    /// <summary>A press that never moved has no direction to point in, so nothing is drawn and the picture is the one that came in.</summary>
    [Fact]
    public void AnArrowThatWentNowhere_LeavesThePictureAsItWas()
    {
        using var image = _Burn(_Arrow(150, 100, 150, 100));

        image.GetPixel(150, 100).Should().Be(SKColors.Black);
        _InkDepth(image, 150).Should().Be(0);
    }

    /// <summary>
    /// Arrows go on in the order they were placed, like every other mark: the one drawn last is the one on top
    /// where they cross.
    /// </summary>
    [Fact]
    public void WhereTwoArrowsCross_TheOneDrawnLastIsOnTop()
    {
        using var image = _Burn(
            new ArrowMark(new CapturePoint(40, 100), new CapturePoint(260, 100), Blue, Thickness),
            new ArrowMark(new CapturePoint(150, 20), new CapturePoint(150, 180), 0xFFFFFF00, Thickness));

        var whereTheyCross = image.GetPixel(150, 100);

        whereTheyCross.Red.Should().BeGreaterThan(200, "the second arrow is yellow and went on last");
        whereTheyCross.Blue.Should().BeLessThan(60);
    }

    private static ArrowMark _Arrow(int fromX, int fromY, int toX, int toY) =>
        new(new CapturePoint(fromX, fromY), new CapturePoint(toX, toY), Blue, Thickness);

    private static SKBitmap _Burn(params Mark[] marks) =>
        SKBitmap.Decode(new SkiaScreenshotImageEditor().Burn(_Filled(300, 200, SKColors.Black), marks));

    /// <summary>
    /// How many rows of that column the mark reaches, ring included. Anything that is no longer the background
    /// counts, so the measurement does not depend on which part of the arrow the column happens to cross.
    /// </summary>
    private static int _InkDepth(SKBitmap image, int x)
    {
        var depth = 0;
        for (var y = 0; y < image.Height; y++)
        {
            if (image.GetPixel(x, y) != SKColors.Black)
            {
                depth++;
            }
        }

        return depth;
    }

    private static byte[] _Filled(int width, int height, SKColor colour)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(colour);
        }

        return SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }
}
