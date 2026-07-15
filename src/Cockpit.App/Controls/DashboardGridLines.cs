using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Cockpit.App.Controls;

/// <summary>
/// Draws the cells a dashboard's widgets snap to. Its own control rather than Avalonia's
/// <c>Grid.ShowGridLines</c>: that one is a debug aid with a fixed look, and this has to sit quietly under real
/// widgets — visible enough to place against, faint enough not to read as content.
/// </summary>
/// <remarks>
/// Renders directly instead of building a Line per cell: a 48×24 grid is 70-odd lines, and a control that
/// redraws them on every resize should not also be allocating seventy visuals to do it.
/// </remarks>
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
