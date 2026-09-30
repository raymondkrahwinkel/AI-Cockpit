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
    // W-6/AC-851: a stroke drawn over a pasted image belongs to it, and moving/resizing the image carries it along
    // — the ticket's own acceptance test (plakken -> tekenen -> verplaatsen -> aantekening staat nog op dezelfde plek).

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
        var prompt = canvas.GetVisualDescendants().OfType<Button>().First(b => ((string)b.Content!).StartsWith("Just the image"));
        Assert.NotNull(prompt);

        window.Close();
    }

    // AC-913/AC2: a stroke drawn after "passend maken" zoomed/panned the surface still lands at the document
    // point under the cursor. `ApplyFit` on an empty board centres the fixed workspace, so clicking the
    // viewport's centre should hit the workspace's own centre — proof the pointer-to-document math still holds.

    // AC-913/AC3: the middle button pans, whatever tool is active — a drag with it must never draw or select.

    // AC-913/AC4+AC7: "Fit" on an empty 300x300 window fits the fixed 2400x1800 workspace width-limited (zoom
    // 0.125) and centres the 75px of vertical slack that leaves — not just some positive zoom, but a workspace
    // whose top-left and bottom-right corners land at the predicted, symmetrically-margined screen positions.

    // AC-913/AC2: select, drag and resize must resolve to correct document positions after a pan too, not only
    // after a zoom — panning is a plain additive screen-space offset, so a bug here would show up as objects
    // moving/resizing by the wrong amount rather than not being hit at all.

    // AC-913/AC2: the click-to-edit gesture (a second press on an already-selected shape, no drag) must still find
    // the right shape and land the editor in the right place under a combined zoom AND pan — the case in between
    // the zoom-only and pan-only coverage above.

    // AC-916 AC10: a sweep over a whole stroke erases it, and never partially — the ticket's own scope cut.

    // AC-916 AC13: one sweep over several objects is one Ctrl+Z, not one per object.

    // AC-916 AC11: the gum never takes a pasted image — only selecting it and pressing Delete does, so the
    // "detach or delete annotations" prompt keeps working.

    // AC-916 AC12: moving the pointer with the gum tool active, but no button pressed, must change nothing.

    // AC-916 AC15/AC-924: with the gum active, a right-click must not start a sweep — the shared guard in
    // OnPointerPressed, not a second filter in the gum branch.

    // AC-916 AC7/AC8: recolouring the selection is one journaled handling, and Ctrl+D carries the colour along.

    // AC-982 AC1/AC2/AC4: SetColor(null) is the way back a picked swatch previously had none of — it clears the
    // pending colour for whatever gets drawn/placed next and, on a selection, drops that object's override too, so
    // it falls back to the fixed default for its kind. Undoable the same as any other recolour.

    // AC-924 criterion 4/14: the pencil tool has no selection concept, so a right-click there opens no menu — and,
    // per the shared guard, never starts a stroke either.

    // AC-924 criteria 1/3/14: a right-click with Select active opens the menu on whatever it landed on, selecting it
    // exactly as a left-click would (replacing whatever was selected before) — and it never arms a drag: the object
    // stays exactly where it was.

    // AC-924 criterion 5/14: the menu's own "Duplicate" runs through the exact same method Ctrl+D does.

    // AC-924 criterion 14: the keyboard route (Menu key / Shift+F10) opens the same menu on the current selection —
    // a parameterless ContextRequestedEventArgs carries no position, same convention SessionContextMenuTargetView's
    // own keyboard-route test uses.

    // AC-924 criterion 14: with nothing selected, the keyboard route opens nothing.

    private static Window _Show(Control content)
    {
        var window = new Window { Width = 300, Height = 300, Content = content };
        window.Show();
        return window;
    }
}
