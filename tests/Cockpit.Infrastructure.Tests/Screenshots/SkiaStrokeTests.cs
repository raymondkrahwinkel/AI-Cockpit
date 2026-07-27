using FluentAssertions;
using SkiaSharp;
using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Infrastructure.Screenshots;

namespace Cockpit.Infrastructure.Tests.Screenshots;

/// <summary>
/// Burning a freehand line into the picture (AC-362), read off the decoded bytes — the mark being right and the
/// picture being right are different claims, and only one of them is sent.
/// </summary>
public class SkiaStrokeTests
{
    private const uint Blue = 0xFF0000FF;
    private const int Thickness = 6;

    /// <summary>The line is in the bytes, where it was drawn and not where it was not.</summary>
    [Fact]
    public void AStroke_IsInTheReturnedBytes_WhereItWasDrawn()
    {
        using var image = _Burn(new StrokeMark(
            [new(40, 40), new(140, 40), new(240, 40)], Blue, Thickness));

        image.GetPixel(140, 40).Should().Be(new SKColor(0, 0, 255), "the line runs through there");
        image.GetPixel(140, 120).Should().Be(SKColors.Black, "and nowhere near there");
    }

    /// <summary>
    /// The ring is under the line rather than over it. Over it would cover the very thing it exists to make
    /// visible — which is the one way round a filled shape can be ringed and a line cannot.
    /// </summary>
    [Fact]
    public void TheRingIsUnderTheLine_NotOverIt()
    {
        using var image = _Burn(new StrokeMark([new(40, 60), new(240, 60)], Blue, Thickness));

        image.GetPixel(140, 60).Should().Be(new SKColor(0, 0, 255), "the middle of it is still the line's colour");

        var besideIt = image.GetPixel(140, 64);
        besideIt.Red.Should().BeGreaterThan(200);
        besideIt.Green.Should().BeGreaterThan(200);
    }

    /// <summary>
    /// The line is a curve through its points, not a chain of straight runs between them. Measured where the two
    /// differ most: midway between two samples on a ring, where the chord falls inside the curve by more than the
    /// line is wide. A polygon would leave that spot empty.
    /// </summary>
    [Fact]
    public void AStrokeThroughSpacedOutPoints_IsACurveAndNotAChainOfSegments()
    {
        const int radius = 100;
        var centre = new CapturePoint(150, 150);
        var ring = Enumerable.Range(0, 9)
            .Select(step => 2 * Math.PI * step / 8)
            .Select(angle => new CapturePoint(
                centre.X + (int)Math.Round(radius * Math.Cos(angle)),
                centre.Y + (int)Math.Round(radius * Math.Sin(angle))))
            .ToList();

        using var image = _Burn(new StrokeMark(ring, Blue, Thickness), 300, 300);

        // Halfway between two of the samples, on the ring itself. A straight run between them passes about eight
        // pixels inside this, which is further than the line and its own ring reach.
        var between = 2 * Math.PI / 16;
        var x = centre.X + (int)Math.Round(radius * Math.Cos(between));
        var y = centre.Y + (int)Math.Round(radius * Math.Sin(between));

        image.GetPixel(x, y).Should().NotBe(SKColors.Black, "the curve bulges out to where the hand went");
    }

    /// <summary>A press that never moved leaves the picture as it was — there is no gesture to draw.</summary>
    [Fact]
    public void AStrokeThatWentNowhere_LeavesThePictureAsItWas()
    {
        using var image = _Burn(new StrokeMark([new(150, 100)], Blue, Thickness));

        image.GetPixel(150, 100).Should().Be(SKColors.Black);
    }

    private static SKBitmap _Burn(Mark mark, int width = 300, int height = 200) =>
        SKBitmap.Decode(new SkiaScreenshotImageEditor().Burn(_Filled(width, height), [mark]));

    private static byte[] _Filled(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Black);
        }

        return SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }
}
