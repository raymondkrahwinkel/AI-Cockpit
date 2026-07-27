using FluentAssertions;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The mark layer (AC-359): one ordered list of things the operator put on the capture, one undo across all of
/// them, and each kind surviving the crop the way its own shape has to.
/// </summary>
/// <remarks>
/// What the marks then do to the pixels is the editor's and is tested there. This is where they are placed,
/// taken back, and moved into the coordinates of the picture that actually gets sent.
/// </remarks>
public class ScreenshotMarkLayerTests
{
    private static readonly CapturedDisplay Panel = new()
    {
        DesktopBounds = new CaptureRect(0, 0, 1920, 1080),
        Scale = 1,
        ImageBounds = new CaptureRect(0, 0, 1920, 1080),
    };

    private const uint Accent = 0xFF3B82F6;

    [Fact]
    public void AFrameDrawnOnTheCapture_ArrivesInTheCropsOwnCoordinates()
    {
        var selection = _Surface();
        _MarkOut(selection, 100, 100, 500, 400);
        _DrawFrame(selection, 150, 180, 60, 40);

        selection.Confirm();

        selection.Result!.Marks.Should().ContainSingle().Which
            .Should().BeOfType<OutlineMark>().Which
            .Area.Should().Be(new CaptureRect(50, 80, 60, 40));
    }

    /// <summary>
    /// A frame keeps its shape when it hangs over the edge. Shrinking it to the crop would close it along the
    /// crop's edge — drawing a side the operator never drew, on the one picture they cannot check against the
    /// original. It is moved whole; the part that falls outside simply is not painted.
    /// </summary>
    [Fact]
    public void AFrameHalfOutsideTheRegion_IsMovedWholeRatherThanShrunk()
    {
        var selection = _Surface();
        _MarkOut(selection, 100, 100, 500, 400);
        _DrawFrame(selection, 450, 150, 200, 100);

        selection.Confirm();

        selection.Result!.Marks.Should().ContainSingle().Which
            .Should().BeOfType<OutlineMark>().Which
            .Area.Should().Be(
                new CaptureRect(350, 50, 200, 100),
                "the width is the one that was drawn, not the part that fits");
    }

    /// <summary>A frame around something that is not being sent points at nothing, so it does not travel either.</summary>
    [Fact]
    public void AFrameOutsideTheRegion_IsNotCarried()
    {
        var selection = _Surface();
        _MarkOut(selection, 100, 100, 500, 400);
        _DrawFrame(selection, 700, 700, 50, 50);

        selection.Confirm();

        selection.Result!.Marks.Should().BeEmpty();
    }

    /// <summary>
    /// One undo for the lot. This is the whole reason redaction was folded into the layer: two stacks on one
    /// surface means the operator has to remember which tool they were in to know what Ctrl+Z will take.
    /// </summary>
    [Fact]
    public void UndoTakesBackTheLastMark_WhicheverKindItWas()
    {
        var selection = _Surface();
        _MarkOut(selection, 0, 0, 800, 600);
        _DrawBox(selection, 10, 10, 40, 40);
        _DrawFrame(selection, 100, 100, 40, 40);

        selection.Undo();

        selection.Marks.Should().ContainSingle().Which.Should().BeOfType<RedactionMark>(
            "the frame went on last, so the frame comes off first");

        selection.Undo();

        selection.Marks.Should().BeEmpty("and the box after it, off the same stack");
    }

    /// <summary>Placement order is kept, because it is visible: a frame over a pixelated box is not the same picture as the reverse.</summary>
    [Fact]
    public void TheOrderMarksWerePlacedIn_IsTheOrderTheyAreCarriedIn()
    {
        var selection = _Surface();
        _MarkOut(selection, 0, 0, 800, 600);
        _DrawBox(selection, 10, 10, 40, 40);
        _DrawFrame(selection, 20, 20, 40, 40);
        _DrawBox(selection, 30, 30, 40, 40);

        selection.Confirm();

        selection.Result!.Marks.Select(mark => mark.GetType()).Should().Equal(
            typeof(RedactionMark), typeof(OutlineMark), typeof(RedactionMark));
    }

    /// <summary>There is nothing to frame until something has been marked out — the same refusal redaction gives.</summary>
    [Fact]
    public void WithNothingMarkedOut_OutliningCannotStart_AndSaysWhy()
    {
        var selection = _Surface();

        selection.Outline(true);

        selection.Outlining.Should().BeFalse();
        selection.Hint.Should().Contain("Mark out a region first");
    }

    /// <summary>Taking up one mark tool puts the other down — they share the drag, so both being on has no meaning.</summary>
    [Fact]
    public void TakingUpOneMarkTool_PutsTheOtherDown()
    {
        var selection = _Surface();
        _MarkOut(selection, 0, 0, 800, 600);

        selection.Redact(true);
        selection.Outline(true);

        selection.Outlining.Should().BeTrue();
        selection.Redacting.Should().BeFalse();
    }

    /// <summary>The frame is drawn in the colour it was handed, which is the theme's — not one this layer decided on.</summary>
    [Fact]
    public void AFrameIsDrawnInTheColourTheSurfaceWasGiven()
    {
        const uint green = 0xFF00FF00;
        var selection = new ScreenshotSelectionViewModel(
            new ScreenCapture { Image = [0x89, 0x50, 0x4E, 0x47], Displays = [Panel] }, 1920, 1080, green)
        {
            SurfaceWidth = 1920,
            SurfaceHeight = 1080,
        };

        _MarkOut(selection, 0, 0, 800, 600);
        _DrawFrame(selection, 10, 10, 40, 40);

        selection.Marks.Should().ContainSingle().Which
            .Should().BeOfType<OutlineMark>().Which
            .Colour.Should().Be(green);
    }

    private static void _MarkOut(ScreenshotSelectionViewModel selection, int x, int y, int toX, int toY)
    {
        selection.BeginDrag(x, y);
        selection.DragTo(toX, toY);
        selection.EndDrag();
    }

    private static void _DrawBox(ScreenshotSelectionViewModel selection, int x, int y, int width, int height) =>
        _DrawWith(selection, MarkTool.Redaction, x, y, width, height);

    private static void _DrawFrame(ScreenshotSelectionViewModel selection, int x, int y, int width, int height) =>
        _DrawWith(selection, MarkTool.Outline, x, y, width, height);

    private static void _DrawWith(
        ScreenshotSelectionViewModel selection, MarkTool tool, int x, int y, int width, int height)
    {
        selection.MarkWith(tool, true);
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
