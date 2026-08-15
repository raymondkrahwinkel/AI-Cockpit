using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Cockpit.App.Views;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewTests;

// AC-778's mini-gallery, exercised through the same `Build` seam `FilePreviewWindow`/`ScreenshotPreviewWindow`
// use — the harness's own step, not a real click through `TranscriptRowView`.
[Collection("avalonia")]
public sealed class ImagePreviewWindowTests
{
    private static readonly ImageAttachment Image = new("image/png", TinyPngBase64);

    private static TextBlock _CountText(Window window) =>
        window.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Name == "CountText");

    private static Control _NavigationRow(Window window) =>
        window.GetLogicalDescendants().OfType<Control>().First(c => c.Name == "NavigationRow");

    private static ScrollViewer _Scroller(Window window) =>
        window.GetLogicalDescendants().OfType<ScrollViewer>().First(s => s.Name == "BodyScroller");

    private static Image _PreviewImage(Window window) =>
        window.GetLogicalDescendants().OfType<Image>().First(i => i.Name == "PreviewImage");

    private static LayoutTransformControl _ZoomBox(Window window) =>
        window.GetLogicalDescendants().OfType<LayoutTransformControl>().First(c => c.Name == "PreviewImageZoom");

    private static ScaleTransform _ZoomTransform(Window window) =>
        (ScaleTransform)_ZoomBox(window).LayoutTransform!;

    private static Point _Centre(Window window, Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
        ?? throw new InvalidOperationException($"'{control.Name}' is not laid out in the window.");

    private static void _Drag(Window window, Point from, Point to)
    {
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(to, RawInputModifiers.LeftMouseButton);
        window.MouseUp(to, MouseButton.Left);
    }

    [Fact]
    public async Task ASingleImage_ShowsNoNavigation()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var window = ImagePreviewWindow.Build([Image], 0);
            await Task.Delay(200);

            Assert.Equal("Image", _CountText(window).Text);
            Assert.False(_NavigationRow(window).IsVisible);
        });
    }

    [Fact]
    public async Task MultipleImages_ShowsPositionAndNavigatesBothWays()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var window = ImagePreviewWindow.Build([Image, Image, Image], 0);
            await Task.Delay(200);

            Assert.Equal("Image 1 of 3", _CountText(window).Text);
            Assert.True(_NavigationRow(window).IsVisible);

            var next = window.GetLogicalDescendants().OfType<Button>().First(b => b.Name == "NextButton");
            next.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(50);

            Assert.Equal("Image 2 of 3", _CountText(window).Text);

            var previous = window.GetLogicalDescendants().OfType<Button>().First(b => b.Name == "PreviousButton");
            previous.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(50);

            Assert.Equal("Image 1 of 3", _CountText(window).Text);
        });
    }

    [Fact]
    public async Task StartIndex_OpensOnThatImage()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var window = ImagePreviewWindow.Build([Image, Image], 1);
            await Task.Delay(200);

            Assert.Equal("Image 2 of 2", _CountText(window).Text);
        });
    }

    // AC-778 follow-up: Ctrl+scroll zoom's only branchy logic is the clamp — invoked directly since Avalonia's
    // headless input harness has no way to raise a wheel event carrying `KeyModifiers.Control`.
    [Fact]
    public async Task CtrlScrollZoom_ClampsToConfiguredRange()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var window = ImagePreviewWindow.Build([Image], 0);
            await Task.Delay(200);

            var applyZoom = typeof(ImagePreviewWindow).GetMethod("_ApplyZoom", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var zoomTransform = _ZoomTransform(window);

            applyZoom.Invoke(window, [50.0]);
            Assert.Equal(6.0, zoomTransform.ScaleX);

            applyZoom.Invoke(window, [0.001]);
            Assert.Equal(0.2, zoomTransform.ScaleX);
        });
    }

    // AC-778 follow-up: the window otherwise has no OS-drawn edge at all (AC-678) — rendered and sampled rather
    // than only asserting a property, since a brush wired to the wrong element would still pass a property check.
    [Fact]
    public async Task Window_HasAVisibleOuterBorder()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var window = ImagePreviewWindow.Build([Image], 0);
            window.Show();
            await Task.Delay(200);

            var edge = RenderedScene.PaintedAt(window, new Point(0, 300));
            var interior = RenderedScene.PaintedAt(window, new Point(10, 300));

            Assert.Equal(RenderedScene.Token("CockpitHairlineColor"), edge);
            Assert.NotEqual(edge, interior);
        });
    }

    // AC-778 follow-up 2: Fit and 1:1 must put visibly different sizes on screen — this window has now had two
    // click regressions after a zoom change, so this measures the laid-out image box rather than a property.
    [Fact]
    public async Task FitAndActualSize_LayOutTheImageDifferently()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var window = ImagePreviewWindow.Build([_Png(1200, 900)], 0);
            window.Show();
            await Task.Delay(200);

            var image = window.GetLogicalDescendants().OfType<Image>().First(i => i.Name == "PreviewImage");

            _Click(window, "ActualSizeButton");
            window.UpdateLayout();
            var actualSize = image.Bounds.Size;

            _Click(window, "FitButton");
            window.UpdateLayout();
            var fitted = image.Bounds.Size;

            Assert.Equal(new Size(1200, 900), actualSize);
            Assert.True(fitted.Width < window.Width, $"fit left the image at {fitted} in a {window.Width}px window");
            Assert.True(fitted.Height < actualSize.Height);
        });
    }

    // Both buttons also drop whatever Ctrl+scroll left standing, back to their own 1x baseline.
    [Fact]
    public async Task FitAndActualSize_ResetTheCtrlScrollZoom()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var window = ImagePreviewWindow.Build([Image], 0);
            await Task.Delay(200);

            var zoomTransform = _ZoomTransform(window);

            foreach (var button in new[] { "FitButton", "ActualSizeButton" })
            {
                _Zoom(window, 3.0);
                _Click(window, button);
                Assert.Equal(1.0, zoomTransform.ScaleX);
            }
        });
    }

    // AC-804: Fit at zoom 1 is the AC-778 baseline this ticket must not regress — no scrollbars, and a drag
    // across the image leaves the ScrollViewer's offset untouched because there is nothing to pan to.
    [Fact]
    public async Task FitMode_AtZoomOne_HasNoScrollbarsAndCannotBePanned()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var window = ImagePreviewWindow.Build([_Png(1200, 900)], 0);
            window.Show();
            await Task.Delay(200);
            window.UpdateLayout();

            var scroller = _Scroller(window);
            Assert.Equal(ScrollBarVisibility.Disabled, scroller.HorizontalScrollBarVisibility);
            Assert.Equal(ScrollBarVisibility.Disabled, scroller.VerticalScrollBarVisibility);

            var start = _Centre(window, scroller);
            _Drag(window, start, start + new Vector(-80, -60));
            window.UpdateLayout();

            Assert.Equal(default, scroller.Offset);
        });
    }

    // AC-804: growth shows on the transform box, not the Image (LayoutTransformControl keeps its child at the
    // pre-transform size) — and by exactly the zoom factor, since a looser bound also passes a missing freeze.
    [Fact]
    public async Task FitMode_ZoomedPastOne_GrowsTheImageBoxByExactlyTheZoomFactor()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var window = ImagePreviewWindow.Build([_Png(1200, 900)], 0);
            window.Show();
            await Task.Delay(200);
            window.UpdateLayout();

            var image = _PreviewImage(window);
            var zoomBox = _ZoomBox(window);
            var scroller = _Scroller(window);
            var fitted = zoomBox.Bounds.Size;

            _Zoom(window, 3.0);
            window.UpdateLayout();

            Assert.Equal(fitted.Width, image.Bounds.Width, 1);
            Assert.Equal(fitted.Width * 3.0, zoomBox.Bounds.Width, 1);
            Assert.Equal(ScrollBarVisibility.Auto, scroller.HorizontalScrollBarVisibility);

            var start = _Centre(window, scroller);
            _Drag(window, start, start + new Vector(-80, -60));
            window.UpdateLayout();

            Assert.True(scroller.Offset.X > 0);
            Assert.True(scroller.Offset.Y > 0);
        });
    }

    // AC-804: the same Fit-box freeze fixes zooming out too — without it, LayoutTransformControl hands
    // Stretch.Uniform a proportionally larger constraint below zoom 1, which re-fits to exactly cancel the
    // scale-down, so Ctrl+scroll-down in Fit did nothing at all.
    [Fact]
    public async Task FitMode_ZoomedBelowOne_ShrinksTheImageBox()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var window = ImagePreviewWindow.Build([_Png(1200, 900)], 0);
            window.Show();
            await Task.Delay(200);
            window.UpdateLayout();

            var zoomBox = _ZoomBox(window);
            var fitted = zoomBox.Bounds.Size;

            _Zoom(window, 0.5);
            window.UpdateLayout();

            Assert.Equal(fitted.Width * 0.5, zoomBox.Bounds.Width, 1);
        });
    }

    [Fact]
    public async Task ActualSize_BiggerThanWindow_HasScrollbars()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var window = ImagePreviewWindow.Build([_Png(1200, 900)], 0);
            window.Show();
            await Task.Delay(200);

            _Click(window, "ActualSizeButton");
            window.UpdateLayout();

            var scroller = _Scroller(window);
            Assert.Equal(ScrollBarVisibility.Auto, scroller.HorizontalScrollBarVisibility);
            Assert.Equal(ScrollBarVisibility.Auto, scroller.VerticalScrollBarVisibility);
            Assert.True(scroller.Extent.Width > scroller.Viewport.Width);
        });
    }

    [Fact]
    public async Task Panning_MovesTheOffsetByExactlyTheDragDistance()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var window = ImagePreviewWindow.Build([_Png(1200, 900)], 0);
            window.Show();
            await Task.Delay(200);
            _Click(window, "ActualSizeButton");
            window.UpdateLayout();

            var scroller = _Scroller(window);
            var start = _Centre(window, scroller);

            _Drag(window, start, start + new Vector(-50, -30));
            window.UpdateLayout();

            Assert.Equal(50, scroller.Offset.X, 1);
            Assert.Equal(30, scroller.Offset.Y, 1);
        });
    }

    [Fact]
    public async Task Panning_StopsAtTheImageEdges()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var window = ImagePreviewWindow.Build([_Png(1200, 900)], 0);
            window.Show();
            await Task.Delay(200);
            _Click(window, "ActualSizeButton");
            window.UpdateLayout();

            var scroller = _Scroller(window);
            var start = _Centre(window, scroller);
            var max = scroller.Extent - scroller.Viewport;

            _Drag(window, start, start + new Vector(-5000, -5000));
            window.UpdateLayout();

            Assert.Equal(max.Width, scroller.Offset.X, 1);
            Assert.Equal(max.Height, scroller.Offset.Y, 1);
        });
    }

    // `_ShowImage` already reset the Ctrl+scroll zoom before this ticket; panning gets the same reset so the
    // next image doesn't open scrolled to wherever the previous one was left.
    [Fact]
    public async Task SwitchingImage_ResetsZoomAndPanPosition()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var window = ImagePreviewWindow.Build([_Png(1200, 900), _Png(1200, 900)], 0);
            window.Show();
            await Task.Delay(200);
            _Click(window, "ActualSizeButton");
            window.UpdateLayout();

            var scroller = _Scroller(window);
            var zoomTransform = _ZoomTransform(window);
            _Zoom(window, 2.0);
            var start = _Centre(window, scroller);
            _Drag(window, start, start + new Vector(-60, -40));
            window.UpdateLayout();
            Assert.NotEqual(default, scroller.Offset);

            _Click(window, "NextButton");
            window.UpdateLayout();

            Assert.Equal(1.0, zoomTransform.ScaleX);
            Assert.Equal(default, scroller.Offset);
        });
    }

    [Fact]
    public async Task Panning_ShowsAGrabCursorWhilePannableAndAGrabbingCursorWhileDragging()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var window = ImagePreviewWindow.Build([_Png(1200, 900)], 0);
            window.Show();
            await Task.Delay(200);

            var scroller = _Scroller(window);
            Assert.Equal("Arrow", scroller.Cursor?.ToString());

            _Click(window, "ActualSizeButton");
            window.UpdateLayout();
            Assert.Equal("Hand", scroller.Cursor?.ToString());

            var start = _Centre(window, scroller);
            window.MouseDown(start, MouseButton.Left);
            Assert.Equal("SizeAll", scroller.Cursor?.ToString());

            window.MouseUp(start, MouseButton.Left);
            Assert.Equal("Hand", scroller.Cursor?.ToString());
        });
    }

    private static void _Zoom(Window window, double zoom) =>
        typeof(ImagePreviewWindow).GetMethod("_ApplyZoom", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(window, [zoom]);

    private static void _Click(Window window, string name) =>
        window.GetLogicalDescendants().OfType<Button>().First(b => b.Name == name)
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

    private static ImageAttachment _Png(int width, int height)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height));
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return new ImageAttachment("image/png", Convert.ToBase64String(stream.ToArray()));
    }

    // 1x1 transparent PNG — just enough for `Bitmap` to decode without throwing.
    private const string TinyPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
}
