using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.Plugin.Diagram.Whiteboard;
using Cockpit.Plugin.Diagram.Whiteboard.Model;
using Cockpit.Plugin.Diagram.Whiteboard.Rendering;

namespace Cockpit.Plugin.Diagram.Tests.Whiteboard;

// AC-982: the colour flyout's own default swatch — the way back to a swatch pick that AC-916's flyout never had.
[Collection("avalonia")]
public class WhiteboardControlTests
{
    // AC-982's hard boundary from AC-916: the operator's own default is not the colour reserved for the agent's
    // consent promise. The reset swatch must reach SetColor(null) — never the literal reserved hex — and that
    // hex must never appear anywhere in the flyout's swatch row.
    [Fact]
    public void ColourFlyout_NeverExposesTheAgentsReservedConsentColour()
    {
        var document = new WhiteboardDocument();
        var control = new WhiteboardControl(document);
        _Show(control);

        var swatches = _OpenColourFlyoutSwatches(control);

        Assert.All(swatches, swatch => Assert.NotEqual("#2563EB", swatch.Tag as string, StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(WhiteboardPalette.Swatches, hex => string.Equals(hex, "#2563EB", StringComparison.OrdinalIgnoreCase));
    }

    private static List<Button> _OpenColourFlyoutSwatches(WhiteboardControl control)
    {
        var colourButton = control.GetVisualDescendants().OfType<Button>()
            .Single(button => ToolTip.GetTip(button) as string == "Colour");
        var flyout = Assert.IsType<Flyout>(colourButton.Flyout);
        flyout.ShowAt(colourButton);
        var row = Assert.IsType<StackPanel>(flyout.Content);
        return [.. row.Children.OfType<Button>()];
    }

    private static Window _Show(Control content)
    {
        var window = new Window { Width = 300, Height = 300, Content = content };
        window.Show();
        return window;
    }
}
