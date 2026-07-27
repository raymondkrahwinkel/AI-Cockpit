using FluentAssertions;
using SkiaSharp;
using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Infrastructure.Screenshots;

namespace Cockpit.Infrastructure.Tests.Screenshots;

/// <summary>
/// Burning a wash into the picture (AC-361). The whole of what separates this from the box that hides is that what
/// is under it survives, so that is what these measure — on the returned bytes, which are the only version the
/// model ever sees.
/// </summary>
public class SkiaHighlightTests
{
    private const uint Accent = 0xFF3B82F6;

    /// <summary>
    /// The band is visible over a light page and the text on it is still text. Measured as the distance between
    /// the two, because that is what reading is: a wash that left the page alone would be no mark, and one that
    /// pulled the ink up to meet it would be a redaction drawn in a friendlier colour.
    /// </summary>
    [Fact]
    public void OverALightPage_TheBandShows_AndTheTextUnderItSurvives()
    {
        var page = _Page(SKColors.White, SKColors.Black);

        using var burnt = _Burn(page, new HighlightMark(new CaptureRect(0, 40, 200, 40), Accent, HighlightBlend.Darken));

        var washedPage = _Brightness(burnt.GetPixel(10, 50));
        var washedInk = _Brightness(burnt.GetPixel(100, 50));

        washedPage.Should().BeLessThan(240, "the page took the colour, so the band can be seen at all");
        (washedPage - washedInk).Should().BeGreaterThan(
            150, "and the ink stayed far below it — multiplying scales both ends rather than pulling them together");
    }

    /// <summary>
    /// The same over a terminal, the other way up. A wash that only knew how to darken would be invisible here,
    /// which is the failure the blend on the mark exists to prevent.
    /// </summary>
    [Fact]
    public void OverADarkTerminal_TheBandShows_AndTheTextUnderItSurvives()
    {
        var terminal = _Page(SKColors.Black, SKColors.White);

        using var burnt = _Burn(terminal, new HighlightMark(new CaptureRect(0, 40, 200, 40), Accent, HighlightBlend.Lighten));

        var washedBackground = _Brightness(burnt.GetPixel(10, 50));
        var washedInk = _Brightness(burnt.GetPixel(100, 50));

        washedBackground.Should().BeGreaterThan(20, "the background lifted, so the band can be seen");
        (washedInk - washedBackground).Should().BeGreaterThan(
            100, "and the ink stayed well above it");
    }

    /// <summary>
    /// A wash that darkened a terminal would be the tool doing nothing — worth its own measurement, because it is
    /// exactly what a single fixed blend would have done and it would have passed every other test here.
    /// </summary>
    [Fact]
    public void DarkeningATerminalWouldHideTheBand_WhichIsWhyTheBlendIsDecidedPerMark()
    {
        var terminal = _Page(SKColors.Black, SKColors.White);

        using var darkened = _Burn(terminal, new HighlightMark(new CaptureRect(0, 40, 200, 40), Accent, HighlightBlend.Darken));
        using var lifted = _Burn(terminal, new HighlightMark(new CaptureRect(0, 40, 200, 40), Accent, HighlightBlend.Lighten));

        _Brightness(darkened.GetPixel(10, 50)).Should().BeLessThan(
            10, "multiplying into a black background leaves it black — no band at all");
        _Brightness(lifted.GetPixel(10, 50)).Should().BeGreaterThan(
            _Brightness(darkened.GetPixel(10, 50)) + 20, "which is the whole reason the other way exists");
    }

    /// <summary>Two passes deepen, the way two passes of a marker pen do. Asked because it is a real choice and the alternative — a flat band however often it is drawn — would need bookkeeping the mark layer does not have.</summary>
    [Fact]
    public void TwoWashesOverEachOther_Deepen()
    {
        var page = _Page(SKColors.White, SKColors.Black);
        var band = new HighlightMark(new CaptureRect(0, 40, 200, 40), Accent, HighlightBlend.Darken);

        using var once = _Burn(page, band);
        using var twice = _Burn(page, band, band);

        _Brightness(twice.GetPixel(10, 50)).Should().BeLessThan(_Brightness(once.GetPixel(10, 50)));
    }

    /// <summary>What is outside the band is untouched — a wash is emphasis on one thing, and a picture where everything is emphasised says nothing.</summary>
    [Fact]
    public void OutsideTheBand_ThePictureIsExactlyAsItWas()
    {
        var page = _Page(SKColors.White, SKColors.Black);

        using var burnt = _Burn(page, new HighlightMark(new CaptureRect(0, 40, 200, 40), Accent, HighlightBlend.Darken));

        burnt.GetPixel(10, 10).Should().Be(SKColors.White);
        burnt.GetPixel(100, 10).Should().Be(SKColors.Black);
    }

    private static SKBitmap _Burn(byte[] png, params Mark[] marks) =>
        SKBitmap.Decode(new SkiaScreenshotImageEditor().Burn(png, marks));

    private static int _Brightness(SKColor pixel) => (pixel.Red + pixel.Green + pixel.Blue) / 3;

    /// <summary>A background with a band of ink down the right-hand half of it, so one row holds both and a single y can be read twice.</summary>
    private static byte[] _Page(SKColor background, SKColor ink)
    {
        using var bitmap = new SKBitmap(200, 100);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(background);
            using var paint = new SKPaint { Color = ink, Style = SKPaintStyle.Fill };
            canvas.DrawRect(new SKRect(50, 0, 200, 100), paint);
        }

        return SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }
}
