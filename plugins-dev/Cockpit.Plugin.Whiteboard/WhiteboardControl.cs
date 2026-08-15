using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Cockpit.Plugin.Whiteboard.Canvas;
using Cockpit.Plugin.Whiteboard.Model;

namespace Cockpit.Plugin.Whiteboard;

// Toolbar + canvas (AC-821): Select and Pencil toggle each other off, and the shape button's flyout is the only
// way to place a template — the eight the ticket asks for, in the order it lists them.
public sealed class WhiteboardControl : UserControl
{
    private static readonly (PlacedShapeKind Kind, string Label)[] ShapeMenuEntries =
    [
        (PlacedShapeKind.Rectangle, "Vierkant"),
        (PlacedShapeKind.RoundedRectangle, "Afgerond"),
        (PlacedShapeKind.Ellipse, "Cirkel"),
        (PlacedShapeKind.Diamond, "Ruit"),
        (PlacedShapeKind.Arrow, "Pijl"),
        (PlacedShapeKind.Column, "Kolom"),
        (PlacedShapeKind.Callout, "Ballon"),
        (PlacedShapeKind.Text, "Tekst"),
    ];

    private readonly ToggleButton _selectButton;
    private readonly ToggleButton _pencilButton;

    public WhiteboardControl(WhiteboardDocument document)
    {
        Canvas = new WhiteboardCanvasControl(document);
        Canvas.ToolChanged += (_, _) => _SyncToolButtons();

        _selectButton = new ToggleButton { Content = "Select", IsChecked = true };
        _selectButton.Click += (_, _) => Canvas.UseSelectTool();

        _pencilButton = new ToggleButton { Content = "Pencil" };
        _pencilButton.Click += (_, _) => Canvas.UsePencilTool();

        var shapeButton = new Button { Content = "Shape", Flyout = _BuildShapeFlyout() };

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Children = { _selectButton, _pencilButton, shapeButton },
        };
        DockPanel.SetDock(toolbar, Dock.Top);

        Content = new DockPanel { Children = { toolbar, Canvas } };
    }

    public WhiteboardCanvasControl Canvas { get; }

    private MenuFlyout _BuildShapeFlyout()
    {
        var flyout = new MenuFlyout();
        foreach (var (kind, label) in ShapeMenuEntries)
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => Canvas.UseShapeTool(kind);
            flyout.Items.Add(item);
        }

        return flyout;
    }

    private void _SyncToolButtons()
    {
        _selectButton.IsChecked = Canvas.Tool == WhiteboardTool.Select;
        _pencilButton.IsChecked = Canvas.Tool == WhiteboardTool.Pencil;
    }
}
