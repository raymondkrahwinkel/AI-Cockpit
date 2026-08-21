using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.Core.Abstractions.Diagrams;
using Cockpit.Core.Abstractions.Whiteboard;
using Cockpit.Core.Abstractions.Wireframe;
using Cockpit.Plugin.Diagram.Whiteboard;
using Cockpit.Plugin.Diagram.Whiteboard.Model;
using Cockpit.Plugin.Diagram.Wireframe;

namespace Cockpit.Plugin.Diagram.Tests;

// AC-974: the shared coupling bar (Collab/CouplingBarFactory) must never let its info group (title, pip, label,
// chips) paint over the action buttons, on any surface, at that surface's own default window size (900x640 for
// diagram/whiteboard, 960x680 for wireframe). See the PR description for the audit's findings.
[Collection("avalonia")]
public class CollabBarOverflowTests
{
    [Fact]
    public void Whiteboard_CoupledWithDeniedCapabilitiesAtDefaultWindowSize_InviteButtonAndChipsDoNotOverlap()
    {
        var registry = new ActivityStripTests.FakeWhiteboardRegistry();
        var host = new ActivityStripTests.FakeHost(whiteboard: registry);
        var document = new WhiteboardDocument(title: "Test whiteboard");
        var body = new WhiteboardWorkspaceBody(host, document, null);
        var window = _Show(body, width: 900, height: 640);

        registry.SetCoupling(document.Id, new WhiteboardCoupling("a-very-long-session-name-that-runs-on", CanRead: false));
        Dispatcher.UIThread.RunJobs();

        _AssertCollabBarDoesNotOverlap(body, window);

        window.Close();
    }

    [Fact]
    public void Diagram_CoupledWithDeniedCapabilitiesAtDefaultWindowSize_DisconnectButtonAndChipsDoNotOverlap()
    {
        var registry = new ActivityStripTests.FakeDiagramRegistry();
        var host = new ActivityStripTests.FakeHost(registry);
        var document = DiagramDocument.New("Test diagram");
        var body = new DiagramWorkspaceBody(host, document, null);
        var window = _Show(body, width: 900, height: 640);

        registry.SetCoupling(document.Id, new DiagramCoupling("a-very-long-session-name-that-runs-on", CanRead: false, CanEdit: false));
        Dispatcher.UIThread.RunJobs();

        _AssertCollabBarDoesNotOverlap(body, window);

        window.Close();
    }

    [Fact]
    public void Wireframe_CoupledWithDeniedCapabilitiesAtDefaultWindowSize_DisconnectButtonAndChipsDoNotOverlap()
    {
        var registry = new FakeWireframeRegistry();
        var host = new ActivityStripTests.FakeHost(wireframe: registry);
        var document = WireframeDocument.New("Test wireframe");
        var body = new WireframeWorkspaceBody(host, document, null);
        var window = _Show(body, width: 960, height: 680);

        registry.SetCoupling(document.Id, new WireframeCoupling("a-very-long-session-name-that-runs-on", CanRead: false, CanEdit: false));
        Dispatcher.UIThread.RunJobs();

        _AssertCollabBarDoesNotOverlap(body, window);

        window.Close();
    }

