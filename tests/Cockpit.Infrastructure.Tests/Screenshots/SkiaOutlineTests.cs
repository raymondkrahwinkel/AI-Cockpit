using SkiaSharp;
using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Infrastructure.Screenshots;

namespace Cockpit.Infrastructure.Tests.Screenshots;

/// <summary>
/// Burning a frame into the picture (AC-359). Every assertion here decodes the returned bytes, because that is
/// the whole claim: the agent is handed one array and a mark that is not in it does not exist.
/// </summary>
public class SkiaOutlineTests
{
    private const uint Green = 0xFF00FF00;
    private const int Thickness = 4;

    /// <summary>
    /// The frame is in the bytes, in the colour it was asked for, at its edge — and the middle of it is untouched,
    /// which is what makes it a frame rather than a fill.
    /// </summary>
    [Fact]
    public void AFrame_IsInTheReturnedBytes_AtItsEdgeAndNotItsMiddle()
    {
        var png = _Filled(200, 200, SKColors.Black);

        var burnt = new SkiaScreenshotImageEditor().Burn(
            png, [new OutlineMark(new CaptureRect(50, 50, 100, 100), Green, Thickness)]);

        using var image = SKBitmap.Decode(burnt);
        Assert.Equal(new SKColor(0, 255, 0), image.GetPixel(52, 100));
        Assert.Equal(new SKColor(0, 255, 0), image.GetPixel(100, 52));
        Assert.Equal(SKColors.Black, image.GetPixel(100, 100));
        Assert.Equal(SKColors.Black, image.GetPixel(20, 20));
    }

    /// <summary>
    /// A frame running off the edge keeps the sides that are in the picture and grows none along the crop's edge.
    /// The far side of this one is outside the image entirely, so the column where it would have been stays black.
    /// </summary>
    [Fact]
    public void AFrameRunningOffTheEdge_GrowsNoSideAlongTheEdge()
    {
        var png = _Filled(100, 100, SKColors.Black);

        var burnt = new SkiaScreenshotImageEditor().Burn(
            png, [new OutlineMark(new CaptureRect(50, 20, 200, 40), Green, Thickness)]);

        using var image = SKBitmap.Decode(burnt);
        Assert.Equal(new SKColor(0, 255, 0), image.GetPixel(52, 40));
        Assert.Equal(SKColors.Black, image.GetPixel(97, 40));
    }

    /// <summary>
    /// Marks are burnt in the order they were placed. A frame over a pixelated box and a pixelated box over a
    /// frame are different pictures, and the one that is sent has to be the one the operator watched being made.
    /// </summary>
    [Fact]
    public void MarksAreBurntInOrder_SoTheLastOneIsOnTop()
    {
        var area = new CaptureRect(20, 20, 60, 60);

        using var frameOnTop = SKBitmap.Decode(new SkiaScreenshotImageEditor().Burn(
            _Checkerboard(100, 100),
            [new RedactionMark(area), new OutlineMark(area, Green, Thickness)]));
        using var boxOnTop = SKBitmap.Decode(new SkiaScreenshotImageEditor().Burn(
            _Checkerboard(100, 100),
            [new OutlineMark(area, Green, Thickness), new RedactionMark(area)]));

        Assert.Equal(new SKColor(0, 255, 0), frameOnTop.GetPixel(22, 50));
        Assert.NotEqual(new SKColor(0, 255, 0), boxOnTop.GetPixel(22, 50));
    }

    /// <summary>A picture with nothing on it is handed straight back, untouched and un-re-encoded.</summary>
    [Fact]
    public void WithNoMarks_TheBytesAreTheSameOnes()
    {
        var png = _Filled(50, 50, SKColors.Black);

        Assert.Same(png, new SkiaScreenshotImageEditor().Burn(png, []));
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

    /// <summary>Two colours in blocks, so a pixelated area averages to something that is neither of them.</summary>
    private static byte[] _Checkerboard(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                bitmap.SetPixel(x, y, (x / 8 + y / 8) % 2 == 0 ? SKColors.Black : SKColors.White);
            }
        }

        return SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }
}
