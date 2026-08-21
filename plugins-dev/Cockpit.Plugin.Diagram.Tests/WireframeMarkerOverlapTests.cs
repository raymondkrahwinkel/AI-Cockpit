using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.Plugin.Diagram.Wireframe;

namespace Cockpit.Plugin.Diagram.Tests;

// AC-979: a note marker sits at a component's left-top corner (AC-907), and must never paint over that
// component's own text, which starts at the same x the marker anchors to. Uses the ticket's own repro source.
[Collection("avalonia")]
public class WireframeMarkerOverlapTests
{
    [Fact]
    public void NoteMarker_DoesNotOverlapItsComponentsText()
    {
        const string source = """
            screen "Log in"
              column align:center
                label "Welcome back"
                input "Email address" note:"Must accept a company e-mail only"
                input "Password" note:"Minimum 12 characters"
                button "Log in" primary note:"Disabled until both fields are filled"
            """;
        var body = new WireframeWorkspaceBody(
            new ActivityStripTests.FakeHost(), WireframeDocument.New("Test wireframe", source), null);
        var window = _Show(body, width: 960, height: 680);

        var expectedTips = new[]
        {
            "Must accept a company e-mail only", "Minimum 12 characters", "Disabled until both fields are filled",
        };
        var markers = body.GetVisualDescendants().OfType<Border>()
            .Where(b => ToolTip.GetTip(b) is string tip && expectedTips.Contains(tip))
            .ToList();
        Assert.Equal(expectedTips.Length, markers.Count);

        var labels = body.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Text is "Email address" or "Password")
            .ToList();
        Assert.Equal(2, labels.Count);

        foreach (var marker in markers)
        {
            var markerRect = _RectInWindow(window, marker);
            foreach (var label in labels)
            {
                var labelRect = _RectInWindow(window, label);
                var sameRow = markerRect.Y < labelRect.Bottom && labelRect.Y < markerRect.Bottom;
                if (sameRow)
                {
                    Assert.True(markerRect.Right <= labelRect.X,
                        $"Marker (tip \"{ToolTip.GetTip(marker)}\") at {markerRect} overlaps \"{label.Text}\" at {labelRect}");
                }
            }
        }

        window.Close();
    }

    private static Window _Show(Control content, double width, double height)
    {
        var window = new Window { Content = content, Width = width, Height = height };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    // Translates both corners rather than adding local Bounds.Size to a translated origin — the wireframe surface
    // carries a zoom RenderTransform, so a control's on-screen size is not its local, unscaled Bounds.Size.
    private static Rect _RectInWindow(Window window, Control control)
    {
        var topLeft = control.TranslatePoint(new Point(0, 0), window)
            ?? throw new InvalidOperationException($"{control.GetType().Name} must be laid out to be checked");
        var bottomRight = control.TranslatePoint(new Point(control.Bounds.Width, control.Bounds.Height), window)
            ?? throw new InvalidOperationException($"{control.GetType().Name} must be laid out to be checked");
        return new Rect(topLeft, bottomRight);
    }
}
