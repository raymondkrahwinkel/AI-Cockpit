using System.Buffers.Binary;
using Cockpit.Core.Screenshots;

namespace Cockpit.Core.Tests.Screenshots;

/// <summary>
/// Reading a PNG's dimensions out of its header (AC-326). A capture that gets its layout from the desktop rather
/// than from its own blit has nothing else to check the two against, so bytes that are not the image that was
/// asked for have to read as exactly that rather than as a plausible size.
/// </summary>
public class PngImageTests
{
    [Fact]
    public void TheHeadersDimensions_AreRead()
    {
        Assert.True(PngImage.TryReadSize(Png(2880, 1620), out var width, out var height));

        Assert.Equal(2880, width);
        Assert.Equal(1620, height);
    }

    /// <summary>The image is a desktop's worth of pixels; the answer is twenty-four bytes in and the rest is never touched.</summary>
    [Fact]
    public void TrailingPixelData_IsIgnored()
    {
        var png = Png(1920, 1080).Concat(Enumerable.Repeat((byte)0x42, 4096)).ToArray();

        Assert.True(PngImage.TryReadSize(png, out var width, out _));
        Assert.Equal(1920, width);
    }

    /// <summary>A JPEG, an error page, a truncated write — none of them are the capture, and reporting a size for one would put a nonsense rectangle into the layout.</summary>
    [Theory]
    [InlineData("not a png at all")]
    [InlineData("\x89PNG\r\n\x1a\n")]
    public void BytesThatAreNotAPng_HaveNoSize(string content)
    {
        Assert.False(PngImage.TryReadSize(System.Text.Encoding.Latin1.GetBytes(content), out _, out _));
    }

    /// <summary>
    /// Everything about it says PNG except the eight bytes that decide — a header block copied into a file that
    /// is not one, or a transfer that mangled the first line. The dimensions would read perfectly well; they
    /// just would not be the capture's.
    /// </summary>
    [Fact]
    public void BytesCarryingAValidHeaderBehindAWrongSignature_AreRefused()
    {
        var png = Png(1920, 1080);
        png[0] = 0xFF;

        Assert.False(PngImage.TryReadSize(png, out _, out _));
    }

    /// <summary>
    /// The signature is right and the first chunk is not IHDR. The spec requires IHDR first, so this is a file
    /// whose dimensions are somewhere other than where they would be read from — the worst case, because the
    /// bytes at that offset would parse as a number.
    /// </summary>
    [Fact]
    public void APngWhoseFirstChunkIsNotTheHeader_IsRefused()
    {
        var png = Png(1920, 1080);
        "sRGB"u8.CopyTo(png.AsSpan(12));

        Assert.False(PngImage.TryReadSize(png, out _, out _));
    }

    /// <summary>The spec forbids a zero dimension, so a zero is a big-endian read of something that was never a size.</summary>
    [Fact]
    public void APngClaimingNoWidth_IsRefused()
    {
        Assert.False(PngImage.TryReadSize(Png(0, 1080), out _, out _));
    }

    /// <summary>
    /// A PNG header: the signature, then IHDR's length and type, then width and height big-endian. Enough for a
    /// reader that never goes past byte 24, and deliberately not a whole valid image — nothing here decodes one.
    /// </summary>
    internal static byte[] Png(int width, int height)
    {
        var png = new byte[24];
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(png);
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(8), 13);
        "IHDR"u8.CopyTo(png.AsSpan(12));
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20), height);

        return png;
    }
}
