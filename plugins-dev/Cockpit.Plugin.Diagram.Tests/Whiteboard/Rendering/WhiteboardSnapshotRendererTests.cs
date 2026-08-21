using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Cockpit.Plugin.Diagram.Whiteboard;
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
    public void FreehandStroke_RendersInYellow()
    {
        var document = new WhiteboardDocument();
        document.Add(new FreehandStroke { Points = [new WhiteboardPoint(10, 50), new WhiteboardPoint(90, 50)] });

        var image = _Render(document, new PixelSize(160, 80));

        var pixel = _PixelAt(image, 80, 40);
        Assert.True(_CloseTo(pixel, Color.Parse("#F2C230")), $"expected yellow at the stroke, got {pixel}");
    }

    [Fact]
    public void PlacedRectangle_RendersInPlacedBlue()
    {
        var document = new WhiteboardDocument();
        document.Add(new PlacedObject { ShapeKind = PlacedShapeKind.Rectangle, X = 20, Y = 20, Width = 60, Height = 40 });

        var image = _Render(document, new PixelSize(140, 120));

        var pixel = _PixelAt(image, 40, 60);
        Assert.True(_CloseTo(pixel, Color.Parse("#2563EB")), $"expected placed blue on the rectangle's edge, got {pixel}");
    }

    [Fact]
    public void EmptyDocument_RendersAsPlainWhite()
    {
        var image = _Render(new WhiteboardDocument(), new PixelSize(100, 100));

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

        var size = new PixelSize(160, 80);
        var pencilPixel = _PixelAt(_Render(pencilDocument, size), 80, 40);
        var markerPixel = _PixelAt(_Render(markerDocument, size), 80, 40);

        Assert.NotEqual(pencilPixel, markerPixel);
        Assert.False(_CloseTo(markerPixel, WhiteboardObjectPainter.MarkerColor, tolerance: 10), $"expected a translucent blend, got opaque marker colour {markerPixel}");
    }

    [Fact]
    public void StickyNote_RendersInStickyYellow()
    {
        var document = new WhiteboardDocument();
        document.Add(new PlacedObject { ShapeKind = PlacedShapeKind.StickyNote, X = 10, Y = 10, Width = 60, Height = 60, Text = "Idee" });

        var pixel = _PixelAt(_Render(document, new PixelSize(140, 140)), 70, 70);

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
    [Fact]
    public void ColouredFreehandStroke_RendersInThatColour_NotTheDefault()
    {
        var document = new WhiteboardDocument();
        document.Add(new FreehandStroke { Points = [new WhiteboardPoint(10, 50), new WhiteboardPoint(90, 50)], Color = "#DC2626" });

        var pixel = _PixelAt(_Render(document, new PixelSize(160, 80)), 80, 40);

        Assert.True(_CloseTo(pixel, Color.Parse("#DC2626")), $"expected the stroke's own colour, got {pixel}");
    }

    [Fact]
    public void ColouredPlacedShape_RendersInThatColour_NotPlacedBlue()
    {
        var document = new WhiteboardDocument();
        document.Add(new PlacedObject { ShapeKind = PlacedShapeKind.Rectangle, X = 20, Y = 20, Width = 60, Height = 40, Color = "#16A34A" });

        var pixel = _PixelAt(_Render(document, new PixelSize(140, 120)), 40, 60);

        Assert.True(_CloseTo(pixel, Color.Parse("#16A34A")), $"expected the shape's own colour, got {pixel}");
    }

    // AC-916 AC1: a null Color is the fixed default — nothing changes for an object saved before this shipped.
    [Fact]
    public void UncolouredPlacedShape_StillRendersInPlacedBlue()
    {
        var document = new WhiteboardDocument();
        document.Add(new PlacedObject { ShapeKind = PlacedShapeKind.Rectangle, X = 20, Y = 20, Width = 60, Height = 40 });

        var pixel = _PixelAt(_Render(document, new PixelSize(140, 120)), 40, 60);

        Assert.True(_CloseTo(pixel, Color.Parse("#2563EB")), $"expected placed blue, got {pixel}");
    }

    // AC-916 AC6: a sticky note's colour is a fill, not a stroke — WhiteboardObject.Color never reaches it.
    [Fact]
    public void ColouredStickyNote_IgnoresTheColour_StillRendersStickyYellow()
    {
        var document = new WhiteboardDocument();
        document.Add(new PlacedObject { ShapeKind = PlacedShapeKind.StickyNote, X = 10, Y = 10, Width = 60, Height = 60, Color = "#DC2626" });

        var pixel = _PixelAt(_Render(document, new PixelSize(140, 140)), 70, 70);

        Assert.True(_CloseTo(pixel, WhiteboardObjectPainter.StickyNoteColor), $"expected sticky-note yellow, got {pixel}");
    }

    // AC-913: a document bigger than the requested pixel size is scaled down to fit, not cropped — a shape placed
    // well outside a small target still shows up in the render, just smaller.
    [Fact]
    public void ContentBiggerThanTheRequestedSize_IsScaledToFit_NotCropped()
    {
        var document = new WhiteboardDocument();
        document.Add(new PlacedObject { ShapeKind = PlacedShapeKind.StickyNote, X = 2000, Y = 1500, Width = 60, Height = 60 });

        var pixel = _PixelAt(_Render(document, new PixelSize(50, 50)), 25, 25);

        Assert.True(_CloseTo(pixel, WhiteboardObjectPainter.StickyNoteColor), $"expected the sticky note scaled into view, got {pixel}");
    }

    // AC-1007 AC2: the legibility claim, tied to the real fit math (WhiteboardGeometry — the same path
    // read_whiteboard's snapshot goes through) rather than a raster pixel-sampling proxy. A pixel-sampling attempt
    // at this turned out to interact with the renderer's image scaling in non-obvious ways (aliasing rather than
    // blur, since minification without mipmapping doesn't reliably blur a fine pattern), so it couldn't stand in
    // for "still legible" without being coincidental. Cockpit's own UI text sits at 12-13px (Theme.axaml); ~7px
    // cap-height is the commonly cited floor below which anti-aliased UI text stops reading as letters. A
    // screenshot pasted near-native fills most of the board, so its effective on-canvas scale is the whole-board
    // fit factor computed here.
    [Fact]
    public void SnapshotSize_KeepsAFullBoardScreenshotsCaptionAboveTheLegibilityFloor_UnlikeTheOldSize()
    {
        const double CaptionHeight = 13; // Theme.axaml's own button/caption font size
        const double LegibilityFloor = 7; // commonly cited minimum cap-height for anti-aliased UI text to stay readable

        var board = WhiteboardGeometry.WorkspaceSize;
        var document = new WhiteboardDocument();
        document.Add(new PlacedObject { ShapeKind = PlacedShapeKind.Image, X = 0, Y = 0, Width = board.Width, Height = board.Height, ImageData = [], IsPastedScreenshot = true });
        var content = WhiteboardGeometry.ContentBounds(document);

        var oldZoom = WhiteboardGeometry.FitTransform(content, new Size(800, 600)).M11; // AC-1007: what read_whiteboard used to hand the agent
        var newZoom = WhiteboardGeometry.FitTransform(content, new Size(1600, 1200)).M11; // WhiteboardWorkspaceBody.SnapshotSize since AC-1007

        Assert.True(CaptionHeight * oldZoom < LegibilityFloor, $"expected the old size to scale a caption below {LegibilityFloor}px, got {CaptionHeight * oldZoom:0.0}px");
        Assert.True(CaptionHeight * newZoom >= LegibilityFloor, $"expected the new size to keep a caption at or above {LegibilityFloor}px, got {CaptionHeight * newZoom:0.0}px");
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
