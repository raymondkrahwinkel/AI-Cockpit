using Avalonia;

namespace Cockpit.Plugin.Diagram;

// The zoom/pan arithmetic behind DiagramWorkspaceBody (AC-837), pulled out so it is testable without an Avalonia
// control tree: fitting a diagram to its viewport, and zooming around a fixed screen point without it drifting.
internal static class DiagramZoomMath
{
    public static double ClampZoom(double zoom, double minZoom, double maxZoom) => Math.Clamp(zoom, minZoom, maxZoom);

    // 0 when either size is degenerate (not yet laid out) — callers treat that as "nothing to fit yet".
    public static double FitZoom(Size viewport, Size diagram, double minZoom, double maxZoom)
    {
        if (viewport.Width <= 0 || viewport.Height <= 0 || diagram.Width <= 0 || diagram.Height <= 0)
        {
            return 0;
        }

        return ClampZoom(Math.Min(viewport.Width / diagram.Width, viewport.Height / diagram.Height), minZoom, maxZoom);
    }

    public static Vector CenteredPanOffset(Size viewport, Size diagram, double zoom) =>
        new((viewport.Width - diagram.Width * zoom) / 2, (viewport.Height - diagram.Height * zoom) / 2);

    // The diagram point under `anchor` stays under `anchor` after the zoom change — same feel as a browser's
    // Ctrl+scroll or ImagePreviewWindow's image zoom.
    public static (double Zoom, Vector PanOffset) ZoomAround(
        Point anchor, Vector panOffset, double currentZoom, double requestedZoom, double minZoom, double maxZoom)
    {
        var newZoom = ClampZoom(requestedZoom, minZoom, maxZoom);
        var diagramPoint = (anchor - panOffset) / currentZoom;
        var newPanOffset = anchor - diagramPoint * newZoom;
        return (newZoom, newPanOffset);
    }
}
