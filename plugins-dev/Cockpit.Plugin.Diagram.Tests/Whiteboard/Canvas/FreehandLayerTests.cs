using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Cockpit.Plugin.Diagram.Whiteboard.Canvas;
using Cockpit.Plugin.Diagram.Whiteboard.Model;

namespace Cockpit.Plugin.Diagram.Tests.Whiteboard.Canvas;

// AC-898's one runnable check: the draft layer paints the in-progress stroke on top of an otherwise empty
// document, and paints nothing once ActiveStroke is cleared — the logic behind the live-drawing fix that
// doesn't depend on an actual pointer gesture running on a UI thread.
[Collection("avalonia")]
public class FreehandLayerTests
{
    [Fact]
    public void EmptyDocument_WithActiveStrokeSet_PaintsTheDraftStroke()
    {
        var layer = new FreehandLayer(new WhiteboardDocument())
        {
            ActiveStroke = new FreehandLayer.DraftStroke(
                [new WhiteboardPoint(10, 50), new WhiteboardPoint(90, 50)], Thickness: 2.5, IsMarker: false),
        };

        var pixel = _PixelAt(_Render(layer), 50, 50);
        Assert.True(_CloseTo(pixel, Color.Parse("#F2C230")), $"expected the draft stroke's yellow, got {pixel}");
    }

    [Fact]
    public void EmptyDocument_WithNoActiveStroke_PaintsNothing()
    {
        var layer = new FreehandLayer(new WhiteboardDocument());

        var pixel = _PixelAt(_Render(layer), 50, 50);
        Assert.Equal(default, pixel);
    }

    private static bool _CloseTo(Color actual, Color expected, byte tolerance = 20) =>
        Math.Abs(actual.R - expected.R) <= tolerance
        && Math.Abs(actual.G - expected.G) <= tolerance
        && Math.Abs(actual.B - expected.B) <= tolerance;

    private static WriteableBitmap _Render(FreehandLayer layer)
    {
        layer.Measure(new Size(100, 100));
        layer.Arrange(new Rect(0, 0, 100, 100));

        using var target = new RenderTargetBitmap(new PixelSize(100, 100));
        target.Render(layer);

        using var stream = new MemoryStream();
        target.Save(stream, PngBitmapEncoderOptions.Default);
        stream.Position = 0;
        return WriteableBitmap.Decode(stream);
    }

    private static Color _PixelAt(WriteableBitmap image, int x, int y)
    {
        using var buffer = image.Lock();
        var stride = buffer.RowBytes;
        var pixels = new byte[stride];
        Marshal.Copy(buffer.Address + (y * stride), pixels, 0, stride);

        var offset = x * 4;
        return Color.FromArgb(pixels[offset + 3], pixels[offset + 2], pixels[offset + 1], pixels[offset]);
    }
}
