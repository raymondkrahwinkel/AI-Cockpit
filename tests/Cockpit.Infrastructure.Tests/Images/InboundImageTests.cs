using Cockpit.Infrastructure.Images;
using Cockpit.Plugins.Abstractions.Channels;
using SkiaSharp;

namespace Cockpit.Infrastructure.Tests.Images;

// AC-1049: the host's half of the trust boundary. An attachment arrives from a chat platform, so what these
// prove is mostly what is refused — the happy path is one case out of eight.
public class InboundImageTests
{
    private static byte[] _Encoded(int width, int height, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.CornflowerBlue);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    private static bool _IsPng(byte[] bytes) =>
        bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;

    [Fact]
    public void AcceptsAPng()
    {
        Assert.True(InboundImage.TryNormalizeToPng(_Encoded(64, 48, SKEncodedImageFormat.Png), out var png, out var refusal));
        Assert.Null(refusal);
        Assert.True(_IsPng(png));
    }

    // The attachment path hardcodes `media_type: image/png`, so anything else has to come out the other side as
    // a PNG or it would go on the wire mislabelled.
    [Fact]
    public void ConvertsAJpegToPng()
    {
        Assert.True(InboundImage.TryNormalizeToPng(_Encoded(64, 48, SKEncodedImageFormat.Jpeg), out var png, out _));
        Assert.True(_IsPng(png));
    }

    [Fact]
    public void RefusesEmptyBytes()
    {
        Assert.False(InboundImage.TryNormalizeToPng([], out _, out var refusal));
        Assert.Contains("empty", refusal);
    }

    // What a .png that is really a text file looks like — and what Slack answers with when the app is missing
    // the files:read scope: its sign-in page, with a 200 on it.
    [Fact]
    public void RefusesSomethingThatIsNotAnImage()
    {
        var html = "<!doctype html><html><body>Sign in to Slack</body></html>"u8.ToArray();

        Assert.False(InboundImage.TryNormalizeToPng(html, out _, out var refusal));
        Assert.Contains("not an image", refusal);
    }

    [Fact]
    public void RefusesAFileOverTheByteCap()
    {
        var tooBig = new byte[AssistantChannelImageLimits.MaxBytes + 1];

        Assert.False(InboundImage.TryNormalizeToPng(tooBig, out _, out var refusal));
        Assert.Contains("MB", refusal);
    }

    // Refused on the codec's header, before the pixels are decoded — this one would allocate far more decoded
    // than it takes to encode, which is the whole trick of a decompression bomb.
    [Fact]
    public void RefusesAnImagePastThePixelCap()
    {
        var wide = _Encoded(AssistantChannelImageLimits.MaxPixelsPerSide + 1, 8, SKEncodedImageFormat.Png);

        Assert.False(InboundImage.TryNormalizeToPng(wide, out _, out var refusal));
        Assert.Contains("pixels", refusal);
    }

    [Fact]
    public void ScalesALargeImageDownToTheLongEdge()
    {
        var large = _Encoded(4000, 2000, SKEncodedImageFormat.Jpeg);

        Assert.True(InboundImage.TryNormalizeToPng(large, out var png, out _));

        using var decoded = SKBitmap.Decode(png);
        Assert.Equal(AssistantChannelImageLimits.MaxLongEdge, decoded.Width);
        Assert.Equal(AssistantChannelImageLimits.MaxLongEdge / 2, decoded.Height);
    }

    [Fact]
    public void LeavesASmallImageAtItsOwnSize()
    {
        Assert.True(InboundImage.TryNormalizeToPng(_Encoded(320, 200, SKEncodedImageFormat.Png), out var png, out _));

        using var decoded = SKBitmap.Decode(png);
        Assert.Equal(320, decoded.Width);
        Assert.Equal(200, decoded.Height);
    }
}
