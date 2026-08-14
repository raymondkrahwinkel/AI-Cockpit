using Avalonia.Controls;
using Avalonia.LogicalTree;
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

    // 1x1 transparent PNG — just enough for `Bitmap` to decode without throwing.
    private const string TinyPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
}
