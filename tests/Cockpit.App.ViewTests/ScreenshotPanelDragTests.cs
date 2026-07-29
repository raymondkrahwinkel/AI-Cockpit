using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

/// <summary>
/// Moving the control panels by hand (AC-374). They are two now — what is being taken, and what is being put on
/// it — and each goes where the operator puts it.
/// </summary>
/// <remarks>
/// The panels follow the display the pointer is on, which is what AC-358 built them to do and is right until the
/// one you want to look under is the one in the way. Being dragged is the only statement about where a panel
/// belongs that beats that, so it has to switch the following off — an earlier version that moved on its own and
/// remembered nothing rocked between two edges under the operator's hand, and that is the failure to stay clear of.
/// </remarks>
[Collection("avalonia")]
public class ScreenshotPanelDragTests
{
    private const int SurfaceWidth = 1440;
    private const int SurfaceHeight = 900;

    /// <summary>Dragging a panel moves it, and by exactly what the hand moved.</summary>
    [Fact]
    public void APanelDraggedByItsBody_GoesWhereItIsPut() => _OnTheSurface(surface =>
    {
        var from = _SomewhereOn(surface.MarkControls, surface);
        var before = new Point(Canvas.GetLeft(surface.MarkControls), Canvas.GetTop(surface.MarkControls));

        _Drag(surface, from, from + new Vector(120, 240));

        Assert.True(Math.Abs(Canvas.GetLeft(surface.MarkControls) - (before.X + 120)) <= 1);
        Assert.True(Math.Abs(Canvas.GetTop(surface.MarkControls) - (before.Y + 240)) <= 1);
    });

    /// <summary>
    /// And it stays there when the pointer wanders. Without this the first move after letting go would put it
    /// straight back, which is the panel arguing with the operator about where it belongs.
    /// </summary>
    [Fact]
    public void APanelPlacedByHand_StopsFollowingThePointer() => _OnTheSurface(surface =>
    {
        var from = _SomewhereOn(surface.MarkControls, surface);
        _Drag(surface, from, from + new Vector(150, 300));
        var placed = new Point(Canvas.GetLeft(surface.MarkControls), Canvas.GetTop(surface.MarkControls));

        surface.MouseMove(new Point(SurfaceWidth * 0.2, SurfaceHeight * 0.8));

        Assert.Equal(placed.X, Canvas.GetLeft(surface.MarkControls));
        Assert.Equal(placed.Y, Canvas.GetTop(surface.MarkControls));
    });

    /// <summary>The other panel is untouched by it — that is the whole point of there being two.</summary>
    [Fact]
    public void MovingOnePanel_LeavesTheOtherWhereItWas() => _OnTheSurface(surface =>
    {
        var before = new Point(Canvas.GetLeft(surface.Controls), Canvas.GetTop(surface.Controls));
        var from = _SomewhereOn(surface.MarkControls, surface);

        _Drag(surface, from, from + new Vector(200, 300));

        Assert.Equal(before.X, Canvas.GetLeft(surface.Controls));
        Assert.Equal(before.Y, Canvas.GetTop(surface.Controls));
    });

    /// <summary>
    /// Dragging a panel marks nothing out. The press that starts it lands on the panel, so there is no drag on the
    /// picture to begin — and a panel moved across the capture must not leave a rectangle behind it.
    /// </summary>
    [Fact]
    public void DraggingAPanel_MarksNothingOut() => _OnTheSurface(surface =>
    {
        var from = _SomewhereOn(surface.MarkControls, surface);

        _Drag(surface, from, from + new Vector(300, 200));

        Assert.Null(_Model(surface).Selection);
    });

