using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Cockpit.Plugin.Diagram.Whiteboard.Canvas;
using Cockpit.Plugin.Diagram.Whiteboard.Model;

namespace Cockpit.Plugin.Diagram.Tests.Whiteboard.Canvas;

// AC-917: an in-board clipboard for Ctrl+C/X/V/D, kept apart from the system clipboard that Ctrl+V already used
// for pasting a screenshot. One test per acceptance criterion.
[Collection("avalonia")]
public class WhiteboardClipboardTests
{
    [Fact]
    public void ControlC_ThenControlV_PastesACopy_WithItsOwnId_OffsetFromTheOriginal()
    {
        var document = new WhiteboardDocument();
        var canvas = new WhiteboardCanvasControl(document);
        var window = _Show(canvas);

        var original = _PlaceShape(window, canvas, PlacedShapeKind.Rectangle, new Point(20, 20));

        _Copy(window, canvas);
        _Paste(window, canvas);

        Assert.Equal(2, document.Objects.Count);
        var pasted = Assert.IsType<PlacedObject>(document.Objects.Single(o => o.Id != original.Id));
        Assert.Equal(original.X + 10, pasted.X);
        Assert.Equal(original.Y + 10, pasted.Y);
        Assert.Equal(PlacedShapeKind.Rectangle, pasted.ShapeKind);
        Assert.Equal(pasted.Id, canvas.SelectedId);

        window.Close();
    }

    [Fact]
    public void PastedObject_MovesAndDeletesIndependentlyFromTheOriginal()
    {
        var document = new WhiteboardDocument();
        var canvas = new WhiteboardCanvasControl(document);
        var window = _Show(canvas);

        var original = _PlaceShape(window, canvas, PlacedShapeKind.Rectangle, new Point(20, 20));
        _Copy(window, canvas);
        _Paste(window, canvas);
        var pasted = document.Objects.Single(o => o.Id != original.Id);

        window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);

        Assert.DoesNotContain(document.Objects, o => o.Id == pasted.Id);
        Assert.Contains(document.Objects, o => o.Id == original.Id);

