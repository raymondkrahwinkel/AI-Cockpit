using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Infrastructure.Screenshots;

namespace Cockpit.Infrastructure.Tests.Screenshots;

/// <summary>
/// The macOS capture against a faked Mac (AC-328). There is no Mac to run the real thing on, which makes this
/// the whole of the evidence: what is asked of <c>screencapture</c>, and where each display's pixels end up in
/// the one image the contract wants. Both are checked on the pixels themselves rather than on the intent.
/// </summary>
public class MacScreenshotCaptureTests
{
    /// <summary>
    /// The flag AC-220 was rejected over. <c>-i</c> is the system's own crosshair, which is the UI this tool
    /// exists to own — and <c>-x</c> stays, because a shutter sound is not something to hand an agent.
    /// </summary>
    [Fact]
    public void TheArguments_CarryNoInteractiveFlag()
    {
        var arguments = ScreenCaptureArguments.ForDisplay(2, "/tmp/shot.png");

        arguments.Should().NotContain("-i");
        arguments.Should().ContainInOrder("-D", "2");
        arguments.Should().Contain("-x");
        arguments.Should().EndWith("/tmp/shot.png");
    }

    /// <summary>One invocation per display, each naming its own — without <c>-D</c> what the binary writes with several attached is not something anyone established.</summary>
    [Fact]
    public async Task SeveralDisplays_AreEachCapturedByTheirOwnIndex()
    {
        var screens = new StubMacScreens
        {
            Displays = [_Display(1, 0, 0, 1920, 1080), _Display(2, 1920, 0, 1920, 1080)],
        };

        await _Capture(screens).CaptureAsync();

        screens.Captured.Should().Equal(1, 2);
    }

    /// <summary>A single Retina panel: the capture is twice the points it reports, and the whole of it belongs to that display.</summary>
    [Fact]
    public async Task ARetinaPanel_KeepsItsOwnPixels()
    {
        var screens = new StubMacScreens { Displays = [_Display(1, 0, 0, 1710, 1112, pixelWidth: 3420)] };

        var result = await _Capture(screens).CaptureAsync();

        result.Should().NotBeNull();
        result!.Displays.Should().ContainSingle();
        result.Displays[0].ImageBounds.Should().Be(new CaptureRect(0, 0, 3420, 2224));
        result.Displays[0].Scale.Should().Be(2);
    }

    /// <summary>
    /// The arrangement macOS allows and Linux does not: a Retina laptop beside an ordinary monitor, two scales
    /// at once. The canvas takes the larger, so nothing is captured below its native resolution — and the
    /// monitor's pixels have to land on its own half, which is asserted on the colours themselves.
    /// </summary>
    [Fact]
    public async Task ARetinaPanelBesideAnOrdinaryMonitor_EachLandOnTheirOwnHalf()
    {
        var screens = new StubMacScreens
        {
            Displays =
            [
                _Display(1, 0, 0, 1440, 900, pixelWidth: 2880),
                _Display(2, 1440, 0, 1920, 1080),
            ],
        };

        var result = await _Capture(screens).CaptureAsync();

        using var image = SKBitmap.Decode(result!.Image);
        image.Width.Should().Be(6720, "the desktop is 3360 points wide and the larger of the two scales is 2");
        image.Height.Should().Be(2160);

        var laptop = result.Displays[0].ImageBounds;
        var monitor = result.Displays[1].ImageBounds;
        image.GetPixel(laptop.X + 10, laptop.Y + 10).Should().Be(SKColors.White, "each capture carries a band across its top quarter");
        image.GetPixel(laptop.X + 10, laptop.Y + (laptop.Height / 2)).Should().Be(screens.ColourOf(1));
        image.GetPixel(monitor.X + 10, monitor.Y + (monitor.Height / 2)).Should().Be(screens.ColourOf(2));
        monitor.X.Should().Be(2880, "the panel beside it contributed 1440 points at scale 2");
    }

    /// <summary>
    /// The band each capture carries has to land a quarter of the way down the slot it was drawn into. A flat
    /// fill would read the same however the source was stretched; this is what says the destination rectangle
    /// is the size the layout asked for rather than the size the source happened to be.
    /// </summary>
    [Fact]
    public async Task ADisplayDrawnIntoItsSlot_KeepsItsProportions()
    {
        var screens = new StubMacScreens
        {
            Displays = [_Display(1, 0, 0, 1440, 900, pixelWidth: 2880), _Display(2, 1440, 0, 1920, 1080)],
        };

        var result = await _Capture(screens).CaptureAsync();

        using var image = SKBitmap.Decode(result!.Image);
        var monitor = result.Displays[1].ImageBounds;
        var quarter = monitor.Y + (monitor.Height / 4);
        image.GetPixel(monitor.X + 10, quarter - 4).Should().Be(SKColors.White);
        image.GetPixel(monitor.X + 10, quarter + 4).Should().Be(screens.ColourOf(2));
    }

