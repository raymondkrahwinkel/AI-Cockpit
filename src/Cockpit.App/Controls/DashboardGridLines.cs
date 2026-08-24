using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Cockpit.App.Controls;

// AC-1013: Draws the cells a dashboard's widgets snap to as its own control, not Avalonia's fixed-look
// `Grid.ShowGridLines` debug aid, rendering directly rather than one `Line` per cell to avoid allocating
// 70-odd visuals on every resize of a 48x24 grid. Details: dropped the "faint enough not to read as content" note.
public sealed class DashboardGridLines : Control
{
    public static readonly StyledProperty<int> ColumnsProperty =
        AvaloniaProperty.Register<DashboardGridLines, int>(nameof(Columns));

    public static readonly StyledProperty<int> RowsProperty =
        AvaloniaProperty.Register<DashboardGridLines, int>(nameof(Rows));

    public static readonly StyledProperty<IBrush?> LineBrushProperty =
        AvaloniaProperty.Register<DashboardGridLines, IBrush?>(nameof(LineBrush));

    static DashboardGridLines()
    {
        // A changed column count or brush means different lines — without this the control keeps whatever it
        // drew the first time.
        AffectsRender<DashboardGridLines>(ColumnsProperty, RowsProperty, LineBrushProperty);
    }

    public int Columns
    {
        get => GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public int Rows
    {
        get => GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public IBrush? LineBrush
    {
        get => GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var (width, height) = (Bounds.Width, Bounds.Height);
        if (Columns <= 0 || Rows <= 0 || width <= 0 || height <= 0 || LineBrush is null)
        {
            return;
        }

        // Hairline: the grid is a guide for the eye, not a table.
        var pen = new Pen(LineBrush, 1);

        // The outer edges are the dashboard's own bounds and are already implied by the widgets sitting in it,
        // so only the divisions between cells are drawn.
        for (var column = 1; column < Columns; column++)
        {
            var x = Math.Round(column * (width / Columns)) + 0.5;
            context.DrawLine(pen, new Point(x, 0), new Point(x, height));
        }

        for (var row = 1; row < Rows; row++)
        {
            var y = Math.Round(row * (height / Rows)) + 0.5;
            context.DrawLine(pen, new Point(0, y), new Point(width, y));
        }
    }
}
