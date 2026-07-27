using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using FluentAssertions;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The control panel on the selection surface (AC-358): that its tools can be pressed, that pressing one does not
/// cost you the keyboard or start a drag underneath it, and that it puts itself where the operator is looking.
/// </summary>
/// <remarks>
/// Driven through the surface's real pointer and keys, on a shown window, because every one of these is about
/// what happens between a control and the window it sits on — the seam a view-model test cannot see.
/// </remarks>
[Collection("avalonia")]
public class ScreenshotControlPanelTests
{
    private const int SurfaceWidth = 1440;
    private const int SurfaceHeight = 900;

    /// <summary>A window narrower than the panel is, so the clamping has something to clamp.</summary>
    private const int NarrowWidth = 300;
    private const int NarrowHeight = 200;

    [Fact]
    public void PressingAToolChoosesIt_TheSameWayItsKeyDoes() => _Staged(ScreenshotSelectionScene.Idle, surface =>
    {
        var selection = _Model(surface);

        _Press(surface, surface.RedactTool);
        selection.RedactionNeedsARegion.Should().BeTrue("Hide was pressed with nothing marked out, which is the same refusal B gives");

        surface.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.None);
        _Press(surface, surface.RedactTool);
        selection.Redacting.Should().BeTrue("with a region marked out the tool goes on, exactly as B would");

        _Press(surface, surface.RegionTool);
        selection.DraggingRegion.Should().BeTrue("Region is the way back out of whichever tool was on");
    });

    /// <summary>
    /// A press on the panel's own text is a press on the panel. Landing on a tool is the easy half — a button
    /// answers that itself — but everything else on it would otherwise begin a drag underneath, from a point the
    /// operator cannot see.
    /// </summary>
    [Fact]
    public void APressOnThePanelsHint_DoesNotMarkOutARegion() =>
        _DraggingFromThePanel(surface => _Centre(surface, surface.HintText));

    /// <summary>
    /// And a press on the panel's own padding, which is the case the first version of this missed: the padding and
    /// the gaps between the rows have no child control under them, so the press resolves to the panel itself — and
    /// a guard that only asks about ancestors does not count the panel as one of its own.
    /// </summary>
    /// <remarks>
    /// Taken down the left edge rather than in from a corner. The panel is rounded, so a point just inside the
    /// corner of its bounding box is outside the shape that is drawn — you can see the desktop through it, and a
    /// drag starting there is starting on the picture, which is right.
    /// </remarks>
    [Fact]
    public void APressOnThePanelsPadding_DoesNotMarkOutARegion() =>
        _DraggingFromThePanel(surface => new Point(
            Canvas.GetLeft(surface.Controls) + 4,
            Canvas.GetTop(surface.Controls) + (surface.Controls.Bounds.Height / 2)));

    /// <summary>
    /// The keys keep working after the mouse has been used, Enter above all. A button that took focus would
    /// answer Enter by pressing itself again, so the key that takes the shot would quietly stop taking it — and
    /// the panel is meant to be the same surface said twice, not a choice between the two.
    /// </summary>
    [Fact]
    public void AToolChosenWithTheMouse_LeavesTheKeysWorking() => _Staged(ScreenshotSelectionScene.Idle, surface =>
    {
        var selection = _Model(surface);

        surface.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.None);
        _Press(surface, surface.RegionTool);
        surface.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);

        selection.Result.Should().NotBeNull("Enter still reaches the surface, and takes the shot, after a tool was clicked");
    });

    [Fact]
    public void RInAnotherTool_GoesBackToDraggingARegion() => _Staged(ScreenshotSelectionScene.WindowPick, surface =>
    {
        var selection = _Model(surface);
        selection.PickingWindow.Should().BeTrue("the scene left the surface in window mode");

        surface.KeyPressQwerty(PhysicalKey.R, RawInputModifiers.None);

        selection.DraggingRegion.Should().BeTrue();
    });

    /// <summary>
    /// The surface is one window across every screen, so the middle of it is a spot nobody is looking at. The
    /// panel belongs on the display the pointer is on.
    /// </summary>
    [Fact]
    public void ThePointersDisplay_IsWhereThePanelSits() => _Staged(ScreenshotSelectionScene.TwoDisplays, surface =>
    {
        var centre = Canvas.GetLeft(surface.Controls) + (surface.Controls.Bounds.Width / 2);

        centre.Should().BeGreaterThan(
            SurfaceWidth / 2.0,
            "the scene leaves the pointer on the right-hand screen, and the panel follows it there");
        centre.Should().BeApproximately(SurfaceWidth * 0.75, 2, "centred on that screen rather than merely on its side of the line");
    });

    /// <summary>
    /// Moved rather than faded when something is marked out under it: a panel you can see through is still a
    /// panel you cannot drag beneath, and the press would land on a tool.
    /// </summary>
    [Fact]
    public void ARegionUnderThePanel_MovesItOutOfTheWay() => _Staged(ScreenshotSelectionScene.Redaction, surface =>
    {
        var top = Canvas.GetTop(surface.Controls);

        top.Should().BeGreaterThan(
            SurfaceHeight / 2.0,
            "the region in this scene runs from near the top edge, straight through where the panel rests");
    });

    /// <summary>
    /// A screen too small for the panel does not push it off the edge. It cannot be made to fit — nothing can put
    /// a 456-unit panel inside a 300-unit window — but centring alone would give a negative offset and cut the
    /// first tool off the left. Pinned to the corner instead, so the row starts where it can be reached.
    /// </summary>
    [Fact]
    public void ADisplaySmallerThanThePanel_DoesNotPushItOffTheEdge() => _Staged(ScreenshotSelectionScene.Idle, surface =>
    {
        surface.Controls.Bounds.Width.Should().BeGreaterThan(NarrowWidth, "otherwise this window is not the small case at all");

        Canvas.GetLeft(surface.Controls).Should().Be(0, "a panel that cannot fit starts at the edge rather than before it");
        Canvas.GetTop(surface.Controls).Should().BeGreaterThanOrEqualTo(0);
    }, NarrowWidth, NarrowHeight);

    /// <summary>Pressed through the pointer rather than by raising Click, because half of what these tests are about is which control the press lands on.</summary>
    private static void _Press(ScreenshotSelectionWindow surface, Control tool)
    {
        var centre = _Centre(surface, tool);

        surface.MouseDown(centre, MouseButton.Left);
        surface.MouseUp(centre, MouseButton.Left);
    }

    private static Point _Centre(ScreenshotSelectionWindow surface, Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), surface)
        ?? throw new InvalidOperationException($"'{control.Name}' is not laid out on the surface.");

    /// <summary>A drag that starts somewhere on the panel and ends well away from it, which must leave nothing marked out.</summary>
    private static void _DraggingFromThePanel(Func<ScreenshotSelectionWindow, Point> start) =>
        _Staged(ScreenshotSelectionScene.Idle, surface =>
        {
            var away = new Point(SurfaceWidth * 0.8, SurfaceHeight * 0.8);

            surface.MouseDown(start(surface), MouseButton.Left);
            surface.MouseMove(away, RawInputModifiers.LeftMouseButton);
            surface.MouseUp(away, MouseButton.Left);

            _Model(surface).Selection.Should().BeNull("the drag started on the panel, which is not the picture");
        });

    private static void _Staged(
        string scene, Action<ScreenshotSelectionWindow> assert, int width = SurfaceWidth, int height = SurfaceHeight) =>
        HeadlessAvalonia.Run(() =>
    {
        var surface = Screenshotter.BuildScene(scene, width, height)
            .Should().BeOfType<ScreenshotSelectionWindow>().Subject;

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
