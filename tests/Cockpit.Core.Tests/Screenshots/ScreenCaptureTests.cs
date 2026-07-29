using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.Core.Tests.Screenshots;

/// <summary>
/// The capture contract's one piece of arithmetic (AC-333): turning a point on the desktop into a pixel of the
/// composed image and back. Everything the screenshot tool does afterwards — the selection rectangle, the crop,
/// the blur — is that sum, so a display's scale being per display rather than per desktop is a bug class rather
/// than a detail (Spectacle carries an open one, KDE#502047).
/// </summary>
public class ScreenCaptureTests
{
    /// <summary>
    /// The arrangement the whole ticket exists for: a 150% laptop panel with a 100% monitor to the right of it.
    /// The monitor starts at desktop x = 1920 but at image x = 2880, because the panel beside it contributed
    /// 1920 × 1.5 columns — so a point on it cannot be scaled into place by its own factor.
    /// </summary>
    private static readonly CapturedDisplay Laptop = new()
    {
        DesktopBounds = new CaptureRect(0, 0, 1920, 1080),
        Scale = 1.5,
        ImageBounds = new CaptureRect(0, 0, 2880, 1620),
    };

    private static readonly CapturedDisplay Monitor = new()
    {
        DesktopBounds = new CaptureRect(1920, 0, 1920, 1080),
        Scale = 1.0,
        ImageBounds = new CaptureRect(2880, 0, 1920, 1080),
    };

    private static readonly ScreenCapture MixedScaling = new()
    {
        Image = [0x89, 0x50, 0x4E, 0x47],
        Displays = [Laptop, Monitor],
    };

    [Fact]
    public void APointOnTheScaledDisplay_LandsOnItsPixelInTheImage()
    {
        Assert.Equal(new CapturePoint(150, 75), MixedScaling.ToImagePixel(new CapturePoint(100, 50)));
    }

    /// <summary>
    /// The one a per-desktop scale factor gets wrong. Scaling this point by the monitor's own 1.0 puts it at
    /// image x = 2020, which is inside the laptop's half of the image — a crop of the wrong screen entirely.
    /// </summary>
    [Fact]
    public void APointOnTheUnscaledDisplay_LandsPastThePixelsTheScaledOneContributed()
    {
        Assert.Equal(new CapturePoint(2980, 10), MixedScaling.ToImagePixel(new CapturePoint(2020, 10)));
    }

    /// <summary>
    /// Offsets where 1.5 does not land on a whole pixel — the ones a naive round or truncate gets wrong by one,
    /// which is invisible in a screenshot and not invisible in an assertion.
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(3, 5)]
    [InlineData(11, 17)]
    [InlineData(1919, 2879)]
    public void AFractionalOffsetOnTheScaledDisplay_TakesTheFirstPixelThatBelongsToIt(int desktopX, int imageX)
    {
        Assert.Equal(new CapturePoint(imageX, 0), MixedScaling.ToImagePixel(new CapturePoint(desktopX, 0)));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 3)]
    [InlineData(11, 17)]
    [InlineData(1919, 1079)]
    [InlineData(2020, 10)]
    [InlineData(3839, 1079)]
    public void EveryPointOnEitherDisplay_ComesBackAsItself(int desktopX, int desktopY)
    {
        var desktopPoint = new CapturePoint(desktopX, desktopY);

        var imagePixel = MixedScaling.ToImagePixel(desktopPoint);

        Assert.NotNull(imagePixel);
        Assert.Equal(desktopPoint, MixedScaling.ToDesktopPoint(imagePixel.Value));
    }

    /// <summary>
    /// The seam. Two displays laid edge to edge must claim the boundary column exactly once, or a pointer there
    /// crops whichever screen happened to be enumerated first.
    /// </summary>
    [Fact]
    public void ThePointWhereTheDisplaysMeet_BelongsToTheSecondOne()
    {
        Assert.Equal(Laptop, MixedScaling.DisplayAt(new CapturePoint(1919, 0)));
        Assert.Equal(Monitor, MixedScaling.DisplayAt(new CapturePoint(1920, 0)));
    }

    [Fact]
    public void ThePixelWhereTheDisplaysMeetInTheImage_BelongsToTheSecondOne()
    {
        Assert.Equal(new CapturePoint(1919, 0), MixedScaling.ToDesktopPoint(new CapturePoint(2879, 0)));
        Assert.Equal(new CapturePoint(1920, 0), MixedScaling.ToDesktopPoint(new CapturePoint(2880, 0)));
    }

    /// <summary>
    /// A monitor placed left of the primary one puts part of the desktop at negative coordinates while the image
    /// starts at zero — the offset between the two spaces is then larger than either display.
    /// </summary>
    [Fact]
    public void ADisplayLeftOfTheOrigin_MapsFromNegativeDesktopCoordinates()
    {
        var capture = new ScreenCapture
        {
            Image = [0x89],
            Displays =
            [
                new CapturedDisplay
                {
                    DesktopBounds = new CaptureRect(-1920, 0, 1920, 1080),
                    Scale = 1.0,
                    ImageBounds = new CaptureRect(0, 0, 1920, 1080),
                },
                Monitor with { DesktopBounds = new CaptureRect(0, 0, 1920, 1080), ImageBounds = new CaptureRect(1920, 0, 1920, 1080) },
            ],
        };

        Assert.Equal(new CapturePoint(100, 40), capture.ToImagePixel(new CapturePoint(-1820, 40)));
        Assert.Equal(new CapturePoint(-1820, 40), capture.ToDesktopPoint(new CapturePoint(100, 40)));
    }

    /// <summary>Two displays of different heights leave a corner of the desktop's bounding box on no screen at all.</summary>
    [Fact]
    public void APointOnNoDisplay_MapsToNothing()
    {
        Assert.Null(MixedScaling.DisplayAt(new CapturePoint(2000, 2000)));
        Assert.Null(MixedScaling.ToImagePixel(new CapturePoint(2000, 2000)));
    }

    [Fact]
    public void APixelOnNoDisplay_MapsToNothing()
    {
        Assert.Null(MixedScaling.ToDesktopPoint(new CapturePoint(4800, 0)));
    }

    /// <summary>
    /// A capture nobody could put a layout on. No platform produces one since AC-326/327/328 — they refuse
    /// instead — but the mapping still has to answer nothing rather than invent a display and land the first
    /// crop somewhere plausible and wrong.
    /// </summary>
    [Fact]
    public void ACaptureWithoutLayout_MapsNothingInEitherDirection()
    {
        byte[] png = [0x89, 0x50];

        var capture = new ScreenCapture { Image = png, Displays = [] };

        Assert.Equal(png, capture.Image);
        Assert.Empty(capture.Displays);
        Assert.Null(capture.ToImagePixel(new CapturePoint(0, 0)));
        Assert.Null(capture.ToDesktopPoint(new CapturePoint(0, 0)));
    }
}
