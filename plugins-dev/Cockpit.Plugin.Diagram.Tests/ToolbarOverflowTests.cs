using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.Plugin.Diagram.Wireframe;

namespace Cockpit.Plugin.Diagram.Tests;

// AC-973: at the window size each surface itself opens with, every toolbar button must be on screen and none of them
// may cover another. DiagramWindow.OpenAsync opens at 900x640, WireframeWindow.OpenAsync at 960x680; the audit that
// filed this ticket found Export (diagram) and the whole zoom group (wireframe) silently unreachable at exactly
// those sizes, with no wrap, overflow menu or truncation mark — a button just stopped existing on screen.
[Collection("avalonia")]
public class ToolbarOverflowTests
{
    [Fact]
    public void Diagram_AtItsOwnDefaultWindowSize_EveryVisibleToolbarButtonIsReachable()
    {
        var body = new DiagramWorkspaceBody(new ActivityStripTests.FakeHost(), DiagramDocument.New("Test diagram"), null);
        var window = _Show(body, width: 900, height: 640);

        _AssertAllReachable(window, body);

        window.Close();
    }

    [Fact]
    public void Diagram_AtAWiderWindow_EveryVisibleToolbarButtonIsStillReachable()
    {
        // AC-973 criterion 4: wider than the default is where the ticket's own screenshots caught the hint text
        // painted over the zoom percentage — the same reachability check, just at a size the toolbar has room to
        // spread across.
        var body = new DiagramWorkspaceBody(new ActivityStripTests.FakeHost(), DiagramDocument.New("Test diagram"), null);
        var window = _Show(body, width: 1200, height: 640);

        _AssertAllReachable(window, body);

        window.Close();
    }

    [Fact]
    public void Wireframe_AtItsOwnDefaultWindowSize_EveryVisibleToolbarButtonIsReachable()
    {
        var body = new WireframeWorkspaceBody(new ActivityStripTests.FakeHost(), WireframeDocument.New("Test wireframe"), null);
        var window = _Show(body, width: 960, height: 680);

        _AssertAllReachable(window, body);

        window.Close();
    }

    [Fact]
    public void Wireframe_AtAWiderWindow_EveryVisibleToolbarButtonIsStillReachable()
    {
        var body = new WireframeWorkspaceBody(new ActivityStripTests.FakeHost(), WireframeDocument.New("Test wireframe"), null);
        var window = _Show(body, width: 1200, height: 680);

        _AssertAllReachable(window, body);

        window.Close();
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
}
