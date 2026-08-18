using Avalonia;
using Avalonia.Media.Imaging;
using Cockpit.Plugin.Diagram.Whiteboard.Model;

namespace Cockpit.Plugin.Diagram.Whiteboard.Rendering;

// AC-822's panel and AC-823's MCP tool both need "what does the board look like right now" without knowing a
// single thing about the canvas control — this is the interface they share.
public interface IWhiteboardSnapshotRenderer
{
    Bitmap Render(WhiteboardDocument document, PixelSize size);
}

public sealed class WhiteboardSnapshotRenderer : IWhiteboardSnapshotRenderer
{
    // AC-913: the whole board, scaled to fit `size` — never a crop of the currently visible viewport, which would
    // make the snapshot depend on whatever window happened to render it (AC6).
    public Bitmap Render(WhiteboardDocument document, PixelSize size)
    {
        var target = new Size(size.Width, size.Height);
        var transform = WhiteboardGeometry.FitTransform(WhiteboardGeometry.ContentBounds(document), target);
        var visual = new WhiteboardSnapshotVisual(document, transform)
        {
            Width = size.Width,
            Height = size.Height,
        };

        visual.Measure(target);
        visual.Arrange(new Rect(target));

        var bitmap = new RenderTargetBitmap(size);
        bitmap.Render(visual);
        return bitmap;
    }
}
