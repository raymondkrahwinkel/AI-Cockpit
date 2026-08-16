using Avalonia;

namespace Cockpit.Plugin.Diagram.Tests;

public class DiagramZoomMathTests
{
    [Fact]
    public void FitZoom_PicksTheLimitingAxisAndCentersTheOffset()
    {
        var viewport = new Size(800, 400);
        var diagram = new Size(400, 400); // limited by height, not width

        var zoom = DiagramZoomMath.FitZoom(viewport, diagram, minZoom: 0.1, maxZoom: 8.0);
        Assert.Equal(1.0, zoom, 3);

        var offset = DiagramZoomMath.CenteredPanOffset(viewport, diagram, zoom);
        Assert.Equal(200, offset.X, 3); // (800 - 400*1) / 2
        Assert.Equal(0, offset.Y, 3);
    }

    [Fact]
    public void FitZoom_IsZeroBeforeTheViewportHasBeenLaidOut()
    {
        Assert.Equal(0, DiagramZoomMath.FitZoom(default, new Size(400, 400), 0.1, 8.0));
    }

    [Fact]
    public void ZoomAround_KeepsTheAnchoredDiagramPointUnderThePointer()
    {
        var anchor = new Point(100, 100);
        var (zoom, panOffset) = DiagramZoomMath.ZoomAround(
            anchor, panOffset: default, currentZoom: 1.0, requestedZoom: 2.0, minZoom: 0.1, maxZoom: 8.0);

        Assert.Equal(2.0, zoom, 3);
        // The diagram point under the anchor was (100,100) at zoom 1; at zoom 2 it must render at the same anchor.
        var diagramPointOnScreen = new Point(100, 100) * zoom + panOffset;
        Assert.Equal(anchor.X, diagramPointOnScreen.X, 3);
        Assert.Equal(anchor.Y, diagramPointOnScreen.Y, 3);
    }

    [Fact]
    public void ZoomAround_ClampsToTheConfiguredRange()
    {
        var (zoom, _) = DiagramZoomMath.ZoomAround(default, default, 1.0, requestedZoom: 100.0, minZoom: 0.1, maxZoom: 8.0);
        Assert.Equal(8.0, zoom);

        (zoom, _) = DiagramZoomMath.ZoomAround(default, default, 1.0, requestedZoom: 0.0001, minZoom: 0.1, maxZoom: 8.0);
        Assert.Equal(0.1, zoom);
    }
}
