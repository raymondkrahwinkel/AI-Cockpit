using FluentAssertions;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The selection surface's arithmetic (AC-329). The window itself cannot be tested — what it looks like needs
/// eyes — so everything that decides where a crop lands lives in the view model, and this is where it is held to
/// account. The case that matters most is the one where the window and the image are not the same size, because
/// on a scaled display they never are.
/// </summary>
public class ScreenshotSelectionTests
{
    /// <summary>Nothing here draws a frame, so the colour only has to be a value the surface can carry.</summary>
    private const uint Accent = 0xFF3B82F6;

    /// <summary>A 2880×1620 capture of a 1920×1080 desktop — a 150% panel, which is what this is being built on.</summary>
    private static readonly CapturedDisplay Panel = new()
    {
        DesktopBounds = new CaptureRect(0, 0, 1920, 1080),
        Scale = 1.5,
        ImageBounds = new CaptureRect(0, 0, 2880, 1620),
    };

    [Fact]
    public void ADrag_MapsToTheImagePixelsUnderIt()
    {
        var selection = _Surface();

        selection.BeginDrag(100, 50);
        selection.DragTo(300, 250);
        selection.EndDrag();

        // The window lays out 1920×1080 while the image is 2880×1620, so every distance is one and a half times
        // itself in the image. Red the moment anything here works in the window's units.
        selection.Selection.Should().Be(new CaptureRect(150, 75, 300, 300));
    }

    /// <summary>Dragging up and to the left is the same gesture as down and to the right — the anchor is where the press was, not where the rectangle starts.</summary>
    [Fact]
    public void ADragBackTowardsItsStart_KeepsItsArea()
    {
        var selection = _Surface();

        selection.BeginDrag(400, 400);
        selection.DragTo(200, 100);
        selection.EndDrag();

        selection.Selection.Should().Be(new CaptureRect(300, 150, 300, 450));
    }

    /// <summary>
    /// The arrow keys move by one pixel of the image, not one unit of the window. On this surface those are
    /// different distances, and a nudge in window units could not reach two out of every three pixels.
    /// </summary>
    [Fact]
    public void AnArrowKey_MovesTheSelectionByOneImagePixel()
    {
        var selection = _Surface();
        selection.Selection = new CaptureRect(100, 100, 50, 50);

        selection.Nudge(dx: 1, dy: 0);

        selection.Selection.Should().Be(new CaptureRect(101, 100, 50, 50));
    }

    [Fact]
    public void AModifiedArrowKey_MovesFurtherInOneGo()
    {
        var selection = _Surface();
        selection.Selection = new CaptureRect(100, 100, 50, 50);

        selection.Nudge(dx: 0, dy: 1, step: 10);

        selection.Selection.Should().Be(new CaptureRect(100, 110, 50, 50));
    }

    [Fact]
    public void AResizingArrowKey_MovesTheFarEdgeOnly()
    {
        var selection = _Surface();
        selection.Selection = new CaptureRect(100, 100, 50, 50);

        selection.Nudge(dx: 1, dy: 0, resize: true);

        selection.Selection.Should().Be(new CaptureRect(100, 100, 51, 50));
    }

    /// <summary>Nudging cannot walk a selection off the image, which would crop nothing at all.</summary>
    [Fact]
    public void ASelectionAtTheEdge_StaysOnTheImage()
    {
        var selection = _Surface();
        selection.Selection = new CaptureRect(0, 0, 50, 50);

        selection.Nudge(dx: -1, dy: -1, step: 100);

        selection.Selection.Should().Be(new CaptureRect(0, 0, 50, 50));
    }

    [Fact]
    public void Everything_IsTheWholeImage()
    {
        var selection = _Surface();

        selection.SelectEverything();

        selection.Selection.Should().Be(new CaptureRect(0, 0, 2880, 1620));
    }

    [Fact]
    public void Confirming_YieldsTheSelection()
    {
        var selection = _Surface();
        selection.BeginDrag(10, 10);
        selection.DragTo(110, 110);
        selection.EndDrag();

        selection.Confirm();

        selection.Result!.Region.Should().Be(new CaptureRect(15, 15, 150, 150));
        selection.IsClosed.Should().BeTrue();
    }

    [Fact]
    public void Cancelling_YieldsNothing()
    {
        var selection = _Surface();
        selection.BeginDrag(10, 10);
        selection.DragTo(110, 110);
        selection.EndDrag();

        selection.Cancel();

        selection.Result.Should().BeNull();
        selection.IsClosed.Should().BeTrue();
    }

