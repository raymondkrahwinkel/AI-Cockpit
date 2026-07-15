using Avalonia;
using Avalonia.Controls;

namespace Cockpit.App.Controls;

/// <summary>
/// The dashboard's cells: a <see cref="Grid"/> that builds its own column and row definitions from
/// <see cref="Columns"/> and <see cref="Rows"/>.
/// </summary>
/// <remarks>
/// The definitions used to be built by the view, which had to find this panel first — and could not, until a
/// layout pass had created it. At startup the dashboard sits behind whichever workspace is active, so switching
/// to it was the first time the panel existed, and by then nothing was looking any more: the first widget spanned
/// the whole dashboard, because a Grid with no definitions is one big cell. Every event the view could have
/// hung on is too early (measured): a control that starts hidden never raises Loaded at all, and both
/// AttachedToVisualTree and TemplateApplied fire while ItemsPanelRoot is still null. Owning the definitions here
/// removes the question — the panel cannot be told its shape before it exists, and a binding delivers it the
/// moment it does.
/// </remarks>
public sealed class DashboardCellsPanel : Grid
{
    public static readonly StyledProperty<int> ColumnsProperty =
        AvaloniaProperty.Register<DashboardCellsPanel, int>(nameof(Columns));

    public static readonly StyledProperty<int> RowsProperty =
        AvaloniaProperty.Register<DashboardCellsPanel, int>(nameof(Rows));

    /// <summary>How many equal columns the dashboard is divided into. Zero while no dashboard is active.</summary>
    public int Columns
    {
        get => GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    /// <summary>How many equal rows to draw — the configured height, or more once the widgets have grown past it.</summary>
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
