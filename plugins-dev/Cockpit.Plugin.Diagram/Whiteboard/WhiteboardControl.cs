using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugin.Diagram.Whiteboard.Canvas;
using Cockpit.Plugin.Diagram.Whiteboard.Model;
using Cockpit.Plugin.Diagram.Whiteboard.Rendering;
using Material.Icons;
using Material.Icons.Avalonia;

namespace Cockpit.Plugin.Diagram.Whiteboard;

// Toolbar + canvas (AC-844): mockup #W1's row — select, pencil, marker, shape templates, sticky note, image,
// screenshot paste — each with a MaterialIcon rather than the bare-text buttons AC-821 shipped with.
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
    private readonly ToggleButton _markerButton;

    public WhiteboardControl(WhiteboardDocument document)
    {
        Canvas = new WhiteboardCanvasControl(document);
        Canvas.ToolChanged += (_, _) => _SyncToolButtons();

        _selectButton = _ToggleIconButton(MaterialIconKind.CursorDefaultOutline, "Selecteren", Canvas.UseSelectTool);
        _selectButton.IsChecked = true;

        _pencilButton = _ToggleIconButton(MaterialIconKind.Pencil, "Potlood", Canvas.UsePencilTool);
        _markerButton = _ToggleIconButton(MaterialIconKind.Highlighter, "Marker", Canvas.UseMarkerTool);

        var shapeButton = _IconButton(MaterialIconKind.ShapeOutline, "Vormsjablonen", () => { });
        shapeButton.Flyout = _BuildShapeFlyout();

        var stickyButton = _IconButton(MaterialIconKind.StickyNoteOutline, "Sticky note", () => Canvas.UseShapeTool(PlacedShapeKind.StickyNote));
        var imageButton = _IconButton(MaterialIconKind.ImagePlusOutline, "Afbeelding invoegen", () => _ = Canvas.InsertImageAsync());
        var pasteButton = _IconButton(MaterialIconKind.ContentPaste, "Screenshot plakken", () => _ = Canvas.PasteScreenshotAsync());

        // AC-913: same zoom/Fit shape as the diagram and wireframe toolbars — the wheel and the middle-button drag
        // do the same job, this is just where the current level is always visible.
        var zoomOut = new Button { Content = "−", Classes = { "Compact" }, MinWidth = 28 };
        zoomOut.Click += (_, _) => Canvas.ZoomOut();
        var zoomLabel = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 40,
            TextAlignment = TextAlignment.Center,
            FontSize = 12,
            Text = $"{Canvas.Zoom * 100:0}%",
        };
        Canvas.ZoomChanged += (_, _) => zoomLabel.Text = $"{Canvas.Zoom * 100:0}%";
        var zoomIn = new Button { Content = "+", Classes = { "Compact" }, MinWidth = 28 };
        zoomIn.Click += (_, _) => Canvas.ZoomIn();
        var fitButton = new Button { Content = "Fit", Classes = { "Compact" } };
        fitButton.Click += (_, _) => Canvas.ApplyFit();
        ToolTip.SetTip(zoomOut, "Uitzoomen");
        ToolTip.SetTip(zoomIn, "Inzoomen");
        ToolTip.SetTip(fitButton, "Alles in beeld — middelste knop sleept, wiel zoomt.");

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Children = { _selectButton, _pencilButton, _markerButton, shapeButton, stickyButton, imageButton, pasteButton, zoomOut, zoomLabel, zoomIn, fitButton },
        };
        DockPanel.SetDock(toolbar, Dock.Top);

        Content = new DockPanel { Children = { toolbar, Canvas } };
    }

    public WhiteboardCanvasControl Canvas { get; }

    private Flyout _BuildShapeFlyout()
    {
        var flyout = new Flyout();
        var grid = new WrapPanel { MaxWidth = 200 };
        foreach (var (kind, label) in ShapeMenuEntries)
        {
            grid.Children.Add(_ShapeEntryButton(flyout, kind, label));
        }

        flyout.Content = new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(4),
            Children =
            {
                new TextBlock { Text = "Neerzetten, niet tekenen", FontStyle = FontStyle.Italic, FontSize = 11, Opacity = 0.7 },
                grid,
            },
        };

        return flyout;
    }

    private Button _ShapeEntryButton(Flyout flyout, PlacedShapeKind kind, string label)
    {
        var button = new Button
        {
            Content = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new ShapePreview { Kind = kind, Width = 44, Height = 30 },
                    new TextBlock { Text = label, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center },
                },
            },
        };
        button.Click += (_, _) =>
        {
            Canvas.UseShapeTool(kind);
            flyout.Hide();
        };

        return button;
    }

    private static Button _IconButton(MaterialIconKind kind, string tip, Action onClick)
    {
        var button = new Button { Content = new MaterialIcon { Kind = kind, Width = 16, Height = 16 }, Width = 32 };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => onClick();
        return button;
    }

    private static ToggleButton _ToggleIconButton(MaterialIconKind kind, string tip, Action onClick)
    {
        var button = new ToggleButton { Content = new MaterialIcon { Kind = kind, Width = 16, Height = 16 }, Width = 32 };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => onClick();
        return button;
    }

    private void _SyncToolButtons()
    {
        _selectButton.IsChecked = Canvas.Tool == WhiteboardTool.Select;
        _pencilButton.IsChecked = Canvas.Tool == WhiteboardTool.Pencil;
        _markerButton.IsChecked = Canvas.Tool == WhiteboardTool.Marker;
    }

    // A miniature of the shape itself rather than a generic icon — the grid (#W2) is meant to be recognised, not read.
    private sealed class ShapePreview : Control
    {
        public required PlacedShapeKind Kind { get; init; }

        public override void Render(DrawingContext context) =>
            WhiteboardObjectPainter.PaintPlaced(context, Kind, new Rect(Bounds.Size), null, null);
    }
}
