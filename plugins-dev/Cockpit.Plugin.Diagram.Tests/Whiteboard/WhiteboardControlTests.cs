using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Cockpit.Plugin.Diagram.Whiteboard;
using Cockpit.Plugin.Diagram.Whiteboard.Model;
using Cockpit.Plugin.Diagram.Whiteboard.Rendering;

namespace Cockpit.Plugin.Diagram.Tests.Whiteboard;

// AC-982: the colour flyout's own default swatch — the way back to a swatch pick that AC-916's flyout never had.
[Collection("avalonia")]
public class WhiteboardControlTests
{
    [Fact]
    public void ColourFlyout_DefaultSwatch_ResetsASelectedShapeAndThePendingColour()
    {
        var document = new WhiteboardDocument();
        var placed = new PlacedObject { ShapeKind = PlacedShapeKind.Rectangle, X = 10, Y = 10, Width = 30, Height = 30 };
        document.Add(placed);
        var control = new WhiteboardControl(document);
        var window = _Show(control);

        control.Canvas.SelectObject(placed.Id);
        var swatches = _OpenColourFlyoutSwatches(control);

        // A palette swatch first, so there is something to reset away from.
        swatches.First(s => (string?)s.Tag == "#DC2626").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("#DC2626", placed.Color);

        swatches.Single(s => s.Tag is null).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Null(placed.Color);
        Assert.Null(control.Canvas.PendingColor);

        window.Close();
    }

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
        Assert.DoesNotContain(WhiteboardObjectPainter.Palette, hex => string.Equals(hex, "#2563EB", StringComparison.OrdinalIgnoreCase));
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
