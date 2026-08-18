using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Cockpit.Core.Abstractions.Whiteboard;
using Cockpit.Plugin.Diagram.Collab;
using Cockpit.Plugin.Diagram.Whiteboard.Canvas;
using Cockpit.Plugin.Diagram.Whiteboard.Model;

namespace Cockpit.Plugin.Diagram.Tests.Whiteboard.Canvas;

// AC-912: the operator's own handlings are journaled and reversible, which until now only the agent's were. One test
// per acceptance criterion, plus the two refusals that keep an undo from reaching work that landed since.
[Collection("avalonia")]
public class WhiteboardOperatorUndoTests
{
    [Fact]
    public void ControlZ_TakesBackTheLastOwnHandling_AndControlYPutsItBack()
    {
        var document = new WhiteboardDocument();
        var canvas = new WhiteboardCanvasControl(document);
        var window = _Show(canvas);

        _DrawStroke(window, canvas, from: new Point(10, 10), to: new Point(60, 10));
        Assert.Single(document.Objects);
        var strokeId = document.Objects[0].Id;

        _Undo(window, canvas);
        Assert.Empty(document.Objects);

        _Redo(window, canvas);
        Assert.Equal(strokeId, Assert.Single(document.Objects).Id);

        window.Close();
    }

    [Fact]
    public void ControlZ_PressedAgain_WalksFurtherBack()
    {
        var document = new WhiteboardDocument();
        var canvas = new WhiteboardCanvasControl(document);
        var window = _Show(canvas);

        _PlaceShape(window, canvas, PlacedShapeKind.Rectangle, new Point(20, 20));
        _PlaceShape(window, canvas, PlacedShapeKind.Ellipse, new Point(200, 200));
        Assert.Equal(2, document.Objects.Count);

        _Undo(window, canvas);
        _Undo(window, canvas);

        Assert.Empty(document.Objects);

        window.Close();
    }

    [Fact]
    public void ANewHandling_EmptiesTheRedoBranch()
    {
        var document = new WhiteboardDocument();
        var canvas = new WhiteboardCanvasControl(document);
        var window = _Show(canvas);

        _PlaceShape(window, canvas, PlacedShapeKind.Rectangle, new Point(20, 20));
        _Undo(window, canvas);
        Assert.Empty(document.Objects);

        _PlaceShape(window, canvas, PlacedShapeKind.Ellipse, new Point(200, 200));
        _Redo(window, canvas);

        Assert.Equal(PlacedShapeKind.Ellipse, Assert.IsType<PlacedObject>(Assert.Single(document.Objects)).ShapeKind);

        window.Close();
    }

    [Fact]
    public void EveryOperatorHandling_IsAJournalRow_ThatRevertsOnItsOwn()
    {
        var document = new WhiteboardDocument();
        var canvas = new WhiteboardCanvasControl(document);
        var window = _Show(canvas);
        var registry = new ActivityStripTests.FakeWhiteboardRegistry();
        var journal = new WhiteboardActivityJournal(registry, canvas.Edits);

        _PlaceShape(window, canvas, PlacedShapeKind.Rectangle, new Point(20, 20));
        _DrawStroke(window, canvas, from: new Point(200, 200), to: new Point(260, 200));

        var rows = journal.History(document.Id);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal("operator", row.Origin));
        Assert.All(rows, row => Assert.True(row.CanRevert));

        // The oldest row, not the newest: a strip revert reaches one handling on its own, not just the top of a stack.
        Assert.Null(journal.Revert(document.Id, rows[0].Id));

        Assert.Equal(WhiteboardObjectKind.Freehand, Assert.Single(document.Objects).Kind);
        Assert.Empty(registry.RevertCalls);

