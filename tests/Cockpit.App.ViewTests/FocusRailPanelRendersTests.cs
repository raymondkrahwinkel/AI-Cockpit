using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Cockpit.App.Controls;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-443, on a render rather than on a claim: a real pointer drag on <see cref="FocusRailPanel"/>'s
/// divider actually widens the rail and folds its tiles from one column to two (not just what
/// <c>RailLayoutMath</c> predicts in isolation — that half is <c>RailLayoutMathTests</c>), a rail with more
/// tiles than fit scrolls vertically and never grows a horizontal scrollbar, and both minimums hold under a
/// drag that overshoots them. Screenshots land in %TEMP% for the PR, the same convention AC-442 used.
/// </summary>
[Collection("avalonia")]
public class FocusRailPanelRendersTests
{
    private const double FocusAspect = 1000.0 / 640.0; // the mockup's focus pane shape
    private const int TileCount = 6;

    // Chosen so the rail starts at ~280px in a 1000px window (mockup scene 1) — one column, since that's
    // under 2x `FocusRailPanel.MinRailWidth`.
    private const double NarrowRailWeight = 280.0 / (1000.0 - 8.0 - 280.0);

    public static readonly string OutputDirectory = Path.Combine(Path.GetTempPath(), "cockpit-ac443-focus-rail");

    [Fact]
    public void DraggingTheDividerLeft_WidensTheRailAndFoldsItToTwoColumns() => HeadlessAvalonia.Run(() =>
    {
        Directory.CreateDirectory(OutputDirectory);
        var (panel, rail, _) = _BuildTree();
        panel.RailWeight = NarrowRailWeight;

        var window = new Window { Content = panel, Width = 1000, Height = 640 };
        window.Show();
        window.UpdateLayout();
        _SaveFrame(window, "scene1-narrow-rail-one-column.png");

        Assert.Equal(1, rail.Geometry.Columns);

        // Mockup scene 2: drag the divider left, widening the rail past 2x its minimum tile width.
        var gutterX = panel.Children[0].Bounds.Width + 4;
        window.MouseDown(new Point(gutterX, 300), MouseButton.Left);
        window.MouseMove(new Point(gutterX - 140, 300), RawInputModifiers.LeftMouseButton);
        window.MouseUp(new Point(gutterX - 140, 300), MouseButton.Left);
        window.UpdateLayout();
        _SaveFrame(window, "scene2-wide-rail-two-columns.png");

        Assert.Equal(2, rail.Geometry.Columns);
        // Row-major fill (AC-443 #2 "live"): the second tile now sits beside the first, not below it.
        Assert.Equal(rail.Children[0].Bounds.Top, rail.Children[1].Bounds.Top);
        Assert.True(rail.Children[1].Bounds.Left > rail.Children[0].Bounds.Left);

        window.Close();
    });

    [Fact]
    public void MoreTilesThanFit_ScrollsVerticallyNeverHorizontally() => HeadlessAvalonia.Run(() =>
    {
        var (panel, _, scroll) = _BuildTree();
        panel.RailWeight = NarrowRailWeight;

        // Short window: six tiles stacked one per row cannot all fit.
        var window = new Window { Content = panel, Width = 1000, Height = 300 };
        window.Show();
        window.UpdateLayout();

        Assert.Equal(ScrollBarVisibility.Disabled, scroll.HorizontalScrollBarVisibility);
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height + 0.5,
            "six tiles at this height must overflow, or this test proves nothing about the scrollbar");
        Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 0.5, "the rail must never need horizontal scroll");

        window.Close();
    });

    [Fact]
    public void DraggingFarPastEitherSide_ClampsAtItsOwnMinimum() => HeadlessAvalonia.Run(() =>
    {
        var (panel, _, _) = _BuildTree();
        panel.RailWeight = NarrowRailWeight;

        var window = new Window { Content = panel, Width = 1000, Height = 640 };
        window.Show();
        window.UpdateLayout();

        var gutterX = panel.Children[0].Bounds.Width + 4;
        window.MouseDown(new Point(gutterX, 300), MouseButton.Left);
        window.MouseMove(new Point(gutterX - 5000, 300), RawInputModifiers.LeftMouseButton);
        window.MouseUp(new Point(gutterX - 5000, 300), MouseButton.Left);
        window.UpdateLayout();

        Assert.True(panel.Children[0].Bounds.Width >= FocusRailPanel.MinFocusWidth - 0.5,
            "dragging the rail as wide as possible must not starve the focus pane below its minimum");

        gutterX = panel.Children[0].Bounds.Width + 4;
        window.MouseDown(new Point(gutterX, 300), MouseButton.Left);
        window.MouseMove(new Point(gutterX + 5000, 300), RawInputModifiers.LeftMouseButton);
        window.MouseUp(new Point(gutterX + 5000, 300), MouseButton.Left);
        window.UpdateLayout();

        Assert.True(panel.Children[1].Bounds.Width >= FocusRailPanel.MinRailWidth - 0.5,
            "dragging the focus pane as wide as possible must not shrink the rail below one tile");

        window.Close();
    });

    private static (FocusRailPanel Panel, RailTilePanel Rail, ScrollViewer Scroll) _BuildTree()
    {
        var focus = new Border { Background = Brushes.SteelBlue };
        var rail = new RailTilePanel { FocusAspectRatio = FocusAspect, MinTileWidth = FocusRailPanel.MinRailWidth };
        for (var i = 0; i < TileCount; i++)
        {
            rail.Children.Add(new Border { Background = Brushes.Orange, BorderBrush = Brushes.Black, BorderThickness = new Thickness(1) });
        }

        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = rail,
        };

        var panel = new FocusRailPanel();
        panel.Children.Add(focus);
        panel.Children.Add(scroll);
        return (panel, rail, scroll);
    }

    private static void _SaveFrame(Window window, string fileName)
    {
        using var frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("the headless renderer produced no frame to sample");
        frame.Save(Path.Combine(OutputDirectory, fileName), PngBitmapEncoderOptions.Default);
    }
}
