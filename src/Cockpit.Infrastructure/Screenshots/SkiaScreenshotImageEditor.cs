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
    /// <summary>
    /// How coarse a redaction block is, in the image's pixels. Big enough that a line of text inside one is a
    /// single flat square rather than a smear that still has letter shapes in it — the failure mode of every
    /// redaction that gets reversed.
    /// </summary>
    private const int BlockSize = 16;

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

    public byte[] Redact(byte[] png, IReadOnlyList<CaptureRect> regions)
    {
        if (regions.Count == 0)
        {
            return png;
        }

        using var image = CaptureBitmap.Decode(png, "The capture");
        foreach (var region in regions)
        {
            _Pixelate(image, region);
        }

        using var encoded = SKImage.FromBitmap(image).Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("The redacted capture could not be encoded as a PNG.");

        return encoded.ToArray();
    }

    /// <summary>
    /// Replaces each block of the region with its own average colour. Averaging rather than sampling one pixel
    /// of the block: a block that took its colour from a corner keeps whatever happened to be there, which for a
    /// character's stroke is the character.
    /// </summary>
    private static void _Pixelate(SKBitmap image, CaptureRect region)
    {
        var left = Math.Clamp(region.X, 0, image.Width);
        var top = Math.Clamp(region.Y, 0, image.Height);
        var right = Math.Clamp(region.Right, 0, image.Width);
        var bottom = Math.Clamp(region.Bottom, 0, image.Height);

        for (var blockTop = top; blockTop < bottom; blockTop += BlockSize)
        {
            for (var blockLeft = left; blockLeft < right; blockLeft += BlockSize)
            {
                var blockRight = Math.Min(blockLeft + BlockSize, right);
                var blockBottom = Math.Min(blockTop + BlockSize, bottom);
                var colour = _AverageOf(image, blockLeft, blockTop, blockRight, blockBottom);

                for (var y = blockTop; y < blockBottom; y++)
                {
                    for (var x = blockLeft; x < blockRight; x++)
                    {
                        image.SetPixel(x, y, colour);
                    }
                }
            }
        }
    }

    private static SKColor _AverageOf(SKBitmap image, int left, int top, int right, int bottom)
    {
        long red = 0, green = 0, blue = 0;
        var count = 0;

        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                var pixel = image.GetPixel(x, y);
                red += pixel.Red;
                green += pixel.Green;
                blue += pixel.Blue;
                count++;
            }
        }

        return count == 0
            ? SKColors.Black
            : new SKColor((byte)(red / count), (byte)(green / count), (byte)(blue / count));
    }
}