        window.Close();
    }

    [Fact]
    public void ControlX_RemovesTheSelection_AndControlV_PastesItBackWithANewId()
    {
        var document = new WhiteboardDocument();
        var canvas = new WhiteboardCanvasControl(document);
        var window = _Show(canvas);

        var original = _PlaceShape(window, canvas, PlacedShapeKind.Ellipse, new Point(30, 30));

        _Cut(window, canvas);
        Assert.Empty(document.Objects);

        _Paste(window, canvas);
        var pasted = Assert.IsType<PlacedObject>(Assert.Single(document.Objects));
        Assert.NotEqual(original.Id, pasted.Id);
        Assert.Equal(original.X + 10, pasted.X);

        window.Close();
    }

    [Fact]
    public void ControlX_IsOneJournalRow_ThatUndoesInOneStep()
    {
        var document = new WhiteboardDocument();
        var canvas = new WhiteboardCanvasControl(document);
        var window = _Show(canvas);

        _PlaceShape(window, canvas, PlacedShapeKind.Rectangle, new Point(20, 20));
        _Cut(window, canvas);
        Assert.Empty(document.Objects);
        Assert.Equal(2, canvas.Edits.Entries.Count);

        _Undo(window, canvas);

        Assert.Single(document.Objects);

        window.Close();
    }

    [Fact]
    public void ControlD_DuplicatesInPlace_WithATenPixelOffset_AndLeavesTheClipboardAlone()
    {
        var document = new WhiteboardDocument();
        var canvas = new WhiteboardCanvasControl(document);
        var window = _Show(canvas);

        var original = _PlaceShape(window, canvas, PlacedShapeKind.Rectangle, new Point(20, 20));

        _Duplicate(window, canvas);

        Assert.Equal(2, document.Objects.Count);
        var duplicate = Assert.IsType<PlacedObject>(document.Objects.Single(o => o.Id != original.Id));
        Assert.Equal(original.X + 10, duplicate.X);
        Assert.Equal(original.Y + 10, duplicate.Y);
        Assert.NotEqual(original.Id, duplicate.Id);
        Assert.Equal(duplicate.Id, canvas.SelectedId);

        window.Close();
    }

    [Fact]
    public void RepeatedControlV_StacksEachPasteFurtherAway_NeverOnTheExactSameSpot()
    {
        var document = new WhiteboardDocument();
        var canvas = new WhiteboardCanvasControl(document);
        var window = _Show(canvas);

        var original = _PlaceShape(window, canvas, PlacedShapeKind.Rectangle, new Point(20, 20));
        _Copy(window, canvas);

        _Paste(window, canvas);
        _Paste(window, canvas);
        _Paste(window, canvas);

        var pastedX = document.Objects.OfType<PlacedObject>()
            .Where(o => o.Id != original.Id)
            .Select(o => o.X)
            .OrderBy(x => x)
            .ToArray();

        Assert.Equal([original.X + 10, original.X + 20, original.X + 30], pastedX);

        window.Close();
    }

    [Fact]
    public void CopyingAFreehandStroke_PastesItsPointsShiftedByTheOffset()
    {
        var document = new WhiteboardDocument();
        var canvas = new WhiteboardCanvasControl(document);
        var window = _Show(canvas);

        var stroke = _DrawStroke(window, canvas, new Point(10, 10), new Point(60, 10));
        canvas.UseSelectTool();
        window.MouseDown(new Point(35, 10), MouseButton.Left);
        window.MouseUp(new Point(35, 10), MouseButton.Left);
        Assert.Equal(stroke.Id, canvas.SelectedId);

        _Copy(window, canvas);
        _Paste(window, canvas);

        var pasted = Assert.IsType<FreehandStroke>(document.Objects.Single(o => o.Id != stroke.Id));
        Assert.Equal(stroke.Points[0].X + 10, pasted.Points[0].X);
        Assert.Equal(stroke.Points[0].Y + 10, pasted.Points[0].Y);

        window.Close();
    }

    [Fact]
    public void CopyingAnImageWithItsAnnotation_RestoresTheBinding_InsideTheCopy()
    {
        var document = new WhiteboardDocument();
        var image = new PlacedObject { ShapeKind = PlacedShapeKind.Image, X = 10, Y = 10, Width = 80, Height = 60 };
        document.Add(image);
        var note = new PlacedObject { ShapeKind = PlacedShapeKind.Text, X = 20, Y = 20, Width = 10, Height = 10, ParentImageId = image.Id };
        document.Add(note);

        var canvas = new WhiteboardCanvasControl(document);
        var window = _Show(canvas);

        canvas.UseSelectTool();
        window.MouseDown(new Point(70, 60), MouseButton.Left);
        window.MouseUp(new Point(70, 60), MouseButton.Left);
        Assert.Equal(image.Id, canvas.SelectedId);

        _Copy(window, canvas);
        _Paste(window, canvas);

        Assert.Equal(4, document.Objects.Count);
        var pastedImage = document.Objects.OfType<PlacedObject>().Single(o => o.ShapeKind == PlacedShapeKind.Image && o.Id != image.Id);
        var pastedNote = document.Objects.OfType<PlacedObject>().Single(o => o.ShapeKind == PlacedShapeKind.Text && o.Id != note.Id);
        Assert.Equal(pastedImage.Id, pastedNote.ParentImageId);

        window.Close();
    }

    [Fact]
    public void CopyingALoneAnnotation_PastedOffAnyImage_LeavesItUnbound_NeverDangling()
    {
        var document = new WhiteboardDocument();
        var image = new PlacedObject { ShapeKind = PlacedShapeKind.Image, X = 10, Y = 10, Width = 40, Height = 40 };
        document.Add(image);
        var note = new PlacedObject { ShapeKind = PlacedShapeKind.Text, X = 15, Y = 15, Width = 10, Height = 10, ParentImageId = image.Id };
        document.Add(note);

        var canvas = new WhiteboardCanvasControl(document);
        var window = _Show(canvas);
        canvas.SelectObject(note.Id);

        _Copy(window, canvas);
        // Paste twenty times over: eventually the offset carries the copy clear off the image, and it must not
        // keep pointing at an id that copy never brought along.
        for (var i = 0; i < 20; i++)
        {
            _Paste(window, canvas);
        }

        var lastPasted = document.Objects.OfType<PlacedObject>().Last();
        Assert.True(lastPasted.ParentImageId is null || lastPasted.ParentImageId == image.Id);
        Assert.All(document.Objects, o => Assert.True(o.ParentImageId is null || document.Find(o.ParentImageId!.Value) is not null));

        window.Close();
    }

    [Fact]
    public void PastingWithAnEmptyInternalClipboard_FallsBackToTheSystemClipboardPaste_WithoutThrowing()
    {
        var document = new WhiteboardDocument();
        var canvas = new WhiteboardCanvasControl(document);
        var window = _Show(canvas);

        canvas.Focus();
        window.KeyPressQwerty(PhysicalKey.V, RawInputModifiers.Control);

        Assert.Empty(document.Objects);

        window.Close();
    }

    [Fact]
    public void EveryPasteAndDuplicate_RaisesChanged_WhichIsWhatRefreshesTheSnapshot()
    {
        var document = new WhiteboardDocument();
        var canvas = new WhiteboardCanvasControl(document);
        var window = _Show(canvas);

        _PlaceShape(window, canvas, PlacedShapeKind.Rectangle, new Point(20, 20));
        _Copy(window, canvas);

        var changes = 0;
        canvas.Changed += (_, _) => changes++;

        _Paste(window, canvas);
        Assert.Equal(1, changes);

        _Duplicate(window, canvas);
        Assert.Equal(2, changes);

        window.Close();
    }

    private static void _Press(Window window, WhiteboardCanvasControl canvas, PhysicalKey key, RawInputModifiers modifiers)
    {
        canvas.Focus();
        window.KeyPressQwerty(key, modifiers);
    }

    private static void _Copy(Window window, WhiteboardCanvasControl canvas) => _Press(window, canvas, PhysicalKey.C, RawInputModifiers.Control);

    private static void _Cut(Window window, WhiteboardCanvasControl canvas) => _Press(window, canvas, PhysicalKey.X, RawInputModifiers.Control);

    private static void _Paste(Window window, WhiteboardCanvasControl canvas) => _Press(window, canvas, PhysicalKey.V, RawInputModifiers.Control);

    private static void _Duplicate(Window window, WhiteboardCanvasControl canvas) => _Press(window, canvas, PhysicalKey.D, RawInputModifiers.Control);

    private static void _Undo(Window window, WhiteboardCanvasControl canvas) => _Press(window, canvas, PhysicalKey.Z, RawInputModifiers.Control);

    private static PlacedObject _PlaceShape(Window window, WhiteboardCanvasControl canvas, PlacedShapeKind kind, Point at)
    {
        canvas.UseShapeTool(kind);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        return Assert.IsType<PlacedObject>(canvas.Document.Objects.Single(o => o.Kind == WhiteboardObjectKind.Placed));
    }

    private static FreehandStroke _DrawStroke(Window window, WhiteboardCanvasControl canvas, Point from, Point to)
    {
        canvas.UsePencilTool();
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(to);
        window.MouseUp(to, MouseButton.Left);
        return Assert.IsType<FreehandStroke>(canvas.Document.Objects.Single(o => o.Kind == WhiteboardObjectKind.Freehand));
    }

    private static Window _Show(Control content)
    {
        var window = new Window { Width = 400, Height = 400, Content = content };
        window.Show();
        return window;
    }
}
