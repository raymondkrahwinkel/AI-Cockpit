using SkiaSharp;

namespace Cockpit.Infrastructure.Screenshots;

/// <summary>
/// Decodes bytes that are supposed to be a capture, and says so plainly when they are not.
/// </summary>
/// <remarks>
/// Its own thing because <c>SKBitmap.Decode(byte[])</c> does not answer null for something it cannot read — it
/// throws <c>ArgumentNullException</c> from inside, naming a parameter the caller never passed. A
/// <c>?? throw</c> after it is dead code that reads as a guard, which is worse than no guard: the operator gets
/// "Value cannot be null (Parameter 'codec')" where they should be told the capture did not arrive.
/// </remarks>
internal static class CaptureBitmap
{
    /// <summary>The image these bytes hold, or an <see cref="InvalidOperationException"/> naming what they were meant to be.</summary>
    public static SKBitmap Decode(byte[] bytes, string what)
    {
        using var stream = new SKMemoryStream(bytes);
        using var codec = SKCodec.Create(stream)
            ?? throw new InvalidOperationException($"{what} could not be decoded as an image.");

        return SKBitmap.Decode(codec)
            ?? throw new InvalidOperationException($"{what} decodes to nothing.");
    }
}
