using FluentAssertions;
using SkiaSharp;
using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Infrastructure.Screenshots;

namespace Cockpit.Infrastructure.Tests.Screenshots;

/// <summary>
/// The crop (AC-329), on the pixels rather than on the intent. A crop that took the wrong region hands back a
/// perfectly ordinary PNG of the right size, so the only thing that can tell them apart is what is in it.
/// </summary>
public class SkiaScreenshotImageEditorTests
{
    [Fact]
    public void TheRegionAskedFor_IsTheRegionReturned()
    {
        // Four quadrants, one colour each: whichever comes back says which corner was taken.
        var png = _Quadrants(100, 100);

        var cropped = new SkiaScreenshotImageEditor().Crop(png, new CaptureRect(50, 0, 50, 50));

        using var image = SKBitmap.Decode(cropped);
        image.Width.Should().Be(50);
        image.Height.Should().Be(50);
        image.GetPixel(25, 25).Should().Be(SKColors.Lime, "the top-right quadrant was asked for");
    }

    [Fact]
    public void ARegionSpanningTwoQuadrants_KeepsBoth()
    {
        var png = _Quadrants(100, 100);

        var cropped = new SkiaScreenshotImageEditor().Crop(png, new CaptureRect(25, 25, 50, 50));

        using var image = SKBitmap.Decode(cropped);
        image.GetPixel(5, 5).Should().Be(SKColors.Red);
        image.GetPixel(45, 45).Should().Be(SKColors.Yellow);
    }

    /// <summary>
    /// A region that runs off the edge — a display that changed shape between the capture and the confirm. Skia
    /// answers an out-of-bounds extract with an empty bitmap that encodes to a valid, blank PNG, so what is left
    /// of the region is taken rather than a picture of nothing being handed on.
    /// </summary>
    [Fact]
    public void ARegionRunningPastTheEdge_IsClampedToWhatIsThere()
    {
        var png = _Quadrants(100, 100);

        var cropped = new SkiaScreenshotImageEditor().Crop(png, new CaptureRect(80, 80, 100, 100));

        using var image = SKBitmap.Decode(cropped);
        image.Width.Should().Be(20);
        image.Height.Should().Be(20);
    }

    [Fact]
    public void ARegionEntirelyOutsideTheCapture_IsRefused()
    {
        var editor = new SkiaScreenshotImageEditor();

        var act = () => editor.Crop(_Quadrants(100, 100), new CaptureRect(200, 200, 50, 50));

        act.Should().Throw<InvalidOperationException>().WithMessage("*outside*");
    }

    [Fact]
    public void BytesThatAreNotAnImage_AreRefused()
    {
        var editor = new SkiaScreenshotImageEditor();

        var act = () => editor.Crop("<html>bad gateway</html>"u8.ToArray(), new CaptureRect(0, 0, 10, 10));

        act.Should().Throw<InvalidOperationException>().WithMessage("*could not be decoded as an image*");
    }

    private static byte[] _Quadrants(int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.DrawRect(SKRect.Create(0, 0, width / 2f, height / 2f), new SKPaint { Color = SKColors.Red });
        canvas.DrawRect(SKRect.Create(width / 2f, 0, width / 2f, height / 2f), new SKPaint { Color = SKColors.Lime });
        canvas.DrawRect(SKRect.Create(0, height / 2f, width / 2f, height / 2f), new SKPaint { Color = SKColors.Blue });
        canvas.DrawRect(SKRect.Create(width / 2f, height / 2f, width / 2f, height / 2f), new SKPaint { Color = SKColors.Yellow });

        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);

        return encoded.ToArray();
    }
}
