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
    public Bitmap Render(WhiteboardDocument document, PixelSize size)
    {
        var visual = new WhiteboardSnapshotVisual(document)
        {
            Width = size.Width,
            Height = size.Height,
        };

        visual.Measure(new Size(size.Width, size.Height));
        visual.Arrange(new Rect(0, 0, size.Width, size.Height));

        var target = new RenderTargetBitmap(size);
        target.Render(visual);
        return target;
    }
}