        window.Close();
    }

    [Fact]
    public void AnAgentRow_StillRevertsThroughTheRegistry()
    {
        var document = new WhiteboardDocument();
        var canvas = new WhiteboardCanvasControl(document);
        var window = _Show(canvas);
        var registry = new ActivityStripTests.FakeWhiteboardRegistry();
        registry.Seed(document.Id, new WhiteboardHistoryEntry(
            "agent-entry", "pane-1", WhiteboardHistoryKind.Place, Guid.NewGuid().ToString(), "placed a Rectangle", DateTime.Now.AddMinutes(-1), Reverted: false));
        var journal = new WhiteboardActivityJournal(registry, canvas.Edits);

        _PlaceShape(window, canvas, PlacedShapeKind.Rectangle, new Point(20, 20));

        var rows = journal.History(document.Id);
        Assert.Equal(new[] { "pane-1", "operator" }, rows.Select(row => row.Origin).ToArray());

        journal.Revert(document.Id, "agent-entry");

        Assert.Equal(new[] { "agent-entry" }, registry.RevertCalls.ToArray());

        window.Close();
    }

    [Fact]
    public void UndoingADeletion_RestoresTheObjectWithItsOwnIdentity_AndItsImageBinding()
    {
        var document = new WhiteboardDocument();
        var image = new PlacedObject { ShapeKind = PlacedShapeKind.Image, X = 10, Y = 10, Width = 120, Height = 100 };
        document.Add(image);
        var canvas = new WhiteboardCanvasControl(document);
        var window = _Show(canvas);

        _DrawStroke(window, canvas, from: new Point(30, 30), to: new Point(90, 30));
        var stroke = Assert.IsType<FreehandStroke>(document.Objects.Single(o => o.Kind == WhiteboardObjectKind.Freehand));
        Assert.Equal(image.Id, stroke.ParentImageId);

        canvas.UseSelectTool();
        canvas.SelectObject(stroke.Id);
        _PressDelete(window, canvas);
        Assert.Single(document.Objects);

        _Undo(window, canvas);

        var restored = Assert.IsType<FreehandStroke>(document.Objects.Single(o => o.Kind == WhiteboardObjectKind.Freehand));
        Assert.Same(stroke, restored);
        Assert.Equal(image.Id, restored.ParentImageId);

        window.Close();
    }

    [Fact]
    public void UndoingADetachingDelete_ReBindsTheAnnotationsToTheRestoredImage()
    {
        var document = new WhiteboardDocument();
        var image = new PlacedObject { ShapeKind = PlacedShapeKind.Image, X = 10, Y = 10, Width = 120, Height = 100 };
        document.Add(image);
        var canvas = new WhiteboardCanvasControl(document);
        var window = _Show(canvas);

        _DrawStroke(window, canvas, from: new Point(30, 30), to: new Point(90, 30));
        var stroke = Assert.IsType<FreehandStroke>(document.Objects.Single(o => o.Kind == WhiteboardObjectKind.Freehand));

        canvas.UseSelectTool();
        canvas.SelectObject(image.Id);
        _PressDelete(window, canvas);
        _ClickButton(canvas, "Just the image — detach annotations");
        Assert.Null(stroke.ParentImageId);

        _Undo(window, canvas);

        Assert.Contains(image, document.Objects);
        Assert.Equal(image.Id, stroke.ParentImageId);

        window.Close();
    }

    [Fact]
    public void UndoingAnImageResize_LeavesAnAnnotationThatLandedSince_WhereItIs()
    {
        var document = new WhiteboardDocument();
        var image = new PlacedObject { ShapeKind = PlacedShapeKind.Image, X = 10, Y = 10, Width = 100, Height = 100 };
        document.Add(image);
        var canvas = new WhiteboardCanvasControl(document);
        var window = _Show(canvas);

        _DrawStroke(window, canvas, from: new Point(30, 30), to: new Point(90, 30));
        var stroke = Assert.IsType<FreehandStroke>(document.Objects.Single(o => o.Kind == WhiteboardObjectKind.Freehand));

        canvas.UseSelectTool();
        canvas.SelectObject(image.Id);
        _DragBottomRightHandle(window, canvas, to: new Point(210, 210));
        Assert.Equal(200, image.Width);
        Assert.Equal(50, stroke.Points[0].X);

        // The agent's own note (AC-854), landed on the image after the resize — undo carries the stroke it did move
        // back, and leaves this one exactly where the agent put it.
        var note = new PlacedObject { ShapeKind = PlacedShapeKind.StickyNote, X = 40, Y = 40, Width = 30, Height = 30, PlacedByAgent = true, ParentImageId = image.Id };
        document.Add(note);

        _Undo(window, canvas);

        Assert.Equal(100, image.Width);
        Assert.Equal(30, stroke.Points[0].X);
        Assert.Equal(40, note.X);
        Assert.Equal(30, note.Width);

        window.Close();
    }

    [Fact]
    public void UndoingAnAdd_IsRefusedWhileSomethingIsAnchoredToTheObject()
    {
        var document = new WhiteboardDocument();
        var canvas = new WhiteboardCanvasControl(document);
        var window = _Show(canvas);
        var refusals = new List<string>();
        canvas.UndoRefused += refusals.Add;

        _PlaceShape(window, canvas, PlacedShapeKind.Rectangle, new Point(60, 60));
        var anchor = Assert.IsType<PlacedObject>(Assert.Single(document.Objects));

        // The agent's own note (AC-854), anchored to what the operator just put down — the paste-a-screenshot case
        // reaches this same guard, and a bare shape is the one an automated test can place without a clipboard.
        document.Add(new PlacedObject { ShapeKind = PlacedShapeKind.StickyNote, X = 65, Y = 65, PlacedByAgent = true, ParentImageId = anchor.Id });

        _Undo(window, canvas);

        Assert.Equal(new[] { "There is work on this object — remove that first." }, refusals.ToArray());
        Assert.Contains(anchor, document.Objects);

        window.Close();
    }

    [Fact]
    public void ControlZ_DuringAGesture_DoesNothing()
    {
        var document = new WhiteboardDocument();
        var canvas = new WhiteboardCanvasControl(document);
        var window = _Show(canvas);

        _PlaceShape(window, canvas, PlacedShapeKind.Rectangle, new Point(20, 20));
        Assert.Single(document.Objects);

        canvas.UsePencilTool();
        window.MouseDown(new Point(200, 200), MouseButton.Left);
        window.MouseMove(new Point(240, 200));
        _Undo(window, canvas);

        Assert.Single(document.Objects);

        // The gesture still finishes as its own handling rather than being aborted halfway.
        window.MouseUp(new Point(240, 200), MouseButton.Left);
        Assert.Equal(2, document.Objects.Count);

        window.Close();
    }

    [Fact]
    public void EveryUndoAndRedo_RaisesChanged_WhichIsWhatRefreshesTheSnapshot()
    {
        var document = new WhiteboardDocument();
        var canvas = new WhiteboardCanvasControl(document);
        var window = _Show(canvas);

        _PlaceShape(window, canvas, PlacedShapeKind.Rectangle, new Point(20, 20));

        var changes = 0;
        canvas.Changed += (_, _) => changes++;

        _Undo(window, canvas);
        Assert.Equal(1, changes);

        _Redo(window, canvas);
        Assert.Equal(2, changes);

        window.Close();
    }

    // The canvas only sees a key it holds focus for, and half these tests select through SelectObject rather than a
    // pointer press — which is what focuses it in the app itself.
    private static void _Press(Window window, WhiteboardCanvasControl canvas, PhysicalKey key, RawInputModifiers modifiers)
    {
        canvas.Focus();
        window.KeyPressQwerty(key, modifiers);
    }

    private static void _Undo(Window window, WhiteboardCanvasControl canvas) => _Press(window, canvas, PhysicalKey.Z, RawInputModifiers.Control);

    private static void _Redo(Window window, WhiteboardCanvasControl canvas) => _Press(window, canvas, PhysicalKey.Y, RawInputModifiers.Control);

    private static void _PressDelete(Window window, WhiteboardCanvasControl canvas) => _Press(window, canvas, PhysicalKey.Delete, RawInputModifiers.None);

    private static void _DrawStroke(Window window, WhiteboardCanvasControl canvas, Point from, Point to)
    {
        canvas.UsePencilTool();
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(to);
        window.MouseUp(to, MouseButton.Left);
    }

    private static void _PlaceShape(Window window, WhiteboardCanvasControl canvas, PlacedShapeKind kind, Point at)
    {
        canvas.UseShapeTool(kind);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
    }

    private static void _DragBottomRightHandle(Window window, WhiteboardCanvasControl canvas, Point to)
    {
        // Without an arrange pass every handle still sits at 0,0 with no size, so the press lands on whichever one
        // the hit test happens to pick — a resize of the wrong corner rather than the one this test drives.
        window.UpdateLayout();
        var handle = canvas.GetVisualDescendants().OfType<ResizeHandle>().Single(h => h.Corner == HandleCorner.BottomRight);
        var origin = handle.TranslatePoint(new Point(5, 5), canvas) ?? default;
        window.MouseDown(origin, MouseButton.Left);
        window.MouseMove(to);
        window.MouseUp(to, MouseButton.Left);
    }

    private static void _ClickButton(WhiteboardCanvasControl canvas, string content)
    {
        var button = canvas.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, content));
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    }

    private static Window _Show(Control content)
    {
        var window = new Window { Width = 400, Height = 400, Content = content };
        window.Show();
        return window;
    }
}
