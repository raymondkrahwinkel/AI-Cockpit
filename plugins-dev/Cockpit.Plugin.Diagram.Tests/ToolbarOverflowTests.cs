using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.Core.Abstractions.Diagrams;
using Cockpit.Infrastructure.Diagrams;
using Cockpit.Plugin.Diagram.Wireframe;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Diagram.Tests;

// AC-973: every toolbar button must stay on screen and unobscured, at each surface's own default window size
// (DiagramWindow.OpenAsync 900x640, WireframeWindow.OpenAsync 960x680) and at the wider sizes criterion 4 calls
// out (Attributes… visible, Overview + state strip visible). See the PR description for the audit's findings.
[Collection("avalonia")]
public class ToolbarOverflowTests
{
    [Theory]
    // Its own default window size (DiagramWindow.OpenAsync 900x640), then AC-973 criterion 4's wider one: wider than
    // the default is where the ticket's own screenshots caught the hint text painted over the zoom percentage — the
    // same reachability check, just at a size the toolbar has room to spread across.
    [InlineData(900)]
    [InlineData(1200)]
    public void Diagram_EveryVisibleToolbarButtonIsReachable(double width)
    {
        var body = new DiagramWorkspaceBody(new ActivityStripTests.FakeHost(), DiagramDocument.New("Test diagram"), null);
        var window = _Show(body, width, height: 640);

        _AssertAllReachable(window, body);

        window.Close();
    }

    [Fact]
    public void Diagram_ErDialectAtAWiderWindow_AttributesButtonIsVisibleAndReachable()
    {
        // AC-973 criterion 4's diagram case: an ER diagram shows Attributes… instead of Shape…. That needs a real
        // IDiagramAccessRegistry to detect the dialect — ActivityStripTests.FakeHost's fake registry always
        // reports Flowchart, so this test wires up the real one instead, same as DiagramMcpToolsTests does.
        var registry = new DiagramAccessRegistry();
        var document = DiagramDocument.New("Test ER diagram", ErSource);
        var body = new DiagramWorkspaceBody(new DiagramRegistryHost(registry), document, null);
        var window = _Show(body, width: 1200, height: 640);

        var attributes = body.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Attributes…"));
        var shape = body.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Shape…"));
        Assert.True(attributes.IsVisible);
        Assert.False(shape.IsVisible);

        _AssertAllReachable(window, body);

        window.Close();
    }

    [Theory]
    // Its own default window size (WireframeWindow.OpenAsync 960x680), then the wider one criterion 4 calls out.
    [InlineData(960)]
    [InlineData(1200)]
    public void Wireframe_EveryVisibleToolbarButtonIsReachable(double width)
    {
        var body = new WireframeWorkspaceBody(new ActivityStripTests.FakeHost(), WireframeDocument.New("Test wireframe"), null);
        var window = _Show(body, width, height: 680);

        _AssertAllReachable(window, body);

        window.Close();
    }

