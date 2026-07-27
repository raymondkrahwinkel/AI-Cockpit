using Avalonia;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Input;
using FluentAssertions;
using Cockpit.App.Views;

// The shape, not the file system's — both are in scope through the implicit usings.
using Path = Avalonia.Controls.Shapes.Path;

namespace Cockpit.App.ViewTests;

/// <summary>
/// What the marks are drawn <em>with</em> on the surface (AC-360). Every other test of the mark layer stops at the
/// view model, where a mark is a record; this is the seam where each one has to become a shape on a canvas.
/// </summary>
/// <remarks>
/// It exists because the shapes are kept and reused between draws, which was free while every mark was a
/// rectangle and is not free now that one of them is a path. A shape reused across a change of kind is either an
/// exception or, worse, the previous mark still on screen under the new one's colours.
/// </remarks>
[Collection("avalonia")]
public class ScreenshotMarkShapeTests
{
    private const int SurfaceWidth = 1440;
    private const int SurfaceHeight = 900;

    /// <summary>An arrow is drawn with a path, because nothing else can hold that shape.</summary>
    [Fact]
    public void AnArrow_IsDrawnWithAPath() => _OnTheSurface(surface =>
    {
        _Region(surface);
        _MarkWith(surface, PhysicalKey.P, new Point(400, 300), new Point(700, 500));

        _Drawing(surface).OfType<Path>().Should().ContainSingle("the arrow is the only mark on the surface");
    });

    /// <summary>
    /// And a frame drawn where an arrow used to be is a rectangle again. The shapes are reused as marks come and
    /// go, so this is the case where reuse is not possible — and a version that only ever added shapes would leave
    /// the arrow's path sitting there, holding its old geometry, restyled as a frame.
    /// </summary>
    [Fact]
    public void AFrameDrawnWhereAnArrowWas_IsDrawnWithARectangleAgain() => _OnTheSurface(surface =>
    {
        _Region(surface);
        _MarkWith(surface, PhysicalKey.P, new Point(400, 300), new Point(700, 500));
        surface.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);

        // Started somewhere else on purpose: a second press on the point the last one began at is a double click,
        // and a double click inside what is marked out takes the shot and closes the surface.
        _MarkWith(surface, PhysicalKey.O, new Point(500, 350), new Point(800, 560));

        _Drawing(surface).OfType<Path>().Should().BeEmpty("no path is left drawing anything");
        _Drawing(surface).Should().ContainSingle("and the frame is drawn with a rectangle");
    });

    /// <summary>Every shape that is currently drawing something, which is what an operator sees — the emptied ones are kept but paint nothing.</summary>
    private static IReadOnlyList<Shape> _Drawing(ScreenshotSelectionWindow surface) =>
        surface.Shade.Children
            .OfType<Shape>()
            .Where(shape => shape != surface.Marquee && shape.Name is null)
            .Where(shape => shape is Path path ? path.Data is not null : shape.Width > 0 && shape.Height > 0)
            .ToList();

    private static void _Region(ScreenshotSelectionWindow surface) =>
        _Drag(surface, new Point(SurfaceWidth * 0.1, SurfaceHeight * 0.1), new Point(SurfaceWidth * 0.9, SurfaceHeight * 0.9));

    private static void _MarkWith(ScreenshotSelectionWindow surface, PhysicalKey tool, Point from, Point to)
    {
        surface.KeyPressQwerty(tool, RawInputModifiers.None);
        _Drag(surface, from, to);
    }

    private static void _Drag(ScreenshotSelectionWindow surface, Point from, Point to)
    {
        surface.MouseDown(from, MouseButton.Left);
        surface.MouseMove(to, RawInputModifiers.LeftMouseButton);
        surface.MouseUp(to, MouseButton.Left);
    }

    private static void _OnTheSurface(Action<ScreenshotSelectionWindow> assert) => HeadlessAvalonia.Run(() =>
    {
        var surface = Screenshotter.BuildScene(ScreenshotSelectionScene.Idle, SurfaceWidth, SurfaceHeight)
            .Should().BeOfType<ScreenshotSelectionWindow>().Subject;

        surface.Show();
        try
        {
            assert(surface);
        }
        finally
        {
            surface.Close();
        }
    });
}
