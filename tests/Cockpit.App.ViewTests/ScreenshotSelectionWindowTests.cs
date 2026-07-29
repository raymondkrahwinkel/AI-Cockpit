using Avalonia.Media.Imaging;
using SkiaSharp;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The selection window itself, built but not shown (AC-325). Everything else about the surface is arithmetic in
/// the view model; this is the one test that constructs the window at all — and until it existed, nothing did.
/// </summary>
/// <remarks>
/// Written after the surface shipped broken on every platform. The window declared its own
/// <c>InitializeComponent</c>, which hid the one Avalonia generates — and the generated one is what assigns the
/// fields for the controls the XAML names. They stayed null, so the first line to touch one threw, and the
/// operator got "Object reference not set to an instance of an object" instead of a screenshot. 152 view tests
/// passed throughout, because every one of them stopped at the view model.
/// </remarks>
[Collection("avalonia")]
public class ScreenshotSelectionWindowTests
{
    [Fact]
    public void TheWindowIsBuilt_WithItsControlsWiredToTheCapture() => HeadlessAvalonia.Run(() =>
    {
        using var bitmap = _Bitmap(320, 200);

        var window = ScreenshotSelectionWindow.Build(_Capture(), bitmap, lastRegion: null, windows: StubWindows.None);

        Assert.Same(bitmap, window.Capture.Source);
        Assert.NotNull(window.Surface);
        Assert.NotNull(window.Marquee);
        Assert.NotNull(window.Readout);
        Assert.NotNull(window.Shade);
        Assert.NotNull(window.DataContext);
    });

    /// <summary>The image's own size comes from the bitmap, not from the window — a surface that took the window's would crop by the wrong numbers on any scaled display.</summary>
    [Fact]
    public void TheSurfaceMeasuresInTheCapturesOwnPixels() => HeadlessAvalonia.Run(() =>
    {
        using var bitmap = _Bitmap(2880, 1620);

        var window = ScreenshotSelectionWindow.Build(_Capture(), bitmap, lastRegion: null, windows: StubWindows.None);

        Assert.Equal(2880, Assert.IsType<Cockpit.App.ViewModels.ScreenshotSelectionViewModel>(window.DataContext).ImageWidth);
    });

    private static Bitmap _Bitmap(int width, int height)
    {
        using var surface = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using var image = SKImage.FromBitmap(surface);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new MemoryStream(encoded.ToArray());

        return new Bitmap(stream);
    }

    private static ScreenCapture _Capture() => new()
    {
        Image = [0x89, 0x50, 0x4E, 0x47],
        Displays =
        [
            new CapturedDisplay
            {
                DesktopBounds = new CaptureRect(0, 0, 1920, 1080),
                Scale = 1.5,
                ImageBounds = new CaptureRect(0, 0, 2880, 1620),
            },
        ],
    };

    private sealed class StubWindows : IDesktopWindows
    {
        public static StubWindows None => new();

        public bool IsSupported => false;

        public IReadOnlyList<DesktopWindow> Enumerate() => [];
    }
}