    // The bar the workspace body keeps in a private `_couplingBar` field is the same one under test on every
    // surface — CouplingBarFactory.Build names it identically each time (AC-870).
    private static void _AssertCollabBarDoesNotOverlap(Control body, Window window)
    {
        var bar = (Border)body.GetType()
            .GetField("_couplingBar", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(body)!;

        // Buttons, plus the info group's own TextBlocks (title/label/chips) — not a button's internal AccessText,
        // which naturally sits inside its button's own bounds and is not a real overlap.
        var pieces = bar.GetVisualDescendants()
            .OfType<Control>()
            .Where(c => c.IsVisible)
            .Where(c => c is Button || (c is TextBlock && !c.GetVisualAncestors().OfType<Button>().Any()))
            .ToList();

        var rects = pieces.Select(piece => (Piece: piece, Rect: _RenderedRectInWindow(window, piece))).ToList();

        foreach (var (piece, rect) in rects)
        {
            Assert.True(
                rect.X >= 0 && rect.Y >= 0 && rect.Right <= window.Width && rect.Bottom <= window.Height,
                $"{piece.GetType().Name} is off-screen at {rect} (window is {window.Width}x{window.Height})");
        }

        for (var i = 0; i < rects.Count; i++)
        {
            for (var j = i + 1; j < rects.Count; j++)
            {
                var (pieceA, rectA) = rects[i];
                var (pieceB, rectB) = rects[j];
                Assert.False(
                    rectA.Intersects(rectB),
                    $"{pieceA.GetType().Name} and {pieceB.GetType().Name} overlap: {rectA} vs {rectB}");
            }
        }
    }

    private static Rect _RectInWindow(Window window, Control control)
    {
        var topLeft = control.TranslatePoint(new Point(0, 0), window)
            ?? throw new InvalidOperationException($"{control.GetType().Name} must be laid out to be checked");
        return new Rect(topLeft, control.Bounds.Size);
    }

    // What actually paints, not just what the control's own layout box claims: a control under a ClipToBounds
    // ancestor (the coupling bar's info group) is cut down to what that ancestor's own bounds allow, same as the
    // pixels the operator actually sees.
    private static Rect _RenderedRectInWindow(Window window, Control control)
    {
        var rect = _RectInWindow(window, control);
        foreach (var ancestor in control.GetVisualAncestors().OfType<Control>().TakeWhile(c => c != window))
        {
            if (ancestor.ClipToBounds)
            {
                rect = rect.Intersect(_RectInWindow(window, ancestor));
            }
        }

        return rect;
    }

    // The body only reaches its own visual tree inside a real, shown window — same reason
    // DiagramCollabWindowTests shows its content before walking GetVisualDescendants.
    private static Window _Show(Control content, double width, double height)
    {
        var window = new Window { Content = content, Width = width, Height = height };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    // Only what WireframeWorkspaceBody's coupling bar needs (SurfaceOpened/CouplingChanged/Disconnect) does
    // anything — everything else on the interface is a NotSupportedException stand-in, same shape as
    // WireframeDragTests.RecordingRegistry.
    private sealed class FakeWireframeRegistry : IWireframeAccessRegistry
    {
        public event Action<string, string>? TextChanged { add { } remove { } }

        public event Action<WireframeCouplingChange>? CouplingChanged;

        public event Action<string, string>? ComponentEdited { add { } remove { } }

        public event Action<string>? HistoryChanged { add { } remove { } }

        public void SetCoupling(string surfaceId, WireframeCoupling? coupling) =>
            CouplingChanged?.Invoke(new WireframeCouplingChange(surfaceId, coupling));

        public void SurfaceOpened(string surfaceId, string name, string initialText)
        {
        }

        public void SurfaceClosed(string surfaceId)
        {
        }

        public void Disconnect(string surfaceId)
        {
        }

        public void UpdateText(string surfaceId, string text)
        {
        }

        public string? PeekText(string surfaceId) => null;

        public IReadOnlyList<WireframeSurfaceView> ListSurfaces(string sessionId) => [];

        public WireframeSurface? Resolve(string surfaceRef) => null;

        public WireframeCoupling? CouplingOf(string sessionId, string surfaceId) => null;

        public bool IsCoupledByAnother(string sessionId, string surfaceId) => false;

        public void Couple(string sessionId, string surfaceId)
        {
        }

        public void Grant(string sessionId, string surfaceId, WireframeCapability capability)
        {
        }

        public string? ReadCoupled(string sessionId, string surfaceId) => null;

        public void MarkRead(string sessionId, string surfaceId)
        {
        }

        public WireframeEditResult WriteCoupled(string sessionId, string surfaceId, string text) => throw new NotSupportedException();

        public WireframeEditResult EditCoupled(string sessionId, string surfaceId, WireframeComponentEdit edit) => throw new NotSupportedException();

        public string? ApplyHandEdit(string surfaceId, WireframeComponentEdit edit) => throw new NotSupportedException();

        public void SessionEnded(string sessionId)
        {
        }

        public IReadOnlyList<WireframeHistoryEntry> History(string surfaceId) => [];

        public string? Revert(string surfaceId, string entryId) => null;

        public string? EnsureComponentId(string surfaceId, int line) => null;

        public void HoldComponent(string surfaceId, string componentId)
        {
        }

        public void ReleaseComponent(string surfaceId, string componentId)
        {
        }

        public bool IsHeldByOperator(string surfaceId, string componentId) => false;
    }
}
