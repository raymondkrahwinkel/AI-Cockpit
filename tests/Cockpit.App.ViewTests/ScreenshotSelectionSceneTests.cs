using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Headless;
using FluentAssertions;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The selection surface's harness scenes (AC-357): that each mode is actually reached, and that the desktop
/// they are rendered over is worth rendering over.
/// </summary>
/// <remarks>
/// These assert about the surface after it has been shown and driven, which is the part no other test touches.
/// The window's own tests stop at <c>Build</c> and the rest of the suite stops at the view model — which is how
/// the surface once shipped unable to open at all with 152 view tests green.
/// </remarks>
[Collection("avalonia")]
public class ScreenshotSelectionSceneTests
{
    private const int SurfaceWidth = 1440;
    private const int SurfaceHeight = 900;

    [Fact]
    public void TheRestingScene_HasNothingMarkedOutAndNoModeOn() => _Staged(ScreenshotSelectionScene.Idle, surface =>
    {
        var selection = _Model(surface);

        selection.Selection.Should().BeNull("the surface an operator first sees has nothing chosen on it");
        selection.PickingWindow.Should().BeFalse();
        selection.Redacting.Should().BeFalse();
    });

    /// <summary>
    /// The region is dragged out through the pointer, and comes back in the capture's pixels rather than the
    /// window's — the conversion that made AC-329 refuse most of a scaled screen, and the reason the stand-in
    /// capture is deliberately not the same size as the surface drawing it.
    /// </summary>
    [Fact]
    public void TheRegionScene_DragsOutARegionMeasuredInTheCapturesOwnPixels() => _Staged(ScreenshotSelectionScene.Region, surface =>
    {
        var selection = _Model(surface);

        selection.Selection.Should().NotBeNull("the scene drags a region out with the pointer");

        // A range rather than a number: the two ends of the drag are fractions of the surface, so the last pixel
        // either way is down to how those land — which is not what this is about. Half of it would be.
        selection.Selection.GetValueOrDefault().Width.Should().BeInRange(
            1380, 1384,
            "the drag crosses 48% of a 1440-unit surface onto a capture twice its size — 1382 pixels, not 691");
    });

    [Fact]
    public void TheWindowScene_HighlightsTheWindowUnderThePointer() => _Staged(ScreenshotSelectionScene.WindowPick, surface =>
    {
        var selection = _Model(surface);

        selection.PickingWindow.Should().BeTrue("the scene presses W to get into window mode");
        selection.HoveredWindow.Should().NotBeNull("the pointer is left over one of the stand-in windows");
        selection.Selection.Should().NotBeNull("a highlighted window marks its own rectangle out");
    });

    /// <summary>
    /// Redaction is refused until there is a region to hide part of, so a scene that only pressed B would render
    /// the refusal — which looks like a mode and is not one.
    /// </summary>
    [Fact]
    public void TheRedactionScene_GetsIntoRedactionAndLeavesBoxesBehind() => _Staged(ScreenshotSelectionScene.Redaction, surface =>
    {
        var selection = _Model(surface);

        selection.Redacting.Should().BeTrue("the scene marks out a region first, which is what B needs");
        selection.MarkingNeedsARegion.Should().BeFalse();
        selection.Marks.Should().HaveCount(2, "two boxes are dragged over the region")
            .And.AllBeOfType<RedactionMark>();
    });

    /// <summary>
    /// The desktop the surface is rendered over has somewhere genuinely light and somewhere genuinely dark
    /// <em>inside</em> the selection, measured off the frame rather than off the drawing code. A flat fill would
    /// make every dim, stroke and redaction box look right no matter how wrong it was — and looking at it is the
    /// entire point of the scene existing.
    /// </summary>
    [Fact]
    public void TheStandInDesktop_IsNotAFlatFillWhereTheOperatorIsLooking() => _Staged(ScreenshotSelectionScene.Region, surface =>
    {
        var sampled = _SampleInside(surface, 0.24, 0.28, 0.68, 0.72);

        sampled.Darkest.Should().BeLessThan(60, "the region covers part of a dark window");
        sampled.Lightest.Should().BeGreaterThan(200, "and part of a light one");
    });

    /// <summary>Two arrows, pointing opposite ways, so the scene shows both that the head follows the drag and that the mark carries over either half of the desktop.</summary>
    [Fact]
    public void TheArrowScene_LeavesTwoArrowsPointingOppositeWays() => _Staged(ScreenshotSelectionScene.Arrow, surface =>
    {
        var arrows = _Model(surface).Marks.Should().HaveCount(2).And.AllBeOfType<ArrowMark>().Which.ToList();

        (arrows[0].To.X - arrows[0].From.X).Should().BeNegative("the first is dragged to the left");
        (arrows[1].To.X - arrows[1].From.X).Should().BePositive("and the second back to the right");
    });

