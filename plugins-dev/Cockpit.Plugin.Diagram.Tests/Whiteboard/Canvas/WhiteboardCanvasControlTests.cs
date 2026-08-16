using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
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

    [Fact]
    public void MarkerTool_DraggingThePointer_AddsAMarkedFreehandStroke()
    {
        var document = new WhiteboardDocument();
        var canvas = new WhiteboardCanvasControl(document);
        var window = _Show(canvas);

        canvas.UseMarkerTool();
        window.MouseDown(new Point(10, 10), MouseButton.Left);
        window.MouseMove(new Point(40, 10));
        window.MouseUp(new Point(40, 10), MouseButton.Left);

        var stroke = Assert.IsType<FreehandStroke>(Assert.Single(document.Objects));
        Assert.True(stroke.IsMarker);

        window.Close();
    }

    [Fact]
    public void StickyNoteTool_ClickWithoutDrag_PlacesANoteSizedSquare()
    {
        var document = new WhiteboardDocument();
        var canvas = new WhiteboardCanvasControl(document);
        var window = _Show(canvas);

        canvas.UseShapeTool(PlacedShapeKind.StickyNote);
        window.MouseDown(new Point(50, 50), MouseButton.Left);
        window.MouseUp(new Point(50, 50), MouseButton.Left);

        var placed = Assert.IsType<PlacedObject>(Assert.Single(document.Objects));
        Assert.Equal(PlacedShapeKind.StickyNote, placed.ShapeKind);
        Assert.Equal(140, placed.Width);
        Assert.Equal(140, placed.Height);

        window.Close();
    }

    [Fact]
    public void ClickingAnAlreadySelectedShapeAgain_OpensATextEditor_AndCommitsOnBlur()
    {
        var document = new WhiteboardDocument();
        var canvas = new WhiteboardCanvasControl(document);
        var window = _Show(canvas);

        canvas.UseShapeTool(PlacedShapeKind.Text);
        window.MouseDown(new Point(50, 50), MouseButton.Left);
        window.MouseUp(new Point(50, 50), MouseButton.Left);
        var placed = Assert.IsType<PlacedObject>(Assert.Single(document.Objects));

        // Same spot, no drag — the object is already selected from the placement above.
        window.MouseDown(new Point(50, 50), MouseButton.Left);
        window.MouseUp(new Point(50, 50), MouseButton.Left);

        var editor = Assert.Single(canvas.GetVisualDescendants().OfType<TextBox>());
        editor.Text = "Hallo bord";

        // Pressing elsewhere steals focus from the editor, which commits on blur.
        window.MouseDown(new Point(5, 5), MouseButton.Left);
        window.MouseUp(new Point(5, 5), MouseButton.Left);

        Assert.Equal("Hallo bord", placed.Text);
        Assert.Empty(canvas.GetVisualDescendants().OfType<TextBox>());

        window.Close();
    }

    [Fact]
    public void DraggingAnAlreadySelectedShape_DoesNotOpenTheTextEditor()
    {
        var document = new WhiteboardDocument();
        var canvas = new WhiteboardCanvasControl(document);
        var window = _Show(canvas);

        canvas.UseShapeTool(PlacedShapeKind.Text);
        window.MouseDown(new Point(50, 50), MouseButton.Left);
        window.MouseUp(new Point(50, 50), MouseButton.Left);

        window.MouseDown(new Point(50, 50), MouseButton.Left);
        window.MouseMove(new Point(90, 90));
        window.MouseUp(new Point(90, 90), MouseButton.Left);

        Assert.Empty(canvas.GetVisualDescendants().OfType<TextBox>());

        window.Close();
    }

    [Fact]
    public void EmptyDocument_ShowsTheEmptyStateOverlay_UntilSomethingIsPlaced()
    {
        var document = new WhiteboardDocument();
        var canvas = new WhiteboardCanvasControl(document);
        var window = _Show(canvas);

        var overlay = Assert.Single(canvas.GetVisualDescendants().OfType<WhiteboardCanvasControl.EmptyStateOverlay>());
        Assert.True(overlay.IsVisible);

        canvas.UseShapeTool(PlacedShapeKind.Rectangle);
        window.MouseDown(new Point(10, 10), MouseButton.Left);
        window.MouseUp(new Point(10, 10), MouseButton.Left);

        Assert.False(overlay.IsVisible);

        window.Close();
    }

    [Fact]
    public void AnObjectAddedToTheDocumentFromOutside_IsDrawnAndCanBeRemovedAgain()
    {
        // How an agent's placement reaches the live board (AC-854): the workspace adds it to the same document, so
        // the canvas has to follow the document rather than only its own pointer gestures.
        var document = new WhiteboardDocument();
        var canvas = new WhiteboardCanvasControl(document);
        var window = _Show(canvas);

        var placed = new PlacedObject { ShapeKind = PlacedShapeKind.Rectangle, X = 10, Y = 10, PlacedByAgent = true };
        document.Add(placed);

        var control = Assert.Single(canvas.GetVisualDescendants().OfType<PlacedObjectControl>());
        Assert.Same(placed, control.Model);

        document.Remove(placed.Id);

        Assert.Empty(canvas.GetVisualDescendants().OfType<PlacedObjectControl>());

        window.Close();
    }

    private static Window _Show(Control content)
    {
        var window = new Window { Width = 300, Height = 300, Content = content };
        window.Show();
        return window;
    }
}
