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

    [Fact]
    public void MarkerStroke_RendersTranslucent_AndDistinctFromPencil()
    {
        var pencilDocument = new WhiteboardDocument();
        pencilDocument.Add(new FreehandStroke { Points = [new WhiteboardPoint(10, 50), new WhiteboardPoint(90, 50)] });

        var markerDocument = new WhiteboardDocument();
        markerDocument.Add(new FreehandStroke
        {
            Points = [new WhiteboardPoint(10, 50), new WhiteboardPoint(90, 50)],
            Thickness = 14,
            IsMarker = true,
        });

        var pencilPixel = _PixelAt(_Render(pencilDocument), 50, 50);
        var markerPixel = _PixelAt(_Render(markerDocument), 50, 50);

        Assert.NotEqual(pencilPixel, markerPixel);
        Assert.False(_CloseTo(markerPixel, WhiteboardObjectPainter.MarkerColor, tolerance: 10), $"expected a translucent blend, got opaque marker colour {markerPixel}");
    }

    [Fact]
    public void StickyNote_RendersInStickyYellow()
    {
        var document = new WhiteboardDocument();
        document.Add(new PlacedObject { ShapeKind = PlacedShapeKind.StickyNote, X = 10, Y = 10, Width = 60, Height = 60, Text = "Idee" });

        var pixel = _PixelAt(_Render(document), 40, 40);

        Assert.True(_CloseTo(pixel, WhiteboardObjectPainter.StickyNoteColor), $"expected sticky-note yellow, got {pixel}");
    }

    [Fact]
    public void PastedScreenshot_IsBadged_ButTheSnapshotNeverShowsIt()
    {
        // AC-918: BadgeFor still tells a pasted screenshot apart, but the PNG snapshot (no hover/selection in a
        // static image) renders it identically to a plain inserted image.
        var pastedDocument = new WhiteboardDocument();
        pastedDocument.Add(new PlacedObject
        {
            ShapeKind = PlacedShapeKind.Image,
            X = 0,
            Y = 0,
            Width = 100,
            Height = 60,
            ImageData = _TinyPngBytes(),
            IsPastedScreenshot = true,
        });

        var insertedDocument = new WhiteboardDocument();
        insertedDocument.Add(new PlacedObject
        {
            ShapeKind = PlacedShapeKind.Image,
            X = 0,
            Y = 0,
            Width = 100,
            Height = 60,
            ImageData = _TinyPngBytes(),
            IsPastedScreenshot = false,
        });

        var badgePixel = _PixelAt(_Render(pastedDocument), 10, 55);
        var plainPixel = _PixelAt(_Render(insertedDocument), 10, 55);

        Assert.Equal(plainPixel, badgePixel);
    }

    [Fact]
    public void AnAgentPlacedObject_IsBadged_ButTheSnapshotNeverShowsIt()
    {
        // AC-854 tells the agent's work apart via BadgeFor; AC-918 keeps that out of the PNG snapshot — the
        // agent already knows what it placed, and hover/selection don't exist in a static image.
        var agentDocument = new WhiteboardDocument();
        agentDocument.Add(new PlacedObject { ShapeKind = PlacedShapeKind.Rectangle, X = 0, Y = 0, Width = 100, Height = 60, PlacedByAgent = true });

        var operatorDocument = new WhiteboardDocument();
        operatorDocument.Add(new PlacedObject { ShapeKind = PlacedShapeKind.Rectangle, X = 0, Y = 0, Width = 100, Height = 60 });

        var badgePixel = _PixelAt(_Render(agentDocument), 10, 55);
        var plainPixel = _PixelAt(_Render(operatorDocument), 10, 55);

        Assert.Equal(plainPixel, badgePixel);
        Assert.Equal("Placed by agent", WhiteboardObjectPainter.BadgeFor(agentDocument.Objects.OfType<PlacedObject>().Single())?.Tooltip);
        Assert.Null(WhiteboardObjectPainter.BadgeFor(operatorDocument.Objects.OfType<PlacedObject>().Single()));
    }

    private static byte[] _TinyPngBytes()
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(4, 4));
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
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