    /// <summary>
    /// A press on a tool is that tool's. Pressing a button and shifting a pixel while doing it has to choose the
    /// tool rather than pick the panel up — which is most presses on a panel, by area and by intent.
    /// </summary>
    /// <remarks>
    /// Nothing in this app makes that true: a button handles its own press, so it never reaches the window that
    /// would start the drag. This first had a guard against it, which a mutation showed to be unreachable — the
    /// guard came out and the test stayed, because what it holds is the behaviour and not the guard.
    /// </remarks>
    [Fact]
    public void APressOnATool_ChoosesIt_RatherThanPickingThePanelUp() => _OnTheSurface(surface =>
    {
        var selection = _Model(surface);
        surface.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.None);
        var before = new Point(Canvas.GetLeft(surface.MarkControls), Canvas.GetTop(surface.MarkControls));
        var onTheTool = _Centre(surface, surface.OutlineTool);

        surface.MouseDown(onTheTool, MouseButton.Left);
        surface.MouseMove(onTheTool + new Vector(3, 3), RawInputModifiers.LeftMouseButton);
        surface.MouseUp(onTheTool + new Vector(3, 3), MouseButton.Left);

        Assert.True(selection.Outlining, "the press chose the tool");
        Assert.Equal(before.X, Canvas.GetLeft(surface.MarkControls));
    });

    /// <summary>A panel cannot be dragged out of reach — one half off the window is a tool you cannot press.</summary>
    [Fact]
    public void APanelCannotBeDraggedOffTheWindow() => _OnTheSurface(surface =>
    {
        var from = _SomewhereOn(surface.MarkControls, surface);

        _Drag(surface, from, new Point(SurfaceWidth + 400, SurfaceHeight + 400));

        var left = Canvas.GetLeft(surface.MarkControls);
        var top = Canvas.GetTop(surface.MarkControls);

        Assert.True(left <= SurfaceWidth - surface.MarkControls.Bounds.Width);
        Assert.True(top <= SurfaceHeight - surface.MarkControls.Bounds.Height);
        Assert.True(left >= 0);
        Assert.True(top >= 0);
    });

    /// <summary>Both panels fit on the display they are put on — the promise AC-358 made, now made twice.</summary>
    [Fact]
    public void BothPanelsFitOnTheDisplayTheyArePutOn() => _OnTheSurface(surface =>
    {
        foreach (var panel in new[] { surface.Controls, surface.MarkControls })
        {
            Assert.True(panel.Bounds.Width <= SurfaceWidth / 2.0, "a screen may be half the surface");
            Assert.True(Canvas.GetLeft(panel) + panel.Bounds.Width <= SurfaceWidth);
            Assert.True(Canvas.GetTop(panel) + panel.Bounds.Height <= SurfaceHeight);
        }
    });

    /// <summary>They do not sit on top of each other where nobody has moved them.</summary>
    [Fact]
    public void LeftWhereTheyWerePut_ThePanelsDoNotOverlap() => _OnTheSurface(surface =>
    {
        var taking = _BoundsOf(surface.Controls);
        var marking = _BoundsOf(surface.MarkControls);

        Assert.False(taking.Intersects(marking));
    });

    /// <summary>
    /// Pressing an ink marks it and unmarks the one that was on (AC-375). Exactly one is lit, the same promise the
    /// tool row makes — a palette where two look chosen answers nothing.
    /// </summary>
    [Fact]
    public void PressingAnInk_MarksItAndUnmarksTheOther() => _OnTheSurface(surface =>
    {
        Assert.Contains("active", surface.InkAccent.Classes);

        _Press(surface, surface.InkRed);

        Assert.Contains("active", surface.InkRed.Classes);
        Assert.DoesNotContain("active", surface.InkAccent.Classes);
    });

    /// <summary>The same for the line weights, which start at what every mark was drawn at before there was a choice.</summary>
    [Fact]
    public void PressingAWeight_MarksItAndUnmarksTheOther() => _OnTheSurface(surface =>
    {
        Assert.Contains("active", surface.WeightMedium.Classes);

        _Press(surface, surface.WeightThick);

        Assert.Contains("active", surface.WeightThick.Classes);
        Assert.DoesNotContain("active", surface.WeightMedium.Classes);
        Assert.Equal(MarkWeight.Thick, _Model(surface).Weight);
    });

    /// <summary>Pressing one picks up no panel — they are on a panel, and a press on a control is that control's.</summary>
    [Fact]
    public void PressingAnInk_DoesNotPickThePanelUp() => _OnTheSurface(surface =>
    {
        var before = Canvas.GetLeft(surface.MarkControls);

        _Press(surface, surface.InkGreen);

        Assert.Equal(before, Canvas.GetLeft(surface.MarkControls));
    });

    private static void _Press(ScreenshotSelectionWindow surface, Control control)
    {
        var centre = _Centre(surface, control);

        surface.MouseDown(centre, MouseButton.Left);
        surface.MouseUp(centre, MouseButton.Left);
    }

    private static Rect _BoundsOf(Control panel) =>
        new(Canvas.GetLeft(panel), Canvas.GetTop(panel), panel.Bounds.Width, panel.Bounds.Height);

    /// <summary>
    /// A point on the panel that is not on a tool: just inside its left edge, level with its middle. Down the
    /// edge rather than in from a corner — the panel is rounded, so a point inside the corner of its bounding box
    /// is outside the shape that is drawn.
    /// </summary>
    private static Point _SomewhereOn(Control panel, ScreenshotSelectionWindow surface)
    {
        _ = surface;

        return new Point(Canvas.GetLeft(panel) + 4, Canvas.GetTop(panel) + (panel.Bounds.Height / 2));
    }

    private static Point _Centre(ScreenshotSelectionWindow surface, Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), surface)
        ?? throw new InvalidOperationException($"'{control.Name}' is not laid out on the surface.");

    private static void _Drag(ScreenshotSelectionWindow surface, Point from, Point to)
    {
        surface.MouseDown(from, MouseButton.Left);
        surface.MouseMove(to, RawInputModifiers.LeftMouseButton);
        surface.MouseUp(to, MouseButton.Left);
    }

    private static void _OnTheSurface(Action<ScreenshotSelectionWindow> assert) => HeadlessAvalonia.Run(() =>
    {
        var surface = Assert.IsType<ScreenshotSelectionWindow>(Screenshotter.BuildScene(ScreenshotSelectionScene.Idle, SurfaceWidth, SurfaceHeight));

        surface.Show();
        try
        {
            // Put the pointer somewhere definite first: the panels are placed on the display it is on, and every
            // assertion below is about where they went from there.
            surface.MouseMove(new Point(SurfaceWidth * 0.5, SurfaceHeight * 0.5));
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
