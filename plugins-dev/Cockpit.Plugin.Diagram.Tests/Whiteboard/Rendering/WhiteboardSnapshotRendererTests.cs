using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Cockpit.Plugin.Diagram.Whiteboard.Model;
using Cockpit.Plugin.Diagram.Whiteboard.Rendering;

namespace Cockpit.Plugin.Diagram.Tests.Whiteboard.Rendering;

// The one guarantee AC-822 and AC-823 both build on: a document renders to a raster image with freehand yellow
// and placed objects blue-strict, at any moment, with no window required.

// AC-913: the renderer fits the document's content bounding box into whatever pixel size is asked for — the
// whole board, never a crop. Most tests below request that bounding box's own size, which turns the fit into a
// plain shift rather than a rescale, so pixel assertions stay simple while still exercising the real fit path.
[Collection("avalonia")]
public class WhiteboardSnapshotRendererTests
{
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

        var size = new PixelSize(180, 140);
        var badgePixel = _PixelAt(_Render(pastedDocument, size), 50, 95);
        var plainPixel = _PixelAt(_Render(insertedDocument, size), 50, 95);

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

        var size = new PixelSize(180, 140);
        var badgePixel = _PixelAt(_Render(agentDocument, size), 50, 95);
        var plainPixel = _PixelAt(_Render(operatorDocument, size), 50, 95);

        Assert.Equal(plainPixel, badgePixel);
        Assert.Equal("Placed by agent", WhiteboardObjectPainter.BadgeFor(agentDocument.Objects.OfType<PlacedObject>().Single())?.Tooltip);
        Assert.Null(WhiteboardObjectPainter.BadgeFor(operatorDocument.Objects.OfType<PlacedObject>().Single()));
    }

    // AC-916 AC3: WhiteboardObject.Color has exactly one path to the pixels — this painter — so a coloured stroke
    // shows up in the raster snapshot the agent reads, the same way it shows up live.

    // AC-916 AC1: a null Color is the fixed default — nothing changes for an object saved before this shipped.

    // AC-916 AC6: a sticky note's colour is a fill, not a stroke — WhiteboardObject.Color never reaches it.

    // AC-913: a document bigger than the requested pixel size is scaled down to fit, not cropped — a shape placed
    // well outside a small target still shows up in the render, just smaller.

    // AC-1007 AC2: uses the real fit math (WhiteboardGeometry) rather than a raster pixel-sampling proxy — a
    // pixel-sampling attempt aliased instead of blurring under this renderer's minification, so it couldn't stand
    // in for "still legible". See the AC-1007 PR/ticket for the sizing rationale behind the constants below.

    private static byte[] _TinyPngBytes()
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(4, 4));
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    private static WriteableBitmap _Render(WhiteboardDocument document, PixelSize size)
    {
        var renderer = new WhiteboardSnapshotRenderer();
        using var target = renderer.Render(document, size);

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
