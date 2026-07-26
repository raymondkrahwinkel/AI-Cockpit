using System.Buffers.Binary;

namespace Cockpit.Core.Screenshots;

/// <summary>
/// The one thing a capture needs to know about a PNG somebody else encoded: how big it is (AC-326). A capture
/// that gets its layout from the desktop rather than from its own blit has to check the two agree, and the
/// image's own dimensions are the only evidence there is.
/// </summary>
/// <remarks>
/// Reading the header rather than decoding: the answer is twenty-four bytes in, and decoding a whole desktop's
/// worth of pixels to learn its width would cost tens of megabytes to discard immediately. It also keeps this in
/// Core, which has no imaging library and should not grow one for a header read.
/// </remarks>
public static class PngImage
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // Signature, then the first chunk: length (4), type (4), and IHDR opens with width and height as big-endian
    // 32-bit integers. IHDR is required by the spec to be the first chunk, so its position is fixed.
    private const int TypeOffset = 12;
    private const int WidthOffset = 16;
    private const int HeaderLength = 24;

    /// <summary>
    /// The image's dimensions, or <see langword="false"/> when the bytes are not a PNG this can read — which is
    /// itself an answer: something other than the image that was asked for came back.
    /// </summary>
    public static bool TryReadSize(ReadOnlySpan<byte> png, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (png.Length < HeaderLength || !png[..Signature.Length].SequenceEqual(Signature) || !png.Slice(TypeOffset, 4).SequenceEqual("IHDR"u8))
        {
            return false;
        }

        width = BinaryPrimitives.ReadInt32BigEndian(png.Slice(WidthOffset, 4));
        height = BinaryPrimitives.ReadInt32BigEndian(png.Slice(WidthOffset + 4, 4));

        // The spec caps both at 2^31-1 and forbids zero, so a negative value is a big-endian read of something
        // that was never a dimension. Reporting it as a size would put a nonsense rectangle into the layout.
        return width > 0 && height > 0;
    }
}
