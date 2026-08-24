using Microsoft.Extensions.Logging;
using SkiaSharp;
using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.Infrastructure.Screenshots;

// AC-1013 (AC-328): Screen capture on macOS — every display read whole, composed onto one canvas at the largest
// scale in use (Retina panels aren't forced to one scale like on Linux) so the selection surface meets one image.
// Trimmed: AC-220's `-i` interactive-picker history; ships unverified (no Mac); permission-denied vs no-op ambiguity.
internal sealed class MacScreenshotCapture(IMacScreenReader screens, ILogger<MacScreenshotCapture> logger)
    : IScreenshotCapture
{
    public bool IsSupported => true;

    // Nothing to ask anyone: `screencapture` is part of macOS.
    public Task SupportSettled => Task.CompletedTask;

    public async Task<ScreenCapture?> CaptureAsync(CancellationToken cancellationToken = default)
    {
        var displays = screens.ReadDisplays();
        if (displays.Count == 0)
        {
            throw new InvalidOperationException("macOS reported no active displays, so there is nothing to capture.");
        }

        var captured = new List<(MacDisplay Display, byte[] Image)>(displays.Count);
        foreach (var display in displays)
        {
            if (await screens.CaptureDisplayAsync(display.Index, cancellationToken).ConfigureAwait(false) is not { } image)
            {
                // AC-1013: One display yielding nothing means all will (permission is per app, not per screen);
                // reported as nothing captured, indistinguishable here from a selection nobody completed.
                logger.LogInformation(
                    "screencapture wrote nothing for display {Display}. Screen Recording is granted per application, so it may not have been allowed yet.",
                    display.Index);
                return null;
            }

            captured.Add((display, image));
        }

        // AC-1013: Displays are read once, then captured one process launch at a time — a wider unplug window
        // than Windows' single blit. A moved-on display list means the pixels below no longer match this layout.
        if (!screens.ReadDisplays().SequenceEqual(displays))
        {
            throw new InvalidOperationException("The displays changed while the screens were being read, so the captures and the layout describe different desktops.");
        }

        return _Compose(captured);
    }

    // The displays drawn into one image. The canvas is the desktop's own rectangle at the largest scale any
    // display uses, so nothing is captured at less than its native resolution — a Retina panel keeps its
    // pixels and an ordinary monitor beside it is drawn across the same area at that scale.
    private static ScreenCapture _Compose(IReadOnlyList<(MacDisplay Display, byte[] Image)> captured)
    {
        var displays = captured
            .Select(entry => new DesktopDisplay { Bounds = entry.Display.Bounds, Scale = entry.Display.Scale })
            .ToList();

        var scale = displays.Max(display => display.Scale);
        var desktop = _BoundingBox(displays);
        var width = (int)Math.Round(desktop.Width * scale);

        // AC-1013: Height is derived from the width, not the scale a second time — rounding both independently
        // can make ratios disagree by more than a pixel and get a legitimate stacked-display layout refused.
        var height = (int)Math.Round(desktop.Height * (width / (double)desktop.Width));

        var layout = ComposedCaptureLayout.TryCompose(displays, width, height)
            ?? throw new InvalidOperationException(
                $"The displays macOS reports do not add up to a {width}×{height} desktop, so there is nowhere to draw them.");

        using var surface = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(surface);

        // Black behind them, not transparent: a staggered arrangement leaves area no display covers, and that
        // has to read as "nothing here" to the selection surface rather than as an image with holes.
        canvas.Clear(SKColors.Black);

        foreach (var (entry, place) in captured.Zip(layout))
        {
            using var image = CaptureBitmap.Decode(entry.Image, $"What screencapture wrote for display {entry.Display.Index}");

            // AC-1013: What came back must be the display asked for — otherwise the draw silently stretches a
            // wrong-screen capture (mismatched -D numbering) into a right-size, wrong-content image.
            if (image.Width != entry.Display.PixelWidth || image.Height != entry.Display.PixelHeight)
            {
                throw new InvalidOperationException(
                    $"The capture of display {entry.Display.Index} is {image.Width}×{image.Height} pixels, not the {entry.Display.PixelWidth}×{entry.Display.PixelHeight} that display reports.");
            }

            canvas.DrawBitmap(image, SKRect.Create(place.ImageBounds.X, place.ImageBounds.Y, place.ImageBounds.Width, place.ImageBounds.Height));
        }

        using var composed = SKImage.FromBitmap(surface);
        using var encoded = composed.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("The captured screens could not be encoded as a PNG.");

        return new ScreenCapture { Image = encoded.ToArray(), Displays = layout };
    }

    private static CaptureRect _BoundingBox(IReadOnlyList<DesktopDisplay> displays)
    {
        var left = displays.Min(display => display.Bounds.X);
        var top = displays.Min(display => display.Bounds.Y);

        return new CaptureRect(
            left,
            top,
            displays.Max(display => display.Bounds.Right) - left,
            displays.Max(display => display.Bounds.Bottom) - top);
    }
}
