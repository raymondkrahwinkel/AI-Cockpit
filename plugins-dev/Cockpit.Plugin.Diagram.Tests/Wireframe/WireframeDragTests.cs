using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.Core.Abstractions.Wireframe;
using Cockpit.Core.Wireframe;
using Cockpit.Plugin.Diagram.Wireframe;
using Cockpit.Plugin.Diagram.Wireframe.Rendering;

namespace Cockpit.Plugin.Diagram.Tests.Wireframe;

// AC-904 on the surface itself: the pointer gestures, driven through a real window. What is checked is the edit the
// surface hands the registry — the line surgery behind it has its own tests, and this is about which move the gesture
// decided on, and that it decides only once, on release.
[Collection("avalonia")]
public class WireframeDragTests
{
    private const string Source = """
        screen "Instellingen" #screen
          nav #nav
            item "Algemeen" #general
            item "Account" #account
          group "Profiel" #group
            label "Naam" #name
        """;

    [Theory]
    // Which half of the neighbour the pointer is in decides where the drop lands. Position counts the children as
    // they stand before the move, so past the neighbour below is index 2 and in front of it is index 0.
    [InlineData("general", "account", 0.8, "nav", 2)]
    [InlineData("account", "general", 0.2, "nav", 0)]
    // AC2/AC4: a drop in another container reparents it, and however far it travelled it is one edit — so one line in
    // the journal, and one thing to take back.
    [InlineData("general", "name", 0.8, "group", 1)]
    // A label is drawn as bare text with no background of its own, so hit-testing never reached it — which left a
    // whole class of components unselectable, and so undraggable. The surface answers "what is here" from where the
    // controls ended up instead.
    [InlineData("name", "account", 0.8, "nav", 2)]
    public void DraggingASelectedComponent_IsOneMove_ToWhereItWasDropped(
        string dragged, string target, double onto, string expectedParent, int expectedIndex)
    {
        var surface = _Open();

        surface.Select(dragged);
        surface.Drag(dragged, target, onto);

        Assert.Equal(
            WireframeComponentEdit.Move(dragged, expectedParent, expectedIndex),
            Assert.Single(surface.Registry.Applied));
        surface.Close();
    }

    [Theory]
    // AC7: only the component the operator selected drags. The very same gesture on the one beside it is still a pan.
    [InlineData("general", "account", "name")]
    // AC5: a container cannot go inside what it already holds. The cursor said so during the drag, so letting go is
    // silent — no edit, and no toast after the fact either.
    [InlineData("nav", "nav", "general")]
    // Letting go where it already sits is not a change, so it ends in silence rather than in the editor's refusal.
    [InlineData("general", "general", "general")]
    public void ADragThatDecidesOnNoMove_EndsInSilence(string selected, string dragged, string target)
    {
        var surface = _Open();

        surface.Select(selected);
        surface.Drag(dragged, target, onto: 0.8);

        Assert.Empty(surface.Registry.Applied);
        Assert.Empty(surface.Host.Toasts);
        surface.Close();
    }

    // AC3: the source is left alone for the whole gesture — the indicator lives on the draft layer, so an agent
    // reading halfway through sees the wireframe as it was.
    [Fact]
    public void WhileTheDragIsStillInFlight_NothingHasBeenAppliedYet()
    {
        var surface = _Open();
        surface.Select("general");

        surface.Window.MouseDown(surface.PointOn("general"), MouseButton.Left);
        surface.Window.MouseMove(surface.PointOn("name", 0.8));

        Assert.Empty(surface.Registry.Applied);
        surface.Close();
    }

    [Fact]
    public void EscapeDuringADrag_GivesUpTheGesture_AndChangesNothing()
    {
        var surface = _Open();
        surface.Select("general");

        surface.Window.MouseDown(surface.PointOn("general"), MouseButton.Left);
        surface.Window.MouseMove(surface.PointOn("name", 0.8));
        surface.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        surface.Window.MouseUp(surface.PointOn("name", 0.8), MouseButton.Left);

        Assert.Empty(surface.Registry.Applied);
        surface.Close();
    }

