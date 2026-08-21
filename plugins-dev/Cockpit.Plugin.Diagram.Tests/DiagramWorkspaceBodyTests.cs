using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.Plugin.Diagram.Whiteboard.Canvas;

namespace Cockpit.Plugin.Diagram.Tests;

// AC-978: a fresh, empty diagram used to open at a clamped 800% over a blank canvas (the near-zero SVG bounds of
// a node-less flowchart forced DiagramZoomMath.FitZoom to its MaxZoom) with no hint at all what to do next.
[Collection("avalonia")]
public class DiagramWorkspaceBodyTests
{
    [Fact]
    public void EmptyDiagram_OpensAt100PercentZoom_NotTheClampedFit()
    {
        var body = new DiagramWorkspaceBody(new ActivityStripTests.FakeHost(), DiagramDocument.New("Test diagram"), null);
        var window = _Show(body);

        Assert.Equal("100%", _ZoomLabelText(body));

        window.Close();
    }

    [Fact]
    public void EmptyDiagram_ShowsTheEmptyStateHint_UntilANodeExists()
    {
        var body = new DiagramWorkspaceBody(new ActivityStripTests.FakeHost(), DiagramDocument.New("Test diagram"), null);
        var window = _Show(body);

        var overlay = Assert.Single(body.GetVisualDescendants().OfType<WhiteboardCanvasControl.EmptyStateOverlay>());
        Assert.True(overlay.IsVisible);

        window.Close();
    }

    [Fact]
    public void DiagramWithNodes_NeverShowsTheEmptyStateHint_AndFitsInsteadOf100Percent()
    {
        var document = DiagramDocument.New("Test diagram", "flowchart LR\nA-->B");
        var body = new DiagramWorkspaceBody(new ActivityStripTests.FakeHost(), document, null);
        var window = _Show(body);

        var overlay = Assert.Single(body.GetVisualDescendants().OfType<WhiteboardCanvasControl.EmptyStateOverlay>());
        Assert.False(overlay.IsVisible);
        Assert.NotEqual("100%", _ZoomLabelText(body));

        window.Close();
    }

    private static Window _Show(Control content)
    {
        var window = new Window { Content = content, Width = 900, Height = 640 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static string _ZoomLabelText(DiagramWorkspaceBody body) =>
        ((TextBlock)typeof(DiagramWorkspaceBody).GetField("_zoomLabel", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(body)!).Text ?? "";
}
