using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Cockpit.Plugin.Diagram.Whiteboard.Canvas;
using Cockpit.Plugin.Diagram.Whiteboard.Model;

namespace Cockpit.Plugin.Diagram.Tests.Whiteboard.Canvas;

// The three pointer flows the ticket calls out as the non-trivial part: freehand capture, shape drag-to-place,
// and deleting whatever is selected. Not a full interaction suite — the model and rendering tests carry that.
[Collection("avalonia")]
public class WhiteboardCanvasControlTests
{
    [Fact]
    public void PencilTool_DraggingThePointer_AddsAFreehandStroke()
    {
        var document = new WhiteboardDocument();
        var canvas = new WhiteboardCanvasControl(document);
        var window = _Show(canvas);

        canvas.UsePencilTool();
        window.MouseDown(new Point(10, 10), MouseButton.Left);
        window.MouseMove(new Point(40, 10));
        window.MouseUp(new Point(40, 10), MouseButton.Left);

        var stroke = Assert.IsType<FreehandStroke>(Assert.Single(document.Objects));
        Assert.True(stroke.Points.Count >= 2);

        window.Close();
    }

    [Fact]
    public void ShapeTool_DraggingARectangle_AddsAPlacedObject_AndSwitchesBackToSelect()
    {
        var document = new WhiteboardDocument();
        var canvas = new WhiteboardCanvasControl(document);
        var window = _Show(canvas);

        canvas.UseShapeTool(PlacedShapeKind.Ellipse);
        window.MouseDown(new Point(10, 10), MouseButton.Left);
        window.MouseMove(new Point(60, 50));
        window.MouseUp(new Point(60, 50), MouseButton.Left);

        var placed = Assert.IsType<PlacedObject>(Assert.Single(document.Objects));
        Assert.Equal(PlacedShapeKind.Ellipse, placed.ShapeKind);
        Assert.Equal(WhiteboardTool.Select, canvas.Tool);

        window.Close();
    }

    [Fact]
    public void Delete_RemovesTheSelectedPlacedObject()
    {
        var document = new WhiteboardDocument();
        var canvas = new WhiteboardCanvasControl(document);
        var window = _Show(canvas);

        canvas.UseShapeTool(PlacedShapeKind.Rectangle);
        window.MouseDown(new Point(10, 10), MouseButton.Left);
        window.MouseMove(new Point(60, 50));
        window.MouseUp(new Point(60, 50), MouseButton.Left);
        Assert.Single(document.Objects);

        window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);

        Assert.Empty(document.Objects);

        window.Close();
    }

    [Fact]
    public void Delete_RemovesASelectedFreehandStroke()
    {
        var document = new WhiteboardDocument();
        var canvas = new WhiteboardCanvasControl(document);
        var window = _Show(canvas);

        canvas.UsePencilTool();
        window.MouseDown(new Point(10, 10), MouseButton.Left);
        window.MouseMove(new Point(90, 10));
        window.MouseUp(new Point(90, 10), MouseButton.Left);
        Assert.Single(document.Objects);

        // Selecting is a click near the path, the same tolerance the pencil's own line is drawn with.
        canvas.UseSelectTool();
        window.MouseDown(new Point(50, 10), MouseButton.Left);
        window.MouseUp(new Point(50, 10), MouseButton.Left);
        Assert.Equal(document.Objects.Single().Id, canvas.SelectedId);

        window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);

        Assert.Empty(document.Objects);

        window.Close();
    }

    private static Window _Show(Control content)
    {
        var window = new Window { Width = 300, Height = 300, Content = content };
        window.Show();
        return window;
    }
}
