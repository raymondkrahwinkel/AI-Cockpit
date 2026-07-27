using Microsoft.Extensions.Logging;
using SkiaSharp;
using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.Infrastructure.Screenshots;

/// <summary>
/// Screen capture on macOS (AC-328): every display read whole and silently, then drawn into the one image the
/// capture contract asks for. The selection is the cockpit's own (AC-329); this only supplies the pixels.
/// </summary>
/// <remarks>
/// AC-220 ran <c>screencapture -i</c>, which is the system's own crosshair — drag a region, press space for a
/// window. Dropping the <c>-i</c> is the whole change: the same binary writes the screen with no picker and no
/// interaction.
/// <para>
/// macOS does not force one scale across displays the way a Linux compositor does, so a Retina panel beside an
/// ordinary monitor captures at two different resolutions. They are composed here onto a canvas at the largest
/// of those scales, positioned by the point geometry, which is the same shape the Linux portal hands back
/// whole — so the selection surface above meets one kind of image rather than three.
/// </para>
/// <para>
/// <strong>This ships unverified.</strong> There is no Mac to run it on, and the codebase's convention for that
/// is not to pretend: what cannot be checked says so. Screen Recording permission is granted once per app, and
/// until it is, <c>screencapture</c> runs and yields nothing — which is why nothing captured is reported as
/// possibly-not-permitted rather than as a picker somebody dismissed.
/// </para>
/// </remarks>
internal sealed class MacScreenshotCapture(IMacScreenReader screens, ILogger<MacScreenshotCapture> logger)
    : IScreenshotCapture
{
    public bool IsSupported => true;

    /// <summary>Nothing to ask anyone: <c>screencapture</c> is part of macOS.</summary>
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
                // One display yielding nothing means all of them will: the permission is per application, not
                // per screen. Reported as nothing captured, which the caller passes over in silence — the same
                // as a selection nobody completed, and honest, because the two are genuinely indistinguishable
                // from here until someone looks at the privacy settings.
                logger.LogInformation(
                    "screencapture wrote nothing for display {Display}. Screen Recording is granted per application, so it may not have been allowed yet.",
                    display.Index);
                return null;
            }

            captured.Add((display, image));
        }

        // The displays were read once and then captured one process launch at a time, so the window in which
        // somebody can unplug a screen is far wider here than on Windows — where the same check guards a single
        // blit. A display list that has moved on means the pixels below belong to a desktop this layout no
        // longer describes.
        if (!screens.ReadDisplays().SequenceEqual(displays))
        {
            throw new InvalidOperationException("The displays changed while the screens were being read, so the captures and the layout describe different desktops.");
        }

        return _Compose(captured);
    }

    /// <summary>
    /// The displays drawn into one image. The canvas is the desktop's own rectangle at the largest scale any
    /// display uses, so nothing is captured at less than its native resolution — a Retina panel keeps its
    /// pixels and an ordinary monitor beside it is drawn across the same area at that scale.
    /// </summary>
    private static ScreenCapture _Compose(IReadOnlyList<(MacDisplay Display, byte[] Image)> captured)
    {
        var displays = captured
            .Select(entry => new DesktopDisplay { Bounds = entry.Display.Bounds, Scale = entry.Display.Scale })
            .ToList();

        var scale = displays.Max(display => display.Scale);
        var desktop = _BoundingBox(displays);
        var width = (int)Math.Round(desktop.Width * scale);

        // Derived from the width rather than from the scale a second time. Rounding both against the same
        // fraction lets them land on ratios that differ, and the layout below reconstructs its ratio from the
        // width alone — on a desktop taller than it is wide the two can then disagree by more than the pixel it
        // allows, and a perfectly ordinary arrangement of stacked displays is refused.
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

            // What came back has to be the display it was asked for. Nothing else here would notice otherwise:
            // the draw stretches whatever it is given into the slot, so a capture of the wrong screen — the
            // -D numbering not lining up with the enumeration, which is this file's one unverified assumption —
            // would compose into a picture that is the right size and shows the wrong desktop in the wrong place.
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
