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
        Assert.Equal(new CaptureRect(150, 75, 300, 300), selection.Selection);
    }

    /// <summary>Dragging up and to the left is the same gesture as down and to the right — the anchor is where the press was, not where the rectangle starts.</summary>
    [Fact]
    public void ADragBackTowardsItsStart_KeepsItsArea()
    {
        var selection = _Surface();

        selection.BeginDrag(400, 400);
        selection.DragTo(200, 100);
        selection.EndDrag();

        Assert.Equal(new CaptureRect(300, 150, 300, 450), selection.Selection);
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

        Assert.Equal(new CaptureRect(101, 100, 50, 50), selection.Selection);
    }

    [Fact]
    public void AModifiedArrowKey_MovesFurtherInOneGo()
    {
        var selection = _Surface();
        selection.Selection = new CaptureRect(100, 100, 50, 50);

        selection.Nudge(dx: 0, dy: 1, step: 10);

        Assert.Equal(new CaptureRect(100, 110, 50, 50), selection.Selection);
    }

    [Fact]
    public void AResizingArrowKey_MovesTheFarEdgeOnly()
    {
        var selection = _Surface();
        selection.Selection = new CaptureRect(100, 100, 50, 50);

        selection.Nudge(dx: 1, dy: 0, resize: true);

        Assert.Equal(new CaptureRect(100, 100, 51, 50), selection.Selection);
    }

    /// <summary>Nudging cannot walk a selection off the image, which would crop nothing at all.</summary>
    [Fact]
    public void ASelectionAtTheEdge_StaysOnTheImage()
    {
        var selection = _Surface();
        selection.Selection = new CaptureRect(0, 0, 50, 50);

        selection.Nudge(dx: -1, dy: -1, step: 100);

        Assert.Equal(new CaptureRect(0, 0, 50, 50), selection.Selection);
    }

    [Fact]
    public void Everything_IsTheWholeImage()
    {
        var selection = _Surface();

        selection.SelectEverything();

        Assert.Equal(new CaptureRect(0, 0, 2880, 1620), selection.Selection);
    }

    [Fact]
    public void Confirming_YieldsTheSelection()
    {
        var selection = _Surface();
        selection.BeginDrag(10, 10);
        selection.DragTo(110, 110);
        selection.EndDrag();

        selection.Confirm();

        Assert.Equal(new CaptureRect(15, 15, 150, 150), selection.Result!.Region);
        Assert.True(selection.IsClosed);
    }

    [Fact]
    public void Cancelling_YieldsNothing()
    {
        var selection = _Surface();
        selection.BeginDrag(10, 10);
        selection.DragTo(110, 110);
        selection.EndDrag();

        selection.Cancel();

        Assert.Null(selection.Result);
        Assert.True(selection.IsClosed);
    }

    /// <summary>A press that never moved is not a selection, and confirming it would send a rectangle with no area.</summary>
    [Fact]
    public void AClickThatNeverMoved_SelectsNothing()
    {
        var selection = _Surface();

        selection.BeginDrag(10, 10);
        selection.EndDrag();
        selection.Confirm();

        Assert.Null(selection.Selection);
        Assert.Null(selection.Result);
        Assert.False(selection.IsClosed);
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

        Assert.False(started);
        Assert.Null(selection.Selection);
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

        Assert.True(selection.BeginDrag(surfaceX: 1900, surfaceY: 1000));
    }

    [Fact]
    public void ADragStartingOnADisplay_IsAccepted()
    {
        var selection = _Staggered();

        Assert.True(selection.BeginDrag(surfaceX: 2000, surfaceY: 400));
    }

    /// <summary>The same panel gets grabbed over and over; re-dragging it every time is the difference between a tool and a chore.</summary>
    [Fact]
    public void TheRegionFromLastTime_IsWaitingOnTheSurface()
    {
        var selection = _Surface(lastRegion: new CaptureRect(400, 300, 800, 600));

        Assert.Equal(new CaptureRect(400, 300, 800, 600), selection.Selection);
    }

    /// <summary>
    /// A region kept from a desktop that has since changed shape does not fit the image any more, and restoring
    /// it would crop somewhere arbitrary — so it is dropped rather than clamped into something nobody chose.
    /// </summary>
    [Fact]
    public void ARegionThatNoLongerFits_IsNotRestored()
    {
        var selection = _Surface(lastRegion: new CaptureRect(2800, 1600, 400, 400));

        Assert.Null(selection.Selection);
    }

    /// <summary>A window that has not been laid out yet must not put every pointer event on pixel zero.</summary>
    [Fact]
    public void BeforeTheWindowHasASize_TheRatioIsOne()
    {
        var selection = new ScreenshotSelectionViewModel(_Capture(Panel), 2880, 1620, Accent);

        Assert.Equal(new CapturePoint(100, 100), selection.ToImagePixel(100, 100));
    }

    /// <summary>Dragging a corner grip resizes only that corner; the opposite one stays exactly where it was.</summary>
    [Fact]
    public void DraggingACornerGrip_MovesOnlyThatCorner()
    {
        var selection = _Surface();
        // Dimensions divisible by three, so this ratio's every surface/image conversion below lands on an exact
        // double rather than a repeating fraction that Math.Floor could round the wrong way.
        selection.Selection = new CaptureRect(90, 90, 300, 300);

        // The bottom-right grip sits at image pixel (390, 390), which is surface (260, 260) at this ratio.
        Assert.True(selection.BeginDrag(260, 260));
        selection.DragTo(300, 300);
        selection.EndDrag();

        Assert.Equal(new CaptureRect(90, 90, 360, 360), selection.Selection);
    }

    /// <summary>An edge grip moves only the side it sits on — the sides next to it do not follow.</summary>
    [Fact]
    public void DraggingAnEdgeGrip_MovesOnlyThatSide()
    {
        var selection = _Surface();
        selection.Selection = new CaptureRect(90, 90, 300, 300);

        // The top grip sits at the middle of the top edge: image (240, 90), surface (160, 60).
        Assert.True(selection.BeginDrag(160, 60));
        selection.DragTo(160, 40);
        selection.EndDrag();

        Assert.Equal(new CaptureRect(90, 60, 300, 330), selection.Selection);
    }

    /// <summary>
    /// A grip dragged past the side it does not own tips the rectangle over rather than making it negative or
    /// collapsing it to nothing (AC-565, criterion 6) — the same normalisation a mark's own two corners already
    /// get from <c>_Between</c>.
    /// </summary>
    [Fact]
    public void ACornerGripDraggedPastItsOpposite_TipsTheRectangleOver()
    {
        var selection = _Surface();
        selection.Selection = new CaptureRect(150, 150, 300, 300);

        // The top-left grip, at surface (100, 100), dragged well past the rectangle's own bottom-right corner.
        Assert.True(selection.BeginDrag(100, 100));
        selection.DragTo(400, 400);
        selection.EndDrag();

        Assert.Equal(new CaptureRect(450, 450, 150, 150), selection.Selection);
        Assert.True(selection.Selection is { Width: > 0, Height: > 0 });
    }

    /// <summary>Dragging inside the selection carries the whole rectangle; its size does not change.</summary>
    [Fact]
    public void DraggingInsideTheSelection_MovesItWithoutResizingIt()
    {
        var selection = _Surface();
        selection.Selection = new CaptureRect(150, 150, 300, 300);

        // Surface (150, 150) is image pixel (225, 225) — well inside the rectangle and nowhere near a grip.
        Assert.True(selection.BeginDrag(150, 150));
        selection.DragTo(200, 200);
        selection.EndDrag();

        Assert.Equal(new CaptureRect(225, 225, 300, 300), selection.Selection);
    }

    /// <summary>
    /// The grip's own reach is wider than the little square drawn for it (AC-565, criterion 8): landing a few
    /// pixels short of dead centre on it still resizes rather than throwing the selection away and starting a new
    /// one — the costliest and quietest way this surface could misread a press.
    /// </summary>
    [Fact]
    public void ANearMissOnAGrip_StillResizes()
    {
        var selection = _Surface();
        selection.Selection = new CaptureRect(90, 90, 300, 300);

        // The bottom-right grip is at surface (260, 260); this presses 7 units off in each direction, well past
        // its own 9x9 drawn square's half-width but inside the wider hit radius.
        Assert.True(selection.BeginDrag(253, 253));
        selection.DragTo(270, 270);
        selection.EndDrag();

        Assert.Equal(new CaptureRect(90, 90, 315, 315), selection.Selection);
    }

    /// <summary>
    /// With a mark tool in hand, a press over what would otherwise be a grip draws a mark instead — the grips do
    /// nothing (AC-565, criterion 9). This is a decision recorded in AC-567, not a bug: two quick marks on the
    /// selection's edge are still two marks.
    /// </summary>
    [Fact]
    public void WithAMarkToolInHand_AGripPressDrawsAMarkInstead()
    {
        var selection = _Surface();
        selection.Selection = new CaptureRect(100, 100, 200, 200);
        selection.Outline(true);

        // The same spot the bottom-right grip sits on.
        Assert.True(selection.BeginDrag(200, 200));
        selection.DragTo(230, 230);
        selection.EndDrag();

        Assert.Equal(new CaptureRect(100, 100, 200, 200), selection.Selection);
        Assert.Single(selection.Marks);
        Assert.IsType<OutlineMark>(selection.Marks[0]);
    }

    /// <summary>
    /// The defect behind AC-567: before AC-565, any plain click inside an already-marked selection restarted the
    /// drag as a fresh zero-size rectangle and then nulled it on release — which meant the second press of a real
    /// double-click could never see a selection to confirm. A click that does not move must now leave the
    /// selection exactly as it was, so the double-click check that follows it still has something to confirm.
    /// </summary>
    [Fact]
    public void APlainClickInsideTheSelection_LeavesItIntactForADoubleClickToConfirm()
    {
        var selection = _Surface();
        selection.Selection = new CaptureRect(150, 150, 300, 300);

        Assert.True(selection.BeginDrag(150, 150));
        selection.EndDrag();

        Assert.Equal(new CaptureRect(150, 150, 300, 300), selection.Selection);
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
