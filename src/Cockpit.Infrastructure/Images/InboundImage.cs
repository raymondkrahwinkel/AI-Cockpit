using System.Diagnostics.CodeAnalysis;
using Cockpit.Plugins.Abstractions.Channels;
using SkiaSharp;

namespace Cockpit.Infrastructure.Images;

// The trust boundary for an image from outside the app — a Slack or Discord attachment (AC-1049). Nothing said
// about the file is believed: the codec decides whether these bytes are an image, and what comes back is always a
// PNG, because `media_type: image/png` is what the attachment path puts on the wire.
public static class InboundImage
{
    // The PNG these bytes hold, or false with a reason plain enough to show the sender.
    public static bool TryNormalizeToPng(byte[] bytes, [NotNullWhen(true)] out byte[]? png, [NotNullWhen(false)] out string? refusal)
    {
        png = null;

        if (bytes.Length == 0)
        {
            refusal = "the file was empty";
            return false;
        }

        if (bytes.Length > AssistantChannelImageLimits.MaxBytes)
        {
            refusal = $"the file is bigger than {AssistantChannelImageLimits.MaxBytes / (1024 * 1024)} MB";
            return false;
        }

        using var stream = new SKMemoryStream(bytes);

        // Null rather than an exception for something it cannot read — the same quirk CaptureBitmap documents.
        using var codec = SKCodec.Create(stream);
        if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0)
        {
            refusal = "the file is not an image";
            return false;
        }

        // The header's dimensions, before a single pixel is decoded: decoding first is how a decompression bomb
        // gets to allocate width × height × 4 bytes before anything can object to it.
        if (codec.Info.Width > AssistantChannelImageLimits.MaxPixelsPerSide
            || codec.Info.Height > AssistantChannelImageLimits.MaxPixelsPerSide)
        {
            refusal = $"the image is bigger than {AssistantChannelImageLimits.MaxPixelsPerSide}×{AssistantChannelImageLimits.MaxPixelsPerSide} pixels";
            return false;
        }

        using var decoded = SKBitmap.Decode(codec);
        if (decoded is null)
        {
            refusal = "the image could not be decoded";
            return false;
        }

        using var scaled = _ScaledDown(decoded);
        using var image = SKImage.FromBitmap(scaled ?? decoded);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        if (encoded is null)
        {
            refusal = "the image could not be re-encoded as a PNG";
            return false;
        }

        png = encoded.ToArray();
        refusal = null;
        return true;
    }

    // Null when it already fits, so the caller keeps using the decoded bitmap rather than disposing one twice.
    private static SKBitmap? _ScaledDown(SKBitmap bitmap)
    {
        var longEdge = Math.Max(bitmap.Width, bitmap.Height);
        if (longEdge <= AssistantChannelImageLimits.MaxLongEdge)
        {
            return null;
        }

        var factor = (double)AssistantChannelImageLimits.MaxLongEdge / longEdge;
        var target = new SKImageInfo(
            Math.Max(1, (int)Math.Round(bitmap.Width * factor)),
            Math.Max(1, (int)Math.Round(bitmap.Height * factor)),
            bitmap.ColorType,
            bitmap.AlphaType);

        return bitmap.Resize(target, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
    }
}
