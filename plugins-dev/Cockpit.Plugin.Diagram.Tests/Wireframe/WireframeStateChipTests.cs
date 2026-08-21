using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.Infrastructure.Wireframe;
using Cockpit.Plugin.Diagram.Wireframe;
using Cockpit.Plugin.Diagram.Wireframe.Rendering;

namespace Cockpit.Plugin.Diagram.Tests.Wireframe;

// AC-972: a state that has never been selected before carries no #id in the source yet (AC-906 mints lazily), and
// the bug this guards against — the first click doing nothing, only the second one opening the state — only shows
// up on exactly that state. A real WireframeAccessRegistry is used, not RecordingRegistry, since that fake's
// EnsureComponentId only ever looks an id up and never mints one — it would not reproduce the bug either way.
[Collection("avalonia")]
public class WireframeStateChipTests
{
    private const string Source = """
        screen "Inbox"
          list #list1
            item "Message from Alice"
            item "Message from Bob"
          state "Empty" replaces:#list1
            label "Nothing here yet"
        """;

    [Fact]
    public void ClickingAStateChipForTheFirstTime_OpensItImmediately()
    {
        var registry = new WireframeAccessRegistry();
        var host = new ActivityStripTests.FakeHost(wireframe: registry);
        var body = new WireframeWorkspaceBody(host, new WireframeDocument("wireframe-1", "Inbox", Source), sessionPaneId: null);

        var window = new Window { Content = body, Width = 1200, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var chip = body.GetVisualDescendants().OfType<ToggleButton>().First(candidate => (string?)candidate.Content == "Empty");
        var at = chip.TranslatePoint(new Point(chip.Bounds.Width / 2, chip.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        // AC1/AC3: one click, and the canvas already shows the state's own content instead of the base screen.
        Assert.Contains(
            body.GetVisualDescendants().OfType<Control>(),
            control => WireframeSource.GetNode(control)?.Text == "Nothing here yet");
        Assert.DoesNotContain(
            body.GetVisualDescendants().OfType<Control>(),
            control => WireframeSource.GetNode(control)?.Text == "Message from Alice");

        // AC2: the chip that lit up is the one the canvas agrees with — re-found, since _RefreshStateStrip rebuilds
        // the strip's controls on every refresh and the pre-click `chip` reference no longer lives in the tree.
        var reopenedChip = body.GetVisualDescendants().OfType<ToggleButton>().First(candidate => (string?)candidate.Content == "Empty");
        Assert.True(reopenedChip.IsChecked);

        window.Close();
    }
}
