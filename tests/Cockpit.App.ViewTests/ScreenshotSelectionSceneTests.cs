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
        var (darkest, lightest) = _BrightnessInside(surface, 0.24, 0.28, 0.68, 0.72);

        darkest.Should().BeLessThan(60, "the region covers part of a dark window");
        lightest.Should().BeGreaterThan(200, "and part of a light one");
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
    /// The darkest and lightest pixel in a part of the rendered frame, as an average of the colour channels.
    /// Sampled on a grid rather than every pixel: a screen's worth of them is a lot to walk for a question about
    /// whether there is any contrast at all.
    /// </summary>
    private static (int Darkest, int Lightest) _BrightnessInside(
        Window surface, double left, double top, double right, double bottom)
    {
        using var frame = surface.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("The headless renderer produced no frame to sample.");
        using var buffer = frame.Lock();

        var bytesPerPixel = buffer.RowBytes / buffer.Size.Width;
        var row = new byte[buffer.RowBytes];
        var darkest = int.MaxValue;
        var lightest = int.MinValue;

        for (var y = (int)(buffer.Size.Height * top); y < (int)(buffer.Size.Height * bottom); y += 4)
        {
            Marshal.Copy(buffer.Address + (y * buffer.RowBytes), row, 0, row.Length);

            for (var x = (int)(buffer.Size.Width * left); x < (int)(buffer.Size.Width * right); x += 4)
            {
                // The first three channels whichever way round they sit: a sum of red, green and blue is the
                // same number in BGRA as in RGBA, and only the alpha has to stay out of it.
                var brightness = (row[(x * bytesPerPixel) + 0] + row[(x * bytesPerPixel) + 1] + row[(x * bytesPerPixel) + 2]) / 3;
                darkest = Math.Min(darkest, brightness);
                lightest = Math.Max(lightest, brightness);
            }
        }

        return (darkest, lightest);
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