    // AC-924 criteria 1/3/4/14: a right-click opens the component's own menu on whatever it landed on, and by
    // itself applies nothing and arms no drag — Registry.Applied stays empty until a menu item is actually clicked.
    [Fact]
    public void RightClick_OpensTheMenuOnTheClickedComponent_AndAppliesNothingByItself()
    {
        var surface = _Open();

        var at = surface.PointOn("general");
        surface.Window.MouseDown(at, MouseButton.Right);
        surface.Window.MouseUp(at, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(surface.Registry.Applied);

        var delete = surface.MenuItem("Delete");
        delete.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        Assert.Equal(WireframeComponentEdit.Remove("general"), Assert.Single(surface.Registry.Applied));
        surface.Close();
    }

    // AC-924 criterion 14: the keyboard route (Menu key / Shift+F10) opens the same menu on whatever is already
    // selected — a parameterless ContextRequestedEventArgs carries no position, same convention the whiteboard's
    // own keyboard-route test uses.
    [Fact]
    public void ContextRequested_WithNoPosition_OpensOnTheCurrentSelection()
    {
        var surface = _Open();
        surface.Select("account");

        surface.Viewport.RaiseEvent(new ContextRequestedEventArgs());

        var delete = surface.MenuItem("Delete");
        delete.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        Assert.Equal(WireframeComponentEdit.Remove("account"), Assert.Single(surface.Registry.Applied));
        surface.Close();
    }

    private static SurfaceUnderTest _Open()
    {
        var registry = new RecordingRegistry(Source);
        var host = new ActivityStripTests.FakeHost(wireframe: registry);
        var body = new WireframeWorkspaceBody(host, new WireframeDocument("wireframe-1", "Instellingen", Source), sessionPaneId: null);

        // Big enough that the design canvas fits at close to true size, so every component is a comfortable target
        // and the slop a click is allowed cannot swallow a drag.
        var window = new Window { Content = body, Width = 1200, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return new SurfaceUnderTest(window, body, registry, host);
    }

    private sealed record SurfaceUnderTest(Window Window, Control Body, RecordingRegistry Registry, ActivityStripTests.FakeHost Host)
    {
        // AC-924: the one Focusable Border in the tree is the viewport _BuildViewport built — a right-click and
        // the keyboard route (Menu key / Shift+F10) both fire ContextRequested against it.
        public Border Viewport => Body.GetVisualDescendants().OfType<Border>().First(b => b.Focusable);

        public MenuItem MenuItem(string header) =>
            Viewport.ContextMenu!.Items.OfType<MenuItem>().First(item => (string)item.Header! == header);

        public void Select(string id)
        {
            var at = PointOn(id);
            Window.MouseDown(at, MouseButton.Left);
            Window.MouseUp(at, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }

        public void Drag(string from, string target, double onto)
        {
            var at = PointOn(target, onto);
            Window.MouseDown(PointOn(from), MouseButton.Left);
            Window.MouseMove(at);
            Window.MouseUp(at, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }

        // A point inside the control a component was drawn as, in the window's own coordinates. `share` is how far
        // down it sits, since which half of a neighbour the pointer is in decides where the drop lands.
        public Point PointOn(string id, double share = 0.5)
        {
            var control = Body.GetVisualDescendants()
                .OfType<Control>()
                .First(candidate => WireframeSource.GetNode(candidate)?.Id == id);
            return control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height * share), Window)!.Value;
        }

        public void Close() => Window.Close();
    }

    // Everything the wireframe surface asks of the registry, doing only what the surface needs to come up and stay
    // consistent — and remembering the hand-edits, which is what these tests are about. The source never changes, so
    // the controls a drag was aimed at stay valid for the length of the gesture.
    private sealed class RecordingRegistry(string text) : IWireframeAccessRegistry
    {
        public List<WireframeComponentEdit> Applied { get; } = [];

        public event Action<string, string>? TextChanged { add { } remove { } }

        public event Action<WireframeCouplingChange>? CouplingChanged { add { } remove { } }

        public event Action<string, string>? ComponentEdited { add { } remove { } }

        public event Action<string>? HistoryChanged { add { } remove { } }

        public string? ApplyHandEdit(string surfaceId, WireframeComponentEdit edit)
        {
            Applied.Add(edit);
            return null;
        }

        // The surface names a component by the id on its line, which this source already carries throughout.
        public string? EnsureComponentId(string surfaceId, int line) =>
            WireframeHandEdit.Find(WireframeParser.Parse(text).Screens, line)?.Id;

        public string? PeekText(string surfaceId) => text;

        public IReadOnlyList<WireframeHistoryEntry> History(string surfaceId) => [];

        public void SurfaceOpened(string surfaceId, string name, string initialText)
        {
        }

        public void SurfaceClosed(string surfaceId)
        {
        }

        public void HoldComponent(string surfaceId, string componentId)
        {
        }

        public void ReleaseComponent(string surfaceId, string componentId)
        {
        }

        public bool IsHeldByOperator(string surfaceId, string componentId) => false;

        public void Couple(string sessionId, string surfaceId)
        {
        }

        public void Disconnect(string surfaceId)
        {
        }

        public void UpdateText(string surfaceId, string text) => throw new NotSupportedException();

        public IReadOnlyList<WireframeSurfaceView> ListSurfaces(string sessionId) => throw new NotSupportedException();

        public WireframeSurface? Resolve(string surfaceRef) => throw new NotSupportedException();

        public WireframeCoupling? CouplingOf(string sessionId, string surfaceId) => null;

        public bool IsCoupledByAnother(string sessionId, string surfaceId) => false;

        public void Grant(string sessionId, string surfaceId, WireframeCapability capability) => throw new NotSupportedException();

        public string? ReadCoupled(string sessionId, string surfaceId) => throw new NotSupportedException();

        public void MarkRead(string sessionId, string surfaceId) => throw new NotSupportedException();

        public WireframeEditResult WriteCoupled(string sessionId, string surfaceId, string text) => throw new NotSupportedException();

        public WireframeEditResult EditCoupled(string sessionId, string surfaceId, WireframeComponentEdit edit) => throw new NotSupportedException();

        public void SessionEnded(string sessionId) => throw new NotSupportedException();

        public string? Revert(string surfaceId, string entryId) => throw new NotSupportedException();
    }
}