    /// <summary>A press that never moved is not a selection, and confirming it would send a rectangle with no area.</summary>
    [Fact]
    public void AClickThatNeverMoved_SelectsNothing()
    {
        var selection = _Surface();

        selection.BeginDrag(10, 10);
        selection.EndDrag();
        selection.Confirm();

        selection.Selection.Should().BeNull();
        selection.Result.Should().BeNull();
        selection.IsClosed.Should().BeFalse();
    }

    /// <summary>
    /// A staggered arrangement leaves the capture with area no display covers — the compositor never painted it.
    /// A drag cannot start there, because offering pixels that were nobody's as though they were screen is the
    /// one thing this surface must not do.
    /// </summary>
    [Fact]
    public void ADragStartingWhereNoDisplayIs_IsIgnored()
    {
        var selection = _Staggered();

        // Above the shorter display, which starts 360 rows down: inside the image, on nobody's screen.
        var started = selection.BeginDrag(surfaceX: 2000, surfaceY: 20);

        started.Should().BeFalse();
        selection.Selection.Should().BeNull();
    }

    /// <summary>
    /// Far enough into a scaled display that the image pixel is past the desktop's own width. Asking whether a
    /// display is there has to happen in the space the point is in: the desktop rectangle is 1920 wide while the
    /// image is 2880, so a membership test against the wrong one refuses everything beyond two-thirds across —
    /// on a perfectly ordinary single monitor.
    /// </summary>
    [Fact]
    public void ADragPastTheDesktopsOwnWidth_IsStillOnTheDisplay()
    {
        var selection = _Surface();

        selection.BeginDrag(surfaceX: 1900, surfaceY: 1000).Should().BeTrue();
    }

    [Fact]
    public void ADragStartingOnADisplay_IsAccepted()
    {
        var selection = _Staggered();

        selection.BeginDrag(surfaceX: 2000, surfaceY: 400).Should().BeTrue();
    }

    /// <summary>The same panel gets grabbed over and over; re-dragging it every time is the difference between a tool and a chore.</summary>
    [Fact]
    public void TheRegionFromLastTime_IsWaitingOnTheSurface()
    {
        var selection = _Surface(lastRegion: new CaptureRect(400, 300, 800, 600));

        selection.Selection.Should().Be(new CaptureRect(400, 300, 800, 600));
    }

    /// <summary>
    /// A region kept from a desktop that has since changed shape does not fit the image any more, and restoring
    /// it would crop somewhere arbitrary — so it is dropped rather than clamped into something nobody chose.
    /// </summary>
    [Fact]
    public void ARegionThatNoLongerFits_IsNotRestored()
    {
        var selection = _Surface(lastRegion: new CaptureRect(2800, 1600, 400, 400));

        selection.Selection.Should().BeNull();
    }

    /// <summary>A window that has not been laid out yet must not put every pointer event on pixel zero.</summary>
    [Fact]
    public void BeforeTheWindowHasASize_TheRatioIsOne()
    {
        var selection = new ScreenshotSelectionViewModel(_Capture(Panel), 2880, 1620, Accent);

        selection.ToImagePixel(100, 100).Should().Be(new CapturePoint(100, 100));
    }

    private static ScreenshotSelectionViewModel _Surface(CaptureRect? lastRegion = null) =>
        new(_Capture(Panel), 2880, 1620, Accent, lastRegion) { SurfaceWidth = 1920, SurfaceHeight = 1080 };

    /// <summary>Two displays of different heights side by side: the shorter one leaves the capture with area nothing painted.</summary>
    private static ScreenshotSelectionViewModel _Staggered()
    {
        var tall = new CapturedDisplay
        {
            DesktopBounds = new CaptureRect(0, 0, 1920, 1440),
            Scale = 1,
            ImageBounds = new CaptureRect(0, 0, 1920, 1440),
        };
        var shortOne = new CapturedDisplay
        {
            DesktopBounds = new CaptureRect(1920, 360, 1920, 1080),
            Scale = 1,
            ImageBounds = new CaptureRect(1920, 360, 1920, 1080),
        };

        return new ScreenshotSelectionViewModel(_Capture(tall, shortOne), 3840, 1440, Accent)
        {
            SurfaceWidth = 3840,
            SurfaceHeight = 1440,
        };
    }

    private static ScreenCapture _Capture(params CapturedDisplay[] displays) =>
        new() { Image = [0x89, 0x50, 0x4E, 0x47], Displays = displays };
}
