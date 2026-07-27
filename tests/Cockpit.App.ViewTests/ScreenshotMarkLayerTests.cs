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

    /// <summary>
    /// An arrow remembers which way it was dragged (AC-360). The layer held a drag as a rectangle until this tool
    /// arrived, and a rectangle cannot say this: up-and-left and down-and-right cover the very same one, and are
    /// opposite arrows.
    /// </summary>
    [Fact]
    public void AnArrowKeepsTheDirectionItWasDraggedIn()
    {
        var selection = _Surface();
        _MarkOut(selection, 0, 0, 800, 600);
        _DrawArrow(selection, 400, 300, 150, 120);

        selection.Marks.Should().ContainSingle().Which
            .Should().BeOfType<ArrowMark>().Which
            .Should().BeEquivalentTo(new
            {
                From = new CapturePoint(400, 300),
                To = new CapturePoint(150, 120),
            });
    }

    /// <summary>
    /// What counts as a drag going nowhere is the kind's own business. A box needs area; an arrow needs only to
    /// have travelled, so one drawn straight down is a perfectly good arrow and a rectangle of no width.
    /// </summary>
    [Fact]
    public void AnArrowStraightDown_IsAMark_WhereABoxOfNoWidthIsNot()
    {
        var selection = _Surface();
        _MarkOut(selection, 0, 0, 800, 600);

        _DrawArrow(selection, 200, 100, 200, 400);
        selection.Marks.Should().ContainSingle("an arrow that went somewhere is an arrow");

        _DrawBox(selection, 300, 100, 0, 300);
        selection.Marks.Should().ContainSingle("but a box with no width covers nothing, so nothing is placed");
    }

    /// <summary>A press that never moved has no direction, and an arrow with no direction points at nothing.</summary>
    [Fact]
    public void AnArrowThatWentNowhere_PlacesNoMark()
    {
        var selection = _Surface();
        _MarkOut(selection, 0, 0, 800, 600);

        _DrawArrow(selection, 200, 200, 200, 200);

        selection.Marks.Should().BeEmpty();
    }

    /// <summary>
    /// Region takes back whichever mark tool is in hand. It asked to put down redaction by name until AC-360 — from
    /// when that was the only tool there was to be holding — so pressing Region while drawing frames left you
    /// drawing frames, and the row went on saying so.
    /// </summary>
    [Theory]
    [InlineData(MarkTool.Redaction)]
    [InlineData(MarkTool.Outline)]
    [InlineData(MarkTool.Arrow)]
    public void ChoosingRegion_PutsDownWhicheverMarkToolIsInHand(MarkTool tool)
    {
        var selection = _Surface();
        _MarkOut(selection, 0, 0, 800, 600);
        selection.MarkWith(tool, true);

        selection.ChooseRegion();

        selection.MarkingWith.Should().BeNull();
        selection.DraggingRegion.Should().BeTrue("and the row has to be able to say so");
    }

    /// <summary>
    /// What is previewed mid-drag is the mark that is about to be placed, built by the same call. A preview made
    /// separately is a second opinion about the picture rather than a look at it.
    /// </summary>
    [Fact]
    public void TheMarkBeingDragged_IsPreviewedAsTheKindItWillBecome()
    {
        var selection = _Surface();
        _MarkOut(selection, 0, 0, 800, 600);

        selection.MarkWith(MarkTool.Arrow, true);
        selection.BeginDrag(100, 100);
        selection.DragTo(300, 260);

        selection.PendingMarkPreview.Should().BeOfType<ArrowMark>().Which
            .Should().BeEquivalentTo(new { To = new CapturePoint(300, 260) });

        selection.EndDrag();

        selection.PendingMarkPreview.Should().BeNull("the drag is over");
        selection.Marks.Should().ContainSingle().Which.Should().BeOfType<ArrowMark>().Which
            .Should().BeEquivalentTo(new { To = new CapturePoint(300, 260) }, "and what was previewed is what was kept");
    }

    /// <summary>
    /// A mark's thickness is in the captured image's pixels, and the surface draws in its own units. Left
    /// unconverted the preview is heavier than the picture by exactly the display's scale — so the operator checks
    /// a frame that is not the frame they are about to hand over.
    /// </summary>
    [Fact]
    public void AMarksThickness_IsGivenInTheWindowsUnitsWhenItIsDrawn()
    {
        var selection = new ScreenshotSelectionViewModel(
            new ScreenCapture { Image = [0x89, 0x50, 0x4E, 0x47], Displays = [Panel] }, 1920, 1080, Accent)
        {
            SurfaceWidth = 960,
            SurfaceHeight = 540,
        };

        selection.ToSurfaceLength(8).Should().Be(4, "the capture is twice the size of the window drawing it");
    }

    /// <summary>
    /// A wash asks the picture which way it has to go (AC-361). Ink over paper and ink over a terminal move the
    /// pixels in opposite directions, and only what is underneath can say which of the two this is.
    /// </summary>
    [Theory]
    [InlineData(240, HighlightBlend.Darken)]
    [InlineData(20, HighlightBlend.Lighten)]
    public void AWashTakesItsDirectionFromWhatIsUnderIt(int brightness, HighlightBlend expected)
    {
        var selection = _Surface(_ => brightness);
        _MarkOut(selection, 0, 0, 800, 600);

        _DrawWith(selection, MarkTool.Highlight, 100, 100, 200, 40);

        selection.Marks.Should().ContainSingle().Which
            .Should().BeOfType<HighlightMark>().Which
            .Blend.Should().Be(expected);
    }

    /// <summary>
    /// The picture is asked about the band itself, not about the region the band sits in. A page with a terminal
    /// on one side of it averages to something that is neither, and the wash would then be drawn the wrong way
    /// round for both halves.
    /// </summary>
    [Fact]
    public void TheDirectionIsAskedAboutTheBand_NotAboutTheWholeRegion()
    {
        var asked = new List<CaptureRect>();
        var selection = _Surface(area =>
        {
            asked.Add(area);
            return 240;
        });
        _MarkOut(selection, 0, 0, 800, 600);

        _DrawWith(selection, MarkTool.Highlight, 100, 100, 200, 40);

        asked.Should().NotBeEmpty().And.AllBeEquivalentTo(
            new CaptureRect(100, 100, 200, 40), "which is the band that was dragged, not the region under it");
    }

    /// <summary>
    /// A surface with no way to look at its own picture washes the way a marker pen does. It is a fallback rather
    /// than a preference — over a terminal this one is close to invisible, which is exactly why the real surface
    /// hands in a way to look.
    /// </summary>
    [Fact]
    public void WithNoWayToLookAtThePicture_AWashDarkens()
    {
        var selection = _Surface();
        _MarkOut(selection, 0, 0, 800, 600);

        _DrawWith(selection, MarkTool.Highlight, 100, 100, 200, 40);

        selection.Marks.Should().ContainSingle().Which
            .Should().BeOfType<HighlightMark>().Which
            .Blend.Should().Be(HighlightBlend.Darken);
    }

    /// <summary>
    /// A stroke keeps the way the pointer got from one end to the other (AC-362). Every other mark is made from
    /// the two ends of a drag and this one is the whole of it — which is why the drag is carried as a path.
    /// </summary>
    [Fact]
    public void AStrokeKeepsThePathThePointerTook_NotOnlyItsEnds()
    {
        var selection = _Surface();
        _MarkOut(selection, 0, 0, 800, 600);

        selection.MarkWith(MarkTool.Stroke, true);
        selection.BeginDrag(100, 100);
        selection.DragTo(140, 180);
        selection.DragTo(220, 190);
        selection.EndDrag();

        selection.Marks.Should().ContainSingle().Which
            .Should().BeOfType<StrokeMark>().Which
            .Points.Should().Equal(
                new CapturePoint(100, 100), new CapturePoint(140, 180), new CapturePoint(220, 190));
    }

    /// <summary>
    /// One line per press, and one Ctrl+Z takes the whole of it. Undoing the last few points of a gesture would
    /// make the key useless — you would press it over and over and watch the line retreat.
    /// </summary>
    [Fact]
    public void OneUndoTakesBackAWholeLine_NotItsLastFewPoints()
    {
        var selection = _Surface();
        _MarkOut(selection, 0, 0, 800, 600);

        selection.MarkWith(MarkTool.Stroke, true);
        selection.BeginDrag(100, 100);
        selection.DragTo(140, 180);
        selection.DragTo(220, 190);
        selection.EndDrag();

        selection.Undo();

        selection.Marks.Should().BeEmpty();
    }

    /// <summary>Every mark that has a colour is drawn in the ink that was chosen (AC-375).</summary>
    [Theory]
    [InlineData(MarkTool.Outline)]
    [InlineData(MarkTool.Arrow)]
    [InlineData(MarkTool.Highlight)]
    [InlineData(MarkTool.Stroke)]
    public void AMarkIsDrawnInTheChosenInk(MarkTool tool)
    {
        const uint red = 0xFFE5484D;
        var selection = _Surface();
        _MarkOut(selection, 0, 0, 800, 600);

        selection.ChooseInk(red);
        _DrawWith(selection, tool, 100, 100, 200, 120);

        _ColourOf(selection.Marks.Should().ContainSingle().Subject).Should().Be(red);
    }

    /// <summary>
    /// And what is already down keeps the ink it was drawn in. A mark is finished when the drag ends — this layer
    /// has a list and an undo, not a selected mark to recolour.
    /// </summary>
    [Fact]
    public void ChoosingAnInk_LeavesWhatIsAlreadyOnTheCaptureAlone()
    {
        const uint red = 0xFFE5484D;
        var selection = _Surface();
        _MarkOut(selection, 0, 0, 800, 600);
        _DrawFrame(selection, 100, 100, 60, 40);

        selection.ChooseInk(red);
        _DrawFrame(selection, 300, 300, 60, 40);

        _ColourOf(selection.Marks[0]).Should().Be(Accent, "the first was drawn before the ink changed");
        _ColourOf(selection.Marks[1]).Should().Be(red);
    }

    /// <summary>The weight scales the lines, in the order the operator would expect and never down to nothing.</summary>
    [Fact]
    public void TheWeightScalesTheLines()
    {
        var thicknesses = new List<int>();
        foreach (var weight in new[] { MarkWeight.Thin, MarkWeight.Medium, MarkWeight.Thick })
        {
            var selection = _Surface();
            _MarkOut(selection, 0, 0, 800, 600);
            selection.ChooseWeight(weight);
            _DrawFrame(selection, 100, 100, 60, 40);

            thicknesses.Add(selection.Marks.OfType<OutlineMark>().Single().Thickness);
        }

        thicknesses.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        thicknesses[0].Should().BeGreaterThan(0, "a line the operator asked to be thin is still a line");
    }

    /// <summary>
    /// A note's letters are not scaled by it (Raymond, 2026-07-27). A label is there to be read, and at "thin"
    /// that would not be a stylistic choice but an unreadable one.
    /// </summary>
    [Fact]
    public void ANotesLetters_AreNotScaledByTheWeight()
    {
        var sizes = new List<int>();
        foreach (var weight in new[] { MarkWeight.Thin, MarkWeight.Thick })
        {
            var selection = _Surface();
            _MarkOut(selection, 0, 0, 800, 600);
            selection.ChooseWeight(weight);
            selection.MarkWith(MarkTool.Text, true);
            selection.BeginDrag(200, 200);
            selection.Type("expected 12");
            selection.FinishTyping();

            sizes.Add(selection.Marks.OfType<TextMark>().Single().Size);
        }

        sizes[0].Should().Be(sizes[1]);
    }

    private static uint _ColourOf(Mark mark) => mark switch
    {
        OutlineMark outline => outline.Colour,
        ArrowMark arrow => arrow.Colour,
        StrokeMark stroke => stroke.Colour,
        HighlightMark highlight => highlight.Colour,
        TextMark note => note.Colour,
        _ => throw new NotSupportedException($"A {mark.GetType().Name} has no ink."),
    };

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

    /// <summary>Taken as two points rather than as a corner and a size, because that is what an arrow is — the size would throw away which end the head goes on.</summary>
    private static void _DrawArrow(ScreenshotSelectionViewModel selection, int fromX, int fromY, int toX, int toY)
    {
        selection.MarkWith(MarkTool.Arrow, true);
        selection.BeginDrag(fromX, fromY);
        selection.DragTo(toX, toY);
        selection.EndDrag();
    }

    private static void _DrawWith(
        ScreenshotSelectionViewModel selection, MarkTool tool, int x, int y, int width, int height)
    {
        selection.MarkWith(tool, true);
        selection.BeginDrag(x, y);
        selection.DragTo(x + width, y + height);
        selection.EndDrag();
    }

    private static ScreenshotSelectionViewModel _Surface(Func<CaptureRect, int>? brightnessUnder = null) =>
        new(
            new ScreenCapture { Image = [0x89, 0x50, 0x4E, 0x47], Displays = [Panel] }, 1920, 1080, Accent,
            lastRegion: null, windows: null, brightnessUnder)
        {
            SurfaceWidth = 1920,
            SurfaceHeight = 1080,
        };
}
