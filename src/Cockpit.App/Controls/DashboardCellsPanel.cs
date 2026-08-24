using Avalonia;
using Avalonia.Controls;

namespace Cockpit.App.Controls;

// AC-1013: The dashboard's cells: a `Grid` owning its own column/row definitions from `Columns`/`Rows`,
// not built by the view, because every candidate lifecycle event fires too early/not at all on a panel
// starting hidden behind an inactive workspace. Details: dropped the measured failure mode and per-event analysis.
public sealed class DashboardCellsPanel : Grid
{
    public static readonly StyledProperty<int> ColumnsProperty =
        AvaloniaProperty.Register<DashboardCellsPanel, int>(nameof(Columns));

    public static readonly StyledProperty<int> RowsProperty =
        AvaloniaProperty.Register<DashboardCellsPanel, int>(nameof(Rows));

    // How many equal columns the dashboard is divided into. Zero while no dashboard is active.
    public int Columns
    {
        get => GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    // How many equal rows to draw — the configured height, or more once the widgets have grown past it.
    public int Rows
    {
        get => GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ColumnsProperty || change.Property == RowsProperty)
        {
            _RebuildDefinitions();
        }
    }

    private void _RebuildDefinitions()
    {
        ColumnDefinitions.Clear();
        for (var column = 0; column < Columns; column++)
        {
            ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        }

        RowDefinitions.Clear();
        for (var row = 0; row < Rows; row++)
        {
            RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
        }
    }
}
