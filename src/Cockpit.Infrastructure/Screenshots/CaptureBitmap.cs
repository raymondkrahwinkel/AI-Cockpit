using SkiaSharp;

namespace Cockpit.Infrastructure.Screenshots;

// Decodes bytes that are supposed to be a capture, and says so plainly when they are not.
// Its own thing because `SKBitmap.Decode(byte[])` does not answer null for something it cannot read — it
// throws `ArgumentNullException` from inside, naming a parameter the caller never passed. A
// `?? throw` after it is dead code that reads as a guard, which is worse than no guard: the operator gets
// "Value cannot be null (Parameter 'codec')" where they should be told the capture did not arrive.
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
