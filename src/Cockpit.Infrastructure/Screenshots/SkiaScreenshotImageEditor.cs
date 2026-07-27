using SkiaSharp;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.Infrastructure.Screenshots;

/// <summary>
/// Crops a capture with SkiaSharp (AC-329) — the same library the Windows blit and the macOS composition already
/// encode through, so a screenshot passes through one imaging stack from the screen to the session.
/// </summary>
internal sealed class SkiaScreenshotImageEditor : IScreenshotImageEditor, ISingletonService
{
    public byte[] Crop(byte[] png, CaptureRect region)
    {
        using var image = CaptureBitmap.Decode(png, "The capture");

        // Clamped rather than trusted. The region comes from a selection surface working in this image's pixels,
        // but a display that changed between the capture and the confirm would put it past the edge — and Skia
        // answers an out-of-bounds extract with an empty bitmap that encodes to a valid, blank PNG.
        var bounds = new SKRectI(
            Math.Clamp(region.X, 0, image.Width),
            Math.Clamp(region.Y, 0, image.Height),
            Math.Clamp(region.Right, 0, image.Width),
            Math.Clamp(region.Bottom, 0, image.Height));

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidOperationException($"The region {region} lies outside the {image.Width}×{image.Height} capture.");
        }

        using var cropped = new SKBitmap(bounds.Width, bounds.Height, image.ColorType, image.AlphaType);
        if (!image.ExtractSubset(cropped, bounds))
        {
            throw new InvalidOperationException($"The region {region} could not be taken out of the capture.");
        }

        using var encoded = SKImage.FromBitmap(cropped).Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("The cropped capture could not be encoded as a PNG.");

        return encoded.ToArray();
    }
}