    /// <summary>
    /// The arrow can be seen over the dark half of the desktop and over the light half (AC-360). This is the one
    /// thing about the tool that no assertion about the mark can reach: an arrow that vanishes into a terminal is
    /// still an arrow by every measure except the only one that matters.
    /// </summary>
    /// <remarks>
    /// Read off the rendered frame in two small windows the arrows are known to cross, and asked in the terms each
    /// half makes possible. Over the dark editor the ring is what carries, and nothing else there comes near
    /// white — the brightest thing the stand-in draws on it is grey text. Over the light document the ring is
    /// invisible and the body carries instead, so what is looked for is colour rather than brightness: that page
    /// and its text are drawn in greys, and a strongly coloured pixel on it can only be the arrow.
    /// </remarks>
    [Fact]
    public void TheArrowStaysReadable_OverTheDarkHalfAndOverTheLightHalf() =>
        _Staged(ScreenshotSelectionScene.Arrow, surface =>
        {
            var onTheDarkEditor = _SampleInside(surface, 0.30, 0.53, 0.36, 0.59, step: 1);
            var onTheLightDocument = _SampleInside(surface, 0.84, 0.445, 0.875, 0.49, step: 1);

            onTheDarkEditor.WidestColourSpread.Should().BeGreaterThan(
                100, "the arrow's ink is the only strongly coloured thing on a window drawn in greys");
            onTheLightDocument.WidestColourSpread.Should().BeGreaterThan(
                100, "and the same on the page, which is drawn in greys too");
        });

    /// <summary>
    /// One wash of each direction (AC-361). The scene drags a band over the light document and another over the
    /// dark terminal, and the surface has to have read the picture under each to tell them apart — a scene with
    /// one band would show a tool that works and prove nothing about the half it does not.
    /// </summary>
    [Fact]
    public void TheHighlightScene_LeavesOneWashOfEachDirection() => _Staged(ScreenshotSelectionScene.Highlight, surface =>
    {
        var washes = _Model(surface).Marks.Should().HaveCount(2).And.AllBeOfType<HighlightMark>().Which.ToList();

        washes.Select(wash => wash.Blend).Should().BeEquivalentTo(
            [HighlightBlend.Darken, HighlightBlend.Lighten],
            "the document is light and the terminal is dark, and the surface looked");
    });

    /// <summary>
    /// The band shows and what is under it still reads (AC-361) — measured on the rendered frame over the light
    /// half, which is where a wash is most easily got wrong: too strong and it is the box that hides, drawn in a
    /// friendlier colour.
    /// </summary>
    [Fact]
    public void TheWashShowsOverThePage_WithoutSwallowingTheTextUnderIt() =>
        _Staged(ScreenshotSelectionScene.Highlight, surface =>
        {
            var band = _SampleInside(surface, 0.60, 0.307, 0.88, 0.345, step: 1);

            band.Lightest.Should().BeLessThan(
                240, "the page under the band took the colour, so the band can be seen at all");
            (band.Lightest - band.Darkest).Should().BeGreaterThan(
                60, "and the line of text under it is still far darker than the band it lies on");
        });

    /// <summary>
    /// Two freehand lines, each of them one press (AC-362) and each a curve rather than a chain of segments. The
    /// scene draws them the way a hand does, in many small moves round a shape.
    /// </summary>
    [Fact]
    public void TheStrokeScene_LeavesTwoLines_EachOfThemACurve() =>
        _Staged(ScreenshotSelectionScene.Stroke, surface =>
        {
            var lines = _Model(surface).Marks.Should().HaveCount(2).And.AllBeOfType<StrokeMark>().Which.ToList();

            foreach (var line in lines)
            {
                line.Points.Should().HaveCountGreaterThan(20, "a ring is not two points and a hope");
                line.Curve().Should().HaveCount(line.Thinned().Count - 1, "one length of curve between each pair");
            }
        });

    /// <summary>
    /// The line reads over the dark half of the desktop, which it only does because of the ring around it — the
    /// accent alone on a near-black terminal is the case this tool most easily disappears into.
    /// </summary>
    [Fact]
    public void TheStrokeStaysReadable_OverTheDarkHalf() => _Staged(ScreenshotSelectionScene.Stroke, surface =>
    {
        var acrossTheTerminal = _SampleInside(surface, 0.60, 0.685, 0.86, 0.715, step: 1);

        acrossTheTerminal.WidestColourSpread.Should().BeGreaterThan(
            100, "the line's ink is the only strongly coloured thing on that window");
    });