    [Fact]
    public void Wireframe_ZoomedWithOverviewAndStateStripAtAWiderWindow_EveryVisibleToolbarButtonIsReachable()
    {
        // AC-973 criterion 4's wireframe case: two screens (so "← Overview" shows) zoomed into the one that
        // carries a state (so the state strip shows too) — both extra groups the ticket calls out by name.
        var document = WireframeDocument.New("Test wireframe", TwoScreensOneWithState);
        var body = new WireframeWorkspaceBody(new ActivityStripTests.FakeHost(), document, null);
        var window = _Show(body, width: 1200, height: 680);
        _ZoomIntoFirstScreen(body);
        Dispatcher.UIThread.RunJobs();

        var overview = body.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "← Overview"));
        var stateStrip = (StackPanel)typeof(WireframeWorkspaceBody)
            .GetField("_stateStrip", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(body)!;
        Assert.True(overview.IsVisible);
        Assert.True(stateStrip.IsVisible);

        _AssertAllReachable(window, body);

        window.Close();
    }

    private const string ErSource = """
        erDiagram
            CUSTOMER ||--o{ ORDER : "places"
            CUSTOMER {
                string name
                int id PK
            }
            ORDER {
                int id PK
            }
        """;

    // Same shape as WireframeScreens.TwoScreens (Cockpit.Infrastructure.Tests) plus WireframeScreens.WithState
    // combined: two screens, the first carrying a state — nothing existing covers both at once.
    private const string TwoScreensOneWithState = """
        screen "Login" #login
          main w:4 #main
            list #results
              item "Result 1"

          state "Empty" replaces:#results #empty
            label "No results found" #empty-label

        screen "Signup" #signup
          input "Email" #signup-email
        """;

    // Zooms into the document's first screen via the same private path a double-click takes in the app — simpler
    // and less brittle here than simulating the exact gesture and its hit-test geometry.
    private static void _ZoomIntoFirstScreen(WireframeWorkspaceBody body)
    {
        var screens = (System.Collections.IList)typeof(WireframeWorkspaceBody)
            .GetField("_screens", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(body)!;
        var zoomInto = typeof(WireframeWorkspaceBody).GetMethod("_ZoomInto", BindingFlags.NonPublic | BindingFlags.Instance)!;
        zoomInto.Invoke(body, [screens[0]]);
    }

    // The body only reaches its own visual tree inside a real, shown window — same reason DiagramCollabWindowTests
    // shows its content before walking GetVisualDescendants.
    private static Window _Show(Control content, double width, double height)
    {
        var window = new Window { Content = content, Width = width, Height = height };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    // Every visible toolbar button must sit fully inside the window (nothing clipped or pushed off screen) and must
    // not overlap any other button (nothing painted over). ToggleButton derives from Button in Avalonia, so
    // OfType<Button>() already covers Follow/Notes too.
    private static void _AssertAllReachable(Window window, Control content)
    {
        var buttons = content.GetVisualDescendants().OfType<Button>().Where(b => b.IsVisible).ToList();
        var rects = buttons.Select(button => (Button: button, Rect: _RectInWindow(window, button))).ToList();

        foreach (var (button, rect) in rects)
        {
            Assert.True(
                rect.X >= 0 && rect.Y >= 0 && rect.Right <= window.Width && rect.Bottom <= window.Height,
                $"{_Describe(button)} is off-screen at {rect} (window is {window.Width}x{window.Height})");
        }

        for (var i = 0; i < rects.Count; i++)
        {
            for (var j = i + 1; j < rects.Count; j++)
            {
                var (buttonA, rectA) = rects[i];
                var (buttonB, rectB) = rects[j];
                Assert.False(
                    rectA.Intersects(rectB),
                    $"{_Describe(buttonA)} and {_Describe(buttonB)} overlap: {rectA} vs {rectB}");
            }
        }
    }

    private static Rect _RectInWindow(Window window, Control control)
    {
        var topLeft = control.TranslatePoint(new Point(0, 0), window)
            ?? throw new InvalidOperationException($"{_Describe(control)} must be laid out to be checked");
        return new Rect(topLeft, control.Bounds.Size);
    }

    private static string _Describe(Control control) =>
        $"{control.GetType().Name} \"{_ContentText(control)}\"";

    private static string _ContentText(Control control) => control switch
    {
        Button b => _ContentText(b.Content), // ToggleButton derives from Button in Avalonia — one arm covers both.
        _ => "",
    };

    private static string _ContentText(object? content) => content switch
    {
        string s => s,
        StackPanel panel => string.Join(" ", panel.Children.OfType<TextBlock>().Select(t => t.Text)),
        _ => content?.ToString() ?? "",
    };

    // ActivityStripTests.FakeHost's diagram registry parameter is the concrete fake type, which always reports
    // Flowchart — this one carries a real IDiagramAccessRegistry instead, so dialect detection is real too.
    // Internal, not private: AskRelationshipTests (AC-975) reuses this for the same ER-dialect setup.
    internal sealed class DiagramRegistryHost(IDiagramAccessRegistry registry) : ICockpitHost
    {
        public IServiceProvider Services { get; } = new _Services(registry);

        public ICockpitActions Actions => throw new NotSupportedException();

        public IPluginStorage Storage => throw new NotSupportedException();

        public void AddSettings(Func<Control> createView)
        {
        }

        public void AddSideMenuButton(string title, Action onInvoke)
        {
        }

        public void AddSideMenuSection(string title, Func<Control> createView)
        {
        }

        public Task ShowDialogAsync(string title, Func<Control> createContent, double width = 720, double height = 560) =>
            Task.CompletedTask;

        private sealed class _Services(IDiagramAccessRegistry registry) : IServiceProvider
        {
            public object? GetService(Type serviceType) => serviceType == typeof(IDiagramAccessRegistry) ? registry : null;
        }
    }
}
