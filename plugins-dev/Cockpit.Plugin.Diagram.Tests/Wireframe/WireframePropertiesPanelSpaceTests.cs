using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.Core.Abstractions.Wireframe;
using Cockpit.Core.Wireframe;
using Cockpit.Plugin.Diagram.Wireframe;
using Cockpit.Plugin.Diagram.Wireframe.Rendering;

namespace Cockpit.Plugin.Diagram.Tests.Wireframe;

// AC-980: kept at a smaller, constant width instead of collapsing on selection like the notes panel above it —
// that panel can afford to disappear because it changes rarely, but this one would flicker the canvas on every
// single click.
[Collection("avalonia")]
public class WireframePropertiesPanelSpaceTests
{
    private const string Source = """
        screen "Instellingen" #screen
          label "Naam" #name
        """;

    [Fact]
    public void WithNoSelection_PropertiesPanelIsNarrowerThanTheOldFixed240px_GivingTheCanvasMoreRoom()
    {
        var surface = _Open();

        var panelWidth = _PanelBounds(surface).Width;

        Assert.True(panelWidth < 240,
            $"Properties panel is {panelWidth}px wide with nothing selected — no narrower than the old fixed 240px.");

        surface.Close();
    }

    [Fact]
    public void SelectingAndDeselectingAComponent_DoesNotChangeThePropertiesPanelWidth_SoTheCanvasNeverJumps()
    {
        var surface = _Open();
        var beforeWidth = _PanelBounds(surface).Width;
        var beforeViewportX = _ViewportBounds(surface).X;

        surface.Select("name");
        Dispatcher.UIThread.RunJobs();
        var selectedWidth = _PanelBounds(surface).Width;
        var selectedViewportX = _ViewportBounds(surface).X;

        surface.Deselect();
        Dispatcher.UIThread.RunJobs();
        var afterWidth = _PanelBounds(surface).Width;
        var afterViewportX = _ViewportBounds(surface).X;

        Assert.Equal(beforeWidth, selectedWidth);
        Assert.Equal(beforeWidth, afterWidth);
        Assert.Equal(beforeViewportX, selectedViewportX);
        Assert.Equal(beforeViewportX, afterViewportX);

        surface.Close();
    }

    [Fact]
    public void WithNoSelection_PanelShowsAHintInsteadOfBeingBlank()
    {
        var surface = _Open();

        var hint = surface.Body.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(t => t.Text == "Select a component to see its properties.");

        Assert.NotNull(hint);

        surface.Close();
    }

    private static Rect _PanelBounds(SurfaceUnderTest surface)
    {
        var panel = (Border)surface.Body.GetType()
            .GetField("_propertiesPanel", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(surface.Body)!;
        var topLeft = panel.TranslatePoint(new Point(0, 0), surface.Window)
            ?? throw new InvalidOperationException("Properties panel must be laid out to be checked.");
        return new Rect(topLeft, panel.Bounds.Size);
    }

    private static Rect _ViewportBounds(SurfaceUnderTest surface)
    {
        var viewport = surface.Body.GetVisualDescendants().OfType<Border>().First(b => b.Focusable);
        var topLeft = viewport.TranslatePoint(new Point(0, 0), surface.Window)
            ?? throw new InvalidOperationException("Viewport must be laid out to be checked.");
        return new Rect(topLeft, viewport.Bounds.Size);
    }

    private static SurfaceUnderTest _Open()
    {
        var registry = new RecordingRegistry(Source);
        var host = new ActivityStripTests.FakeHost(wireframe: registry);
        var body = new WireframeWorkspaceBody(host, new WireframeDocument("wireframe-1", "Instellingen", Source), sessionPaneId: null);

        var window = new Window { Content = body, Width = 960, Height = 680 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return new SurfaceUnderTest(window, body);
    }

    private sealed record SurfaceUnderTest(Window Window, Control Body)
    {
        public void Select(string id)
        {
            var control = Body.GetVisualDescendants()
                .OfType<Control>()
                .First(candidate => WireframeSource.GetNode(candidate)?.Id == id);
            var at = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), Window)!.Value;
            Window.MouseDown(at, MouseButton.Left);
            Window.MouseUp(at, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }

        // Clicking empty canvas space deselects — same as clicking outside every component.
        public void Deselect()
        {
            var at = new Point(Window.Width - 10, Window.Height - 10);
            Window.MouseDown(at, MouseButton.Left);
            Window.MouseUp(at, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }

        public void Close() => Window.Close();
    }

    // Only what WireframeWorkspaceBody's constructor and rendering need — same minimal shape as
    // WireframeDragTests.RecordingRegistry.
    private sealed class RecordingRegistry(string text) : IWireframeAccessRegistry
    {
        public event Action<string, string>? TextChanged { add { } remove { } }

        public event Action<WireframeCouplingChange>? CouplingChanged { add { } remove { } }

        public event Action<string, string>? ComponentEdited { add { } remove { } }

        public event Action<string>? HistoryChanged { add { } remove { } }

        public string? ApplyHandEdit(string surfaceId, WireframeComponentEdit edit) => null;

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

        public void SessionEnded(string sessionId)
        {
        }

        public string? Revert(string surfaceId, string entryId) => null;
    }
}