    /// <summary>
    /// Two notes, typed through the window's own text input (AC-363) — and one of them is a word made of the
    /// surface's own shortcuts, so the scene renders the case that would otherwise pick a window and take the shot.
    /// </summary>
    [Fact]
    public void TheTextScene_LeavesTwoNotes_OneOfThemAWordMadeOfShortcuts() =>
        _Staged(ScreenshotSelectionScene.Text, surface =>
        {
            var selection = _Model(surface);
            var notes = selection.Marks.Should().HaveCount(2).And.AllBeOfType<TextMark>().Which.ToList();

            notes[0].Text.Should().Be("Window is empty here");
            selection.PickingWindow.Should().BeFalse("typing it picked no window");
            selection.IsClosed.Should().BeFalse("and took no shot");
        });

    /// <summary>The scenes that were already there still build, including the fallback an unknown name lands on.</summary>
    [Theory]
    [InlineData(null, typeof(MainWindow))]
    [InlineData("session", typeof(MainWindow))]
    [InlineData("options", typeof(OptionsDialog))]
    [InlineData("projects", typeof(ProjectsDialog))]
    [InlineData("new-session", typeof(NewSessionDialog))]
    public void TheHarnessStillBuildsTheOtherScenes(string? scene, Type expected) => HeadlessAvalonia.Run(() =>
        Screenshotter.BuildScene(scene, SurfaceWidth, SurfaceHeight).Should().BeOfType(expected));

    /// <summary>
    /// What the rendered frame holds in one part of itself: its darkest and lightest pixel as an average of the
    /// colour channels, and the strongest colour cast any pixel there has.
    /// </summary>
    /// <remarks>
    /// Sampled on a grid by default, because a screen's worth of pixels is a lot to walk for a question about
    /// whether there is any contrast at all. A caller looking for something as thin as a drawn line asks for every
    /// pixel instead — a grid of four steps straight over a stroke a few units wide finds it only by luck.
    /// </remarks>
    private static (int Darkest, int Lightest, int WidestColourSpread) _SampleInside(
        Window surface, double left, double top, double right, double bottom, int step = 4)
    {
        using var frame = surface.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("The headless renderer produced no frame to sample.");
        using var buffer = frame.Lock();

        var bytesPerPixel = buffer.RowBytes / buffer.Size.Width;
        var row = new byte[buffer.RowBytes];
        var darkest = int.MaxValue;
        var lightest = int.MinValue;
        var spread = 0;

        for (var y = (int)(buffer.Size.Height * top); y < (int)(buffer.Size.Height * bottom); y += step)
        {
            Marshal.Copy(buffer.Address + (y * buffer.RowBytes), row, 0, row.Length);

            for (var x = (int)(buffer.Size.Width * left); x < (int)(buffer.Size.Width * right); x += step)
            {
                // The first three channels whichever way round they sit: a sum of red, green and blue is the
                // same number in BGRA as in RGBA, and so is the distance between the largest and the smallest of
                // them. Only the alpha has to stay out of it.
                int first = row[(x * bytesPerPixel) + 0];
                int second = row[(x * bytesPerPixel) + 1];
                int third = row[(x * bytesPerPixel) + 2];

                darkest = Math.Min(darkest, (first + second + third) / 3);
                lightest = Math.Max(lightest, (first + second + third) / 3);
                spread = Math.Max(spread, Math.Max(first, Math.Max(second, third)) - Math.Min(first, Math.Min(second, third)));
            }
        }

        return (darkest, lightest, spread);
    }

    private static void _Staged(string scene, Action<ScreenshotSelectionWindow> assert) => HeadlessAvalonia.Run(() =>
    {
        var surface = Screenshotter.BuildScene(scene, SurfaceWidth, SurfaceHeight)
            .Should().BeOfType<ScreenshotSelectionWindow>("the harness builds the selection surface for this scene").Subject;

        surface.Show();
        try
        {
            ScreenshotSelectionScene.Stage(surface, scene);
            assert(surface);
        }
        finally
        {
            surface.Close();
        }
    });

    private static ScreenshotSelectionViewModel _Model(ScreenshotSelectionWindow surface) =>
        surface.DataContext as ScreenshotSelectionViewModel
        ?? throw new InvalidOperationException("The surface was built without its view model.");
}
