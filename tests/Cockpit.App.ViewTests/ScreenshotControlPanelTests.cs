using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using FluentAssertions;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Screenshots;

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
    /// Taking everything is a tool on the panel too, and pressing it does what A does — including lighting up
    /// afterwards, because a row where one button never marks itself reads as a broken one rather than as a
    /// meaningful difference.
    /// </summary>
    [Fact]
    public void PressingEverything_TakesTheWholeCapture_AndMarksItself() =>
        _Staged(ScreenshotSelectionScene.Idle, surface =>
        {
            var selection = _Model(surface);

            _Press(surface, surface.EverythingTool);

            selection.Selection.Should().Be(
                new CaptureRect(0, 0, selection.ImageWidth, selection.ImageHeight),
                "the whole capture, gaps and all, exactly as A marks it out");
            surface.EverythingTool.Classes.Should().Contain("active");
        });

    /// <summary>
    /// Exactly one tool is lit at a time, taking everything included: two at once stops answering the question
    /// the row is there for. Everything is technically a region as well, so this is the one place where what is
    /// true and what is worth saying come apart — and the row says the second.
    /// </summary>
    [Fact]
    public void WhileEverythingIsTaken_RegionIsNotAlsoLit() => _Staged(ScreenshotSelectionScene.Idle, surface =>
    {
        _Press(surface, surface.EverythingTool);

        surface.RegionTool.Classes.Should().NotContain("active");
    });

    /// <summary>
    /// And Region answers being pressed from there. It is a no-op on what is marked out — everything stays marked
    /// out — but it is not a no-op on which tool is in hand, and the row is what says which that is. Leaving it
    /// dark here would be the same dead button, one place along.
    /// </summary>
    [Fact]
    public void RegionPressedWhileEverythingIsTaken_TakesTheToolBack() =>
        _Staged(ScreenshotSelectionScene.Idle, surface =>
        {
            var selection = _Model(surface);
            _Press(surface, surface.EverythingTool);

            _Press(surface, surface.RegionTool);

            surface.RegionTool.Classes.Should().Contain("active");
            surface.EverythingTool.Classes.Should().NotContain("active");
            selection.Selection.Should().Be(
                new CaptureRect(0, 0, selection.ImageWidth, selection.ImageHeight),
                "what is marked out is untouched — this said which tool is in hand, not what to take");
        });

    /// <summary>The other two tools put it out as well, so the row never lights two at once whichever way it is reached.</summary>
    [Fact]
    public void AnotherToolAfterEverything_PutsItOut() => _Staged(ScreenshotSelectionScene.Idle, surface =>
    {
        _Press(surface, surface.EverythingTool);

        _Press(surface, surface.RedactTool);

        surface.RedactTool.Classes.Should().Contain("active", "there is a region to hide part of, so Hide goes on");
        surface.EverythingTool.Classes.Should().NotContain("active");
    });

    /// <summary>
    /// It goes out again on its own. Nothing turns it off — dragging out anything smaller simply makes it untrue,
    /// which is what taking everything being a selection rather than a mode has to mean at the panel.
    /// </summary>
    [Fact]
    public void DraggingSomethingSmaller_PutsEverythingOutAgain() => _Staged(ScreenshotSelectionScene.Idle, surface =>
    {
        _Press(surface, surface.EverythingTool);

        surface.MouseDown(new Point(SurfaceWidth * 0.3, SurfaceHeight * 0.5), MouseButton.Left);
        surface.MouseMove(new Point(SurfaceWidth * 0.6, SurfaceHeight * 0.8), RawInputModifiers.LeftMouseButton);
        surface.MouseUp(new Point(SurfaceWidth * 0.6, SurfaceHeight * 0.8), MouseButton.Left);

        surface.EverythingTool.Classes.Should().NotContain("active");
        surface.RegionTool.Classes.Should().Contain("active", "and the tool the drag belonged to says so instead");
    });

    /// <summary>
    /// Taking everything leaves the panel where it is. What it marks out covers both places the panel can go, so
    /// moving trades one covered spot for another — and an operator who just pressed a tool should not have to
    /// look for where the row went to press the next one.
    /// </summary>
    [Fact]
    public void TakingEverything_LeavesThePanelWhereItIs() => _Staged(ScreenshotSelectionScene.Idle, surface =>
    {
        var before = Canvas.GetTop(surface.Controls);

        _Press(surface, surface.EverythingTool);

        Canvas.GetTop(surface.Controls).Should().Be(before, "there is nowhere better to be when the selection is everything");
        before.Should().BeLessThan(SurfaceHeight / 2.0, "and where it is, is the top — this test says nothing otherwise");
    });

    /// <summary>
    /// Window mode comes off first, the way the key does it. Taking everything while the surface is still pointing
    /// at windows would leave the next move marking out a window over a selection that is already the whole screen.
    /// </summary>
    [Fact]
    public void PressingEverything_InWindowMode_LeavesIt() =>
        _Staged(ScreenshotSelectionScene.WindowPick, surface =>
        {
            var selection = _Model(surface);
            selection.PickingWindow.Should().BeTrue("the scene left the surface in window mode");

            _Press(surface, surface.EverythingTool);

            selection.PickingWindow.Should().BeFalse();
        });

    /// <summary>
    /// A mode this desktop can offer is offered — enabled, with its key beside its name. This is the promise the
    /// hint text used to carry in words ("W picks a window") and the panel now carries as a control: a mode nobody
    /// mentions is one the operator cannot use, which is what AC-220 was rejected for.
    /// </summary>
    [Fact]
    public void AnAvailableTool_IsOfferedRatherThanGreyedOut() => _Staged(ScreenshotSelectionScene.Idle, surface =>
    {
        _Model(surface).CanPickWindow.Should().BeTrue("the scene's stand-in desktop does say where its windows are");

        surface.WindowTool.IsEffectivelyEnabled.Should().BeTrue();
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
    /// A region under the panel leaves it exactly where it was. It used to step aside (AC-358), and that is what
    /// this test used to say — but nothing remembered where it had been, so every reason to move away became a
    /// reason to move back the moment it lapsed, and the row rocked between the two edges under the operator's
    /// hand. Staying put is worth more than never overlapping a picture that is frozen anyway.
    /// </summary>
    [Fact]
    public void ARegionUnderThePanel_LeavesItWhereItIs() => _Staged(ScreenshotSelectionScene.Redaction, surface =>
    {
        var selection = _Model(surface);
        var panel = new Rect(
            Canvas.GetLeft(surface.Controls), Canvas.GetTop(surface.Controls),
            surface.Controls.Bounds.Width, surface.Controls.Bounds.Height);

        Canvas.GetTop(surface.Controls).Should().BeLessThan(
            SurfaceHeight / 2.0, "it belongs at the top, and this scene does not move it");

        var marked = selection.Selection.Should().NotBeNull().And.Subject as CaptureRect?;
        _ToRect(selection.ToSurface(marked!.Value)).Intersects(panel).Should().BeTrue(
            "otherwise this scene's region does not reach the panel, and the test proves nothing about staying");
    });

    private static Rect _ToRect((double X, double Y, double Width, double Height) area) =>
        new(area.X, area.Y, area.Width, area.Height);

    /// <summary>
    /// A screen too small for the panel does not push it off the edge. It cannot be made to fit — the row of tools
    /// is wider than this window whatever it holds, and wider again with every tool this epic still adds — but
    /// centring alone would give a negative offset and cut the first tool off the left. Pinned to the corner
    /// instead, so the row starts where it can be reached.
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
