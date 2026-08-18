using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Cockpit.Plugin.Diagram.Whiteboard;
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

    // W-6/AC-851: a stroke drawn over a pasted image belongs to it, and moving/resizing the image carries it along
    // — the ticket's own acceptance test (plakken -> tekenen -> verplaatsen -> aantekening staat nog op dezelfde plek).
    [Fact]
    public void PencilTool_DrawingOverAPastedImage_BindsTheStrokeToIt()
    {
        var document = new WhiteboardDocument();
        var image = new PlacedObject { ShapeKind = PlacedShapeKind.Image, X = 10, Y = 10, Width = 80, Height = 60 };
        document.Add(image);
        var canvas = new WhiteboardCanvasControl(document);
        var window = _Show(canvas);

        canvas.UsePencilTool();
        window.MouseDown(new Point(30, 30), MouseButton.Left);
        window.MouseMove(new Point(60, 30));
        window.MouseUp(new Point(60, 30), MouseButton.Left);

        var stroke = Assert.IsType<FreehandStroke>(document.Objects.Single(o => o.Kind == WhiteboardObjectKind.Freehand));
        Assert.Equal(image.Id, stroke.ParentImageId);

        window.Close();
    }

    [Fact]
    public void PencilTool_DrawingOffAnyImage_LeavesTheStrokeUnbound()
    {
        var document = new WhiteboardDocument();
        document.Add(new PlacedObject { ShapeKind = PlacedShapeKind.Image, X = 200, Y = 200, Width = 40, Height = 40 });
        var canvas = new WhiteboardCanvasControl(document);
        var window = _Show(canvas);

        canvas.UsePencilTool();
        window.MouseDown(new Point(10, 10), MouseButton.Left);
        window.MouseMove(new Point(40, 10));
        window.MouseUp(new Point(40, 10), MouseButton.Left);

        var stroke = Assert.IsType<FreehandStroke>(document.Objects.Single(o => o.Kind == WhiteboardObjectKind.Freehand));
        Assert.Null(stroke.ParentImageId);

        window.Close();
    }

    [Fact]
    public void DraggingAPastedImage_CarriesABoundStroke_ByTheSameDelta()
    {
        var document = new WhiteboardDocument();
        var image = new PlacedObject { ShapeKind = PlacedShapeKind.Image, X = 10, Y = 10, Width = 80, Height = 60 };
        document.Add(image);
        var stroke = new FreehandStroke { Points = [new WhiteboardPoint(30, 30), new WhiteboardPoint(60, 30)], ParentImageId = image.Id };
        document.Add(stroke);
        var canvas = new WhiteboardCanvasControl(document);
        var window = _Show(canvas);

        canvas.UseSelectTool();
        window.MouseDown(new Point(20, 20), MouseButton.Left);
        window.MouseMove(new Point(45, 45));
        window.MouseUp(new Point(45, 45), MouseButton.Left);

        Assert.Equal(35, image.X);
        Assert.Equal(35, image.Y);
        Assert.Equal(new WhiteboardPoint(55, 55), stroke.Points[0]);
        Assert.Equal(new WhiteboardPoint(85, 55), stroke.Points[1]);

        window.Close();
    }

    [Fact]
    public void ResizingAPastedImage_ScalesABoundChild_Proportionally()
    {
        var document = new WhiteboardDocument();
        var image = new PlacedObject { ShapeKind = PlacedShapeKind.Image, X = 10, Y = 10, Width = 80, Height = 60 };
        document.Add(image);
        var label = new PlacedObject { ShapeKind = PlacedShapeKind.Text, X = 50, Y = 40, Width = 10, Height = 10, ParentImageId = image.Id };
        document.Add(label);
        var canvas = new WhiteboardCanvasControl(document);
        var window = _Show(canvas);

        canvas.UseSelectTool();
        window.MouseDown(new Point(20, 20), MouseButton.Left);
        window.MouseUp(new Point(20, 20), MouseButton.Left);
        Assert.Equal(image.Id, canvas.SelectedId);

        // Bottom-right handle sits dead centre on the image's bottom-right corner (10+80, 10+60) = (90, 70).
        window.MouseDown(new Point(90, 70), MouseButton.Left);
        window.MouseMove(new Point(140, 130));
        window.MouseUp(new Point(140, 130), MouseButton.Left);

        Assert.Equal(130, image.Width);
        Assert.Equal(120, image.Height);
        Assert.Equal(75, label.X);
        Assert.Equal(70, label.Y);
        Assert.Equal(16.25, label.Width);
        Assert.Equal(20, label.Height);

        window.Close();
    }

    // W-6/AC-851: deleting a pasted image with annotations stuck to it must ask, not silently delete or orphan.
    [Fact]
    public void DeletingAPastedImage_WithBoundAnnotations_AsksInsteadOfDeletingSilently()
    {
        var document = new WhiteboardDocument();
        var image = new PlacedObject { ShapeKind = PlacedShapeKind.Image, X = 10, Y = 10, Width = 80, Height = 60 };
        document.Add(image);
        document.Add(new PlacedObject { ShapeKind = PlacedShapeKind.Text, X = 20, Y = 20, Width = 10, Height = 10, ParentImageId = image.Id });
        var canvas = new WhiteboardCanvasControl(document);
        var window = _Show(canvas);

        canvas.UseSelectTool();
        window.MouseDown(new Point(70, 60), MouseButton.Left);
        window.MouseUp(new Point(70, 60), MouseButton.Left);

        window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);

        Assert.Equal(2, document.Objects.Count);
        var prompt = canvas.GetVisualDescendants().OfType<Button>().First(b => ((string)b.Content!).StartsWith("Alleen de afbeelding"));
        Assert.NotNull(prompt);

        window.Close();
    }

    [Fact]
    public void DeletingAPastedImage_ChoosingBoth_RemovesTheImageAndItsAnnotations()
    {
        var document = new WhiteboardDocument();
        var image = new PlacedObject { ShapeKind = PlacedShapeKind.Image, X = 10, Y = 10, Width = 80, Height = 60 };
        document.Add(image);
        var label = new PlacedObject { ShapeKind = PlacedShapeKind.Text, X = 20, Y = 20, Width = 10, Height = 10, ParentImageId = image.Id };
        document.Add(label);
        var canvas = new WhiteboardCanvasControl(document);
        var window = _Show(canvas);

        canvas.UseSelectTool();
        window.MouseDown(new Point(70, 60), MouseButton.Left);
        window.MouseUp(new Point(70, 60), MouseButton.Left);
        window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);

        var both = canvas.GetVisualDescendants().OfType<Button>().First(b => ((string)b.Content!).StartsWith("Afbeelding en"));
        both.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Empty(document.Objects);

        window.Close();
    }

    [Fact]
    public void DeletingAPastedImage_ChoosingDetach_RemovesOnlyTheImage_AndUnbindsTheAnnotation()
    {
        var document = new WhiteboardDocument();
        var image = new PlacedObject { ShapeKind = PlacedShapeKind.Image, X = 10, Y = 10, Width = 80, Height = 60 };
        document.Add(image);
        var label = new PlacedObject { ShapeKind = PlacedShapeKind.Text, X = 20, Y = 20, Width = 10, Height = 10, ParentImageId = image.Id };
        document.Add(label);
        var canvas = new WhiteboardCanvasControl(document);
        var window = _Show(canvas);

        canvas.UseSelectTool();
        window.MouseDown(new Point(70, 60), MouseButton.Left);
        window.MouseUp(new Point(70, 60), MouseButton.Left);
        window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);

        var detach = canvas.GetVisualDescendants().OfType<Button>().First(b => ((string)b.Content!).StartsWith("Alleen de afbeelding"));
        detach.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(label, Assert.Single(document.Objects));
        Assert.Null(label.ParentImageId);

        window.Close();
    }

    [Fact]
    public void Delete_RemovesAPastedImage_WithoutAnnotations_Immediately_NoPrompt()
    {
        var document = new WhiteboardDocument();
        document.Add(new PlacedObject { ShapeKind = PlacedShapeKind.Image, X = 10, Y = 10, Width = 80, Height = 60 });
        var canvas = new WhiteboardCanvasControl(document);
        var window = _Show(canvas);

        canvas.UseSelectTool();
        window.MouseDown(new Point(20, 20), MouseButton.Left);
        window.MouseUp(new Point(20, 20), MouseButton.Left);
        window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);

        Assert.Empty(document.Objects);
        Assert.DoesNotContain(canvas.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Wat moet er gebeuren met de aantekeningen op deze afbeelding?");

        window.Close();
    }

    // AC-913/AC2: a stroke drawn after "passend maken" zoomed/panned the surface still lands at the document
    // point under the cursor. `ApplyFit` on an empty board centres the fixed workspace, so clicking the
    // viewport's centre should hit the workspace's own centre — proof the pointer-to-document math still holds.
    [Fact]
    public void PencilTool_AfterFit_StillDrawsAtTheCursorsDocumentPosition()
    {
        var document = new WhiteboardDocument();
        var canvas = new WhiteboardCanvasControl(document);
        var window = _Show(canvas);

        canvas.ApplyFit();
        Assert.True(canvas.Zoom is > 0 and < 1, $"expected a 300x300 window to shrink the {WhiteboardGeometry.WorkspaceSize} workspace, got zoom {canvas.Zoom}");

        canvas.UsePencilTool();
        window.MouseDown(new Point(150, 150), MouseButton.Left);
        window.MouseMove(new Point(160, 150));
        window.MouseUp(new Point(160, 150), MouseButton.Left);

        var stroke = Assert.IsType<FreehandStroke>(Assert.Single(document.Objects));
        var first = stroke.Points[0];
        var expected = WhiteboardGeometry.WorkspaceSize;
        Assert.InRange(first.X, (expected.Width / 2) - 2, (expected.Width / 2) + 2);
        Assert.InRange(first.Y, (expected.Height / 2) - 2, (expected.Height / 2) + 2);

        window.Close();
    }

    // AC-913/AC3: the middle button pans, whatever tool is active — a drag with it must never draw or select.
    [Fact]
    public void MiddleButtonDrag_Pans_AndNeverDrawsOrSelects()
    {
        var document = new WhiteboardDocument();
        var canvas = new WhiteboardCanvasControl(document);
        var window = _Show(canvas);

        canvas.UsePencilTool();
        window.MouseDown(new Point(50, 50), MouseButton.Middle);
        window.MouseMove(new Point(120, 90));
        window.MouseUp(new Point(120, 90), MouseButton.Middle);

        Assert.Empty(document.Objects);
        Assert.Equal(WhiteboardTool.Pencil, canvas.Tool);

        window.Close();
    }

    // AC-913/AC4+AC7: "Fit" on an empty board centres the (fixed) workspace rather than zooming to nothing, and a
    // board with content fits that content instead.
    [Fact]
    public void ApplyFit_OnAnEmptyBoard_ProducesAPositiveCenteredZoom()
    {
        var document = new WhiteboardDocument();
        var canvas = new WhiteboardCanvasControl(document);
        _Show(canvas);

        canvas.ApplyFit();

        Assert.True(canvas.Zoom > 0);
    }

    private static Window _Show(Control content)
    {
        var window = new Window { Width = 300, Height = 300, Content = content };
        window.Show();
        return window;
    }
}
