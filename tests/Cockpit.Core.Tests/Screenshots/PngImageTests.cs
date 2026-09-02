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

    /// <summary>
    /// Everything that is not the capture must read as exactly that rather than as a plausible size. A JPEG or a
    /// truncated write is obvious; the interesting rows look like a PNG. A wrong first byte is a header copied
    /// into a file that is not one — the dimensions read perfectly well and are not the capture's. A first chunk
    /// that is not IHDR is worse, because the bytes at that offset parse as a number anyway. And the spec forbids
    /// a zero dimension, so a zero is a big-endian read of something that was never a size.
    /// </summary>
    [Theory]
    [MemberData(nameof(BytesThatAreNotTheCapture))]
    public void BytesThatAreNotAPng_HaveNoSize(byte[] content)
    {
        Assert.False(PngImage.TryReadSize(content, out _, out _));
    }

    public static IEnumerable<object[]> BytesThatAreNotTheCapture()
    {
        yield return [System.Text.Encoding.Latin1.GetBytes("not a png at all")];
        yield return [System.Text.Encoding.Latin1.GetBytes("\x89PNG\r\n\x1a\n")];

        var wrongSignature = Png(1920, 1080);
        wrongSignature[0] = 0xFF;
        yield return [wrongSignature];

        var notIhdrFirst = Png(1920, 1080);
        "sRGB"u8.CopyTo(notIhdrFirst.AsSpan(12));
        yield return [notIhdrFirst];

        yield return [Png(0, 1080)];
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
