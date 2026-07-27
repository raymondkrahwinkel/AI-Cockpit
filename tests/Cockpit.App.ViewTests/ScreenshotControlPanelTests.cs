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
    /// A press anywhere on the panel is a press on the panel. Landing on a tool is the easy half — a button
    /// answers that itself — but the gaps between them, the hint text and the padding are all still the panel,
    /// and a drag begun there would run underneath it from a point the operator cannot see.
    /// </summary>
    [Fact]
    public void DraggingFromThePanelItselfDoesNotMarkOutAnything() => _Staged(ScreenshotSelectionScene.Idle, surface =>
    {
        var selection = _Model(surface);
        var start = _Centre(surface, surface.HintText);

        surface.MouseDown(start, MouseButton.Left);
        surface.MouseMove(new Point(SurfaceWidth * 0.8, SurfaceHeight * 0.8), RawInputModifiers.LeftMouseButton);
        surface.MouseUp(new Point(SurfaceWidth * 0.8, SurfaceHeight * 0.8), MouseButton.Left);

        selection.Selection.Should().BeNull("the drag started on the panel, which is not the picture");
    });

    /// <summary>
    /// The keys keep working after the mouse has been used, Enter above all. A button that took focus would
    /// answer Enter by pressing itself again, so the key that takes the shot would quietly stop taking it — and
    /// the panel is meant to be the same surface said twice, not a choice between the two.
    /// </summary>
    [Fact]
    public void ChoosingAToolWithTheMouseLeavesTheKeysWorking() => _Staged(ScreenshotSelectionScene.Idle, surface =>
    {
        var selection = _Model(surface);

        surface.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.None);
        _Press(surface, surface.RegionTool);
        surface.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);

        selection.Result.Should().NotBeNull("Enter still reaches the surface, and takes the shot, after a tool was clicked");
    });

    [Fact]
    public void RGoesBackToDraggingARegion() => _Staged(ScreenshotSelectionScene.WindowPick, surface =>
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
    public void ThePanelSitsOnTheDisplayThePointerIsOn() => _Staged(ScreenshotSelectionScene.TwoDisplays, surface =>
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
    public void ThePanelGetsOutOfTheWayOfWhatIsMarkedOut() => _Staged(ScreenshotSelectionScene.Redaction, surface =>
    {
        var top = Canvas.GetTop(surface.Controls);

        top.Should().BeGreaterThan(
            SurfaceHeight / 2.0,
            "the region in this scene runs from near the top edge, straight through where the panel rests");
    });

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

    private static void _Staged(string scene, Action<ScreenshotSelectionWindow> assert) => HeadlessAvalonia.Run(() =>
    {
        var surface = Screenshotter.BuildScene(scene, SurfaceWidth, SurfaceHeight)
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
