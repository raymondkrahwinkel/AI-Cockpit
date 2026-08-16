using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Cockpit.Plugin.Diagram.Whiteboard.Model;
using Cockpit.Plugin.Diagram.Whiteboard.Rendering;

namespace Cockpit.Plugin.Diagram.Tests.Whiteboard.Rendering;

// The one guarantee AC-822 and AC-823 both build on: a document renders to a raster image with freehand yellow
// and placed objects blue-strict, at any moment, with no window required.
[Collection("avalonia")]
public class WhiteboardSnapshotRendererTests
{
    [Fact]
    public void FreehandStroke_RendersInYellow()
    {
        var document = new WhiteboardDocument();
        document.Add(new FreehandStroke { Points = [new WhiteboardPoint(10, 50), new WhiteboardPoint(90, 50)] });

        var image = _Render(document);

        var pixel = _PixelAt(image, 50, 50);
        Assert.True(_CloseTo(pixel, Color.Parse("#F2C230")), $"expected yellow at the stroke, got {pixel}");
    }

    [Fact]
    public void PlacedRectangle_RendersInPlacedBlue()
    {
        var document = new WhiteboardDocument();
        document.Add(new PlacedObject { ShapeKind = PlacedShapeKind.Rectangle, X = 20, Y = 20, Width = 60, Height = 40 });

        var image = _Render(document);

        var pixel = _PixelAt(image, 20, 40);
        Assert.True(_CloseTo(pixel, Color.Parse("#2563EB")), $"expected placed blue on the rectangle's edge, got {pixel}");
    }

    [Fact]
    public void EmptyDocument_RendersAsPlainWhite()
    {
        var image = _Render(new WhiteboardDocument());

        Assert.Equal(Colors.White, _PixelAt(image, 5, 5));
    }

    private static bool _CloseTo(Color actual, Color expected, byte tolerance = 20) =>
        Math.Abs(actual.R - expected.R) <= tolerance
        && Math.Abs(actual.G - expected.G) <= tolerance
        && Math.Abs(actual.B - expected.B) <= tolerance;

    private static WriteableBitmap _Render(WhiteboardDocument document)
    {
        var renderer = new WhiteboardSnapshotRenderer();
        using var target = renderer.Render(document, new PixelSize(100, 100));

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
