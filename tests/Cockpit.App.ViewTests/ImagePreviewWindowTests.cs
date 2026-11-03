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

    // `Build` (and the button click handlers below) call straight into `_ShowImage`, which sets every property
    // these tests read — CountText, NavigationRow — synchronously and with no dispatcher hop, so there is
    // nothing here to await: the state is already final the instant the call returns.
    [Fact]
    public void MultipleImages_ShowsPositionAndNavigatesBothWays() => HeadlessAvalonia.Run(() =>
    {
        var window = ImagePreviewWindow.Build([Image, Image, Image], 0);

        Assert.Equal("Image 1 of 3", _CountText(window).Text);
        Assert.True(_NavigationRow(window).IsVisible);

        var next = window.GetLogicalDescendants().OfType<Button>().First(b => b.Name == "NextButton");
        next.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.Equal("Image 2 of 3", _CountText(window).Text);

        var previous = window.GetLogicalDescendants().OfType<Button>().First(b => b.Name == "PreviousButton");
        previous.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.Equal("Image 1 of 3", _CountText(window).Text);
    });

    [Fact]
    public void StartIndex_OpensOnThatImage() => HeadlessAvalonia.Run(() =>
    {
        var window = ImagePreviewWindow.Build([Image, Image], 1);

        Assert.Equal("Image 2 of 2", _CountText(window).Text);
    });

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
    // The explicit `UpdateLayout()` below already forces the real layout pass synchronously (the same pattern
    // `RenderedScene.Show` uses throughout the suite) — a delay before it was waiting for nothing.
    [Fact]
    public void FitMode_AtZoomOne_HasNoScrollbarsAndCannotBePanned() => HeadlessAvalonia.Run(() =>
    {
        var window = ImagePreviewWindow.Build([_Png(1200, 900)], 0);
        window.Show();
        window.UpdateLayout();

        var scroller = _Scroller(window);
        Assert.Equal(ScrollBarVisibility.Disabled, scroller.HorizontalScrollBarVisibility);
        Assert.Equal(ScrollBarVisibility.Disabled, scroller.VerticalScrollBarVisibility);

        var start = _Centre(window, scroller);
        _Drag(window, start, start + new Vector(-80, -60));
        window.UpdateLayout();

        Assert.Equal(default, scroller.Offset);
    });

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
