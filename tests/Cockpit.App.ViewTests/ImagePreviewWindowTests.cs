using System.Reflection;
using Avalonia;
using Avalonia.Controls;
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
            var zoomTransform = (ScaleTransform)window.GetLogicalDescendants().OfType<Image>()
                .First(i => i.Name == "PreviewImage").RenderTransform!;

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

            var zoomTransform = (ScaleTransform)window.GetLogicalDescendants().OfType<Image>()
                .First(i => i.Name == "PreviewImage").RenderTransform!;

            foreach (var button in new[] { "FitButton", "ActualSizeButton" })
            {
                _Zoom(window, 3.0);
                _Click(window, button);
                Assert.Equal(1.0, zoomTransform.ScaleX);
            }
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