    /// <summary>
    /// The one assumption this file rests on that nobody could verify: that
    /// <c>CGGetActiveDisplayList</c>'s order is <c>screencapture -D</c>'s numbering. If it is not, a display's
    /// geometry is paired with another display's pixels — and the draw stretches whatever it is given, so the
    /// result is a picture of exactly the right size showing the wrong screen in the wrong place.
    /// </summary>
    [Fact]
    public async Task ACaptureThatIsNotTheDisplayItWasAskedFor_IsRefused()
    {
        var screens = new StubMacScreens
        {
            Displays = [_Display(1, 0, 0, 1440, 900, pixelWidth: 2880), _Display(2, 1440, 0, 1920, 1080)],
            CapturesSomeOtherDisplay = [2],
        };

        var act = async () => await _Capture(screens).CaptureAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not the 1920×1080 that display reports*");
    }

    /// <summary>
    /// A desktop taller than it is wide, at a scale that divides neither cleanly. Rounding the canvas' width and
    /// height separately against the same fraction leaves them on ratios that differ by more than the layout
    /// allows, and a perfectly ordinary stack of displays is refused — 2000×6000 points at 2402/1600 rounds to
    /// 3002×9008, whose ratios are 1.501 and 1.5013.
    /// </summary>
    [Fact]
    public async Task ADesktopTallerThanItIsWide_IsComposedRatherThanRefused()
    {
        var screens = new StubMacScreens
        {
            Displays =
            [
                _Display(1, 0, 0, 1600, 3000, pixelWidth: 2402),
                _Display(2, 0, 3000, 2000, 3000),
            ],
        };

        var result = await _Capture(screens).CaptureAsync();

        result.Should().NotBeNull();
        using var image = SKBitmap.Decode(result!.Image);
        image.Width.Should().Be(3002);
    }

    /// <summary>
    /// A staggered arrangement leaves canvas no display covers. It has to read as nothing rather than as part
    /// of a screen — the selection surface must not offer pixels that were never anybody's.
    /// </summary>
    [Fact]
    public async Task AreaNoDisplayCovers_IsBlank()
    {
        var screens = new StubMacScreens
        {
            Displays = [_Display(1, 0, 400, 1440, 900), _Display(2, 1440, 0, 1920, 1080)],
        };

        var result = await _Capture(screens).CaptureAsync();

        using var image = SKBitmap.Decode(result!.Image);
        image.GetPixel(10, 10).Should().Be(SKColors.Black, "nothing sits above the display that starts 400 points down");
        result.DisplayAt(new CapturePoint(10, 10)).Should().BeNull();
    }

    /// <summary>
    /// Screen Recording is granted per application, so a display that writes nothing means none of them will.
    /// Reported as nothing captured, which the caller passes over in silence — honest, because from here it is
    /// genuinely indistinguishable from a capture nobody wanted.
    /// </summary>
    [Fact]
    public async Task ADisplayThatWritesNothing_EndsAsNothingCaptured()
    {
        var screens = new StubMacScreens
        {
            Displays = [_Display(1, 0, 0, 1920, 1080), _Display(2, 1920, 0, 1920, 1080)],
            CapturesNothing = [1],
        };

        (await _Capture(screens).CaptureAsync()).Should().BeNull();
    }

    /// <summary>
    /// A screen unplugged partway through. The displays are read once and then captured one process launch at a
    /// time, so the window for this is far wider than the single blit Windows guards — and what comes out is a
    /// composed image of exactly the right size describing a desktop that no longer exists.
    /// </summary>
    [Fact]
    public async Task ADisplayChangingMidCapture_IsRefused()
    {
        var screens = new StubMacScreens
        {
            Displays = [_Display(1, 0, 0, 1920, 1080), _Display(2, 1920, 0, 1920, 1080)],
            DisplaysAfterCapture = [_Display(1, 0, 0, 1920, 1080)],
        };

        var act = async () => await _Capture(screens).CaptureAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*changed while*");
    }

    /// <summary>A Mac with no active display is not a cancel — there was nothing there to read, and an operator who pressed a key is owed the difference.</summary>
    [Fact]
    public async Task NoActiveDisplays_IsRefused()
    {
        var capture = _Capture(new StubMacScreens { Displays = [] });

        var act = async () => await capture.CaptureAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no active displays*");
    }

    private static MacScreenshotCapture _Capture(IMacScreenReader screens) =>
        new(screens, NullLogger<MacScreenshotCapture>.Instance);

    private static MacDisplay _Display(int index, int x, int y, int width, int height, int? pixelWidth = null) =>
        new()
        {
            Index = index,
            Bounds = new CaptureRect(x, y, width, height),
            PixelWidth = pixelWidth ?? width,
            PixelHeight = (int)Math.Round(height * ((pixelWidth ?? width) / (double)width)),
        };
}
