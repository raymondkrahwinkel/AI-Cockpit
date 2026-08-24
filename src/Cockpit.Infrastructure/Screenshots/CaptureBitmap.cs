using SkiaSharp;

namespace Cockpit.Infrastructure.Screenshots;

// AC-1013: Decodes capture bytes and reports failure plainly, because `SKBitmap.Decode` throws an internal
// `ArgumentNullException` (naming a parameter the caller never passed) rather than returning null.
// Trimmed: the "codec" parameter name confusion example and why a `?? throw` guard would be dead code.
internal static class CaptureBitmap
{
    // The image these bytes hold, or an `InvalidOperationException` naming what they were meant to be.
    public static SKBitmap Decode(byte[] bytes, string what)
    {
        using var stream = new SKMemoryStream(bytes);
        using var codec = SKCodec.Create(stream)
            ?? throw new InvalidOperationException($"{what} could not be decoded as an image.");

        return SKBitmap.Decode(codec)
            ?? throw new InvalidOperationException($"{what} decodes to nothing.");
    }
}
