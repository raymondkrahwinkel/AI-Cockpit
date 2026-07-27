using FluentAssertions;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.App.ViewTests;

/// <summary>
/// Drawing the boxes that hide things (AC-331). What the boxes then do to the pixels is the editor's and is
/// tested there; this is where they are drawn, undone, and moved into the coordinates of the picture that
/// actually gets sent.
/// </summary>
/// <remarks>
/// Since AC-359 a box is one mark among others on a shared list and a shared undo. Every promise below is the one
/// AC-331 made and is asserted through the new shape rather than relaxed to fit it — folding redaction into the
/// mark layer was allowed to change the bookkeeping and nothing else.
/// </remarks>
public class ScreenshotRedactionTests
{
    private static readonly CapturedDisplay Panel = new()
    {
        DesktopBounds = new CaptureRect(0, 0, 1920, 1080),
        Scale = 1,
        ImageBounds = new CaptureRect(0, 0, 1920, 1080),
    };

    /// <summary>Any colour will do here — nothing in this file draws a frame, and the value only has to be carried.</summary>
    private const uint Accent = 0xFF3B82F6;

    /// <summary>
    /// A box is drawn on the whole capture but applied to the crop, so it has to be moved into the crop's own
    /// space. Red the moment the offset is dropped — the redaction would then land somewhere else in the picture
    /// that goes to the model, which is the worst way for this to fail.
    /// </summary>
    [Fact]
    public void ABoxDrawnOnTheCapture_ArrivesInTheCropsOwnCoordinates()
    {
        var selection = _Surface();
        _MarkOut(selection, 100, 100, 500, 400);
        _DrawBox(selection, 150, 180, 60, 40);

        selection.Confirm();

        selection.Result!.Region.Should().Be(new CaptureRect(100, 100, 400, 300));
        selection.Result.Marks.Should().Equal(new RedactionMark(new CaptureRect(50, 80, 60, 40)));
    }

    /// <summary>A box outside the region hides nothing in the picture that is sent, so it does not travel with it.</summary>
    [Fact]
    public void ABoxOutsideTheRegion_IsNotCarried()
    {
        var selection = _Surface();
        _MarkOut(selection, 100, 100, 500, 400);
        _DrawBox(selection, 700, 700, 50, 50);

        selection.Confirm();

        selection.Result!.Marks.Should().BeEmpty();
    }

    /// <summary>A box hanging over the edge of the region is kept for the part that is inside it.</summary>
    [Fact]
    public void ABoxHalfOutsideTheRegion_IsKeptForThePartInside()
    {
        var selection = _Surface();
        _MarkOut(selection, 100, 100, 500, 400);
        _DrawBox(selection, 450, 150, 200, 100);

        selection.Confirm();

        selection.Result!.Marks.Should().Equal(new RedactionMark(new CaptureRect(350, 50, 50, 100)));
    }

    /// <summary>
    /// Enter while the button is still down, halfway through drawing a box. The keyboard and the mouse are used
    /// together, and a box that only exists as a pending drag would be dropped in silence — sending the exact
    /// region it was drawn to hide.
    /// </summary>
    [Fact]
    public void ConfirmingMidBox_KeepsTheBoxRatherThanSendingWhatIsUnderIt()
    {
        var selection = _Surface();
        _MarkOut(selection, 100, 100, 500, 400);
        selection.Redact(true);
        selection.BeginDrag(150, 180);
        selection.DragTo(210, 220);

        selection.Confirm();

        selection.Result!.Marks.Should().Equal(new RedactionMark(new CaptureRect(50, 80, 60, 40)));
    }

    [Fact]
    public void UndoTakesBackTheLastBox_AndOnlyTheLast()
    {
        var selection = _Surface();
        _MarkOut(selection, 0, 0, 800, 600);
        _DrawBox(selection, 10, 10, 40, 40);
        _DrawBox(selection, 100, 100, 40, 40);

        selection.Undo();

        selection.Marks.Should().Equal(new RedactionMark(new CaptureRect(10, 10, 40, 40)));
    }

    /// <summary>
    /// There is nothing to hide until something has been marked out — and pressing the key then has to say so.
    /// It used to do nothing at all and say nothing, which reads exactly like a key that is not wired up: the
    /// operator could not tell the mode from a dead button.
    /// </summary>
    [Fact]
    public void WithNothingMarkedOut_RedactionCannotStart_AndSaysWhy()
    {
        var selection = _Surface();

        selection.Redact(true);

        selection.Redacting.Should().BeFalse();
        selection.Hint.Should().Contain("Mark out a region first");
    }

    /// <summary>A mode you cannot see is a mode that is not there — the surface says which of the two drags it is doing.</summary>
    [Fact]
    public void TheHintFollowsTheModeTheSurfaceIsIn()
    {
        var selection = _Surface();
        _MarkOut(selection, 100, 100, 500, 400);

        selection.Hint.Should().Contain("Drag a region");

        selection.Redact(true);

        selection.Hint.Should().Contain("should not be sent");
    }

    /// <summary>Dragging in redaction mode adds a box rather than moving the region the operator already settled on.</summary>
    [Fact]
    public void DraggingWhileRedacting_LeavesTheRegionAlone()
    {
        var selection = _Surface();
        _MarkOut(selection, 100, 100, 500, 400);
        _DrawBox(selection, 150, 150, 50, 50);

        selection.Selection.Should().Be(new CaptureRect(100, 100, 400, 300));
        selection.Marks.Should().ContainSingle().Which.Should().BeOfType<RedactionMark>();
    }

    private static void _MarkOut(ScreenshotSelectionViewModel selection, int x, int y, int toX, int toY)
    {
        selection.BeginDrag(x, y);
        selection.DragTo(toX, toY);
        selection.EndDrag();
    }

    private static void _DrawBox(ScreenshotSelectionViewModel selection, int x, int y, int width, int height)
    {
        selection.Redact(true);
        selection.BeginDrag(x, y);
        selection.DragTo(x + width, y + height);
        selection.EndDrag();
    }

    private static ScreenshotSelectionViewModel _Surface() =>
        new(new ScreenCapture { Image = [0x89, 0x50, 0x4E, 0x47], Displays = [Panel] }, 1920, 1080, Accent)
        {
            SurfaceWidth = 1920,
            SurfaceHeight = 1080,
        };
}
