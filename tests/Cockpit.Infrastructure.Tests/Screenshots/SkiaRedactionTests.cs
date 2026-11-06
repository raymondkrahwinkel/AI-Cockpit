using SkiaSharp;
using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Infrastructure.Screenshots;

namespace Cockpit.Infrastructure.Tests.Screenshots;

/// <summary>
/// Redaction (AC-331), asserted on the pixels that come out. This is the one operation in the epic where a test
/// that checked intent instead of bytes would be worthless: the whole promise is that what was under the box is
/// not in the file any more, and a redaction that is merely *requested* keeps every secret it was asked to hide.
/// </summary>
public class SkiaRedactionTests
{
    /// <summary>
    /// The source is a checkerboard of single pixels — the highest frequency an image can carry, and the closest
    /// thing to text a test can build without a font. Nothing inside the box may survive it: every original
    /// pixel was pure black or pure white, so a single one of either left standing is original data.
    /// </summary>
    [Fact]
    public void NoOriginalPixelSurvivesInsideARedactedBox()
    {
        var png = _Checkerboard(128, 128);

        var redacted = new SkiaScreenshotImageEditor().Burn(png, [new RedactionMark(new CaptureRect(32, 32, 64, 64))]);

        using var image = SKBitmap.Decode(redacted);
        for (var y = 32; y < 96; y++)
        {
            for (var x = 32; x < 96; x++)
            {
                var pixel = image.GetPixel(x, y);
                Assert.NotEqual(SKColors.Black, pixel);
                Assert.NotEqual(SKColors.White, pixel);
            }
        }
    }


    /// <summary>Several boxes, several secrets. Each is obscured on its own rather than only the first.</summary>
    [Fact]
    public void EveryBoxIsRedacted_NotOnlyTheFirst()
    {
        var png = _Checkerboard(128, 128);

        var redacted = new SkiaScreenshotImageEditor().Burn(
            png,
            [new RedactionMark(new CaptureRect(0, 0, 32, 32)), new RedactionMark(new CaptureRect(96, 96, 32, 32))]);

        using var image = SKBitmap.Decode(redacted);
        Assert.NotEqual(SKColors.Black, image.GetPixel(5, 5));
        Assert.NotEqual(SKColors.White, image.GetPixel(5, 5));
        Assert.NotEqual(SKColors.Black, image.GetPixel(120, 120));
        Assert.NotEqual(SKColors.White, image.GetPixel(120, 120));
    }


    /// <summary>A box running off the edge is clamped rather than throwing: the surface can be dragged past the image, and losing the whole redaction over it is the dangerous way to fail.</summary>
    [Fact]
    public void ABoxRunningPastTheEdge_StillRedactsWhatIsThere()
    {
        var png = _Checkerboard(64, 64);

        var redacted = new SkiaScreenshotImageEditor().Burn(png, [new RedactionMark(new CaptureRect(32, 32, 100, 100))]);

        using var image = SKBitmap.Decode(redacted);
        Assert.NotEqual(SKColors.Black, image.GetPixel(50, 50));
        Assert.NotEqual(SKColors.White, image.GetPixel(50, 50));
    }

    private static byte[] _Checkerboard(int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                bitmap.SetPixel(x, y, (x + y) % 2 == 0 ? SKColors.Black : SKColors.White);
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);

        return encoded.ToArray();
    }
}
