using FluentAssertions;
using SkiaSharp;
using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Infrastructure.Screenshots;

namespace Cockpit.Infrastructure.Tests.Screenshots;

/// <summary>
/// Burning a typed note into the picture (AC-363). The note is the only mark that carries meaning rather than
/// emphasis, so the thing measured here is that it arrives at all and that it is readable when it does.
/// </summary>
public class SkiaTextTests
{
    private const uint Blue = 0xFF0000FF;
    private const int Size = 28;

    /// <summary>
    /// The plate is in the bytes where the operator clicked. It is the whole legibility answer for this mark:
    /// letters over a screenshot have no background anyone can rely on, and the plate gives them one.
    /// </summary>
    [Theory]
    [InlineData(255, 255, 255)]
    [InlineData(0, 0, 0)]
    public void ThePlateIsBurntIn_WhateverIsUnderIt(byte red, byte green, byte blue)
    {
        using var image = _Burn(new TextMark(new CapturePoint(40, 40), "expected 12", Blue, Size),
            new SKColor(red, green, blue));

        // Just inside the corner that was clicked, which is plate rather than letter — the padding is there.
        var plate = image.GetPixel(44, 44);

        plate.Red.Should().BeGreaterThan(200, "the plate is the opposite of the letters, which are dark blue");
        plate.Green.Should().BeGreaterThan(200);
        plate.Blue.Should().BeGreaterThan(200);
    }

    /// <summary>The letters are on the plate, in their own colour — a plate with nothing on it is not a note.</summary>
    [Fact]
    public void TheLettersAreOnThePlate()
    {
        using var image = _Burn(new TextMark(new CapturePoint(40, 40), "III", Blue, Size), SKColors.Black);

        var ink = _DarkestIn(image, 40, 40, 160, 40 + Size + 20);

        ink.Should().BeLessThan(120, "somewhere on that plate there are letters, and they are not the plate's colour");
    }

    /// <summary>Nothing outside the plate is touched — a note is a note, not a repaint of the picture.</summary>
    [Fact]
    public void OutsideThePlate_ThePictureIsAsItWas()
    {
        using var image = _Burn(new TextMark(new CapturePoint(40, 40), "note", Blue, Size), SKColors.Black);

        image.GetPixel(10, 10).Should().Be(SKColors.Black);
        image.GetPixel(10, 180).Should().Be(SKColors.Black);
    }

    private static SKBitmap _Burn(Mark mark, SKColor background) =>
        SKBitmap.Decode(new SkiaScreenshotImageEditor().Burn(_Filled(400, 200, background), [mark]));

    private static int _DarkestIn(SKBitmap image, int left, int top, int right, int bottom)
    {
        var darkest = int.MaxValue;
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                var pixel = image.GetPixel(x, y);
                darkest = Math.Min(darkest, (pixel.Red + pixel.Green + pixel.Blue) / 3);
            }
        }

        return darkest;
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
