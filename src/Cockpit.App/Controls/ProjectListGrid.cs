using Avalonia;
using Avalonia.Controls;

namespace Cockpit.App.Controls;

// AC-1600: the Projects list's columns, shared by the column header and every row. No container queries in Avalonia,
// so the list host publishes its own width tier (`SizesColumns`, never the window's): wide ≥ 1050 gives the description
// its own column, 600–1050 puts it under the name, < 600 (the mockup's small tier) keeps logo, project and actions.
public sealed class ProjectListGrid : Grid
{
    public const double WideListWidth = 1050;

    public const double SmallListWidth = 600;

    public static readonly AttachedProperty<bool> IsWideProperty =
        AvaloniaProperty.RegisterAttached<ProjectListGrid, Control, bool>("IsWide", inherits: true);

    public static readonly AttachedProperty<bool> IsSmallProperty =
        AvaloniaProperty.RegisterAttached<ProjectListGrid, Control, bool>("IsSmall", inherits: true);

    // Set on the control whose width is the list's width; it then sets `IsWide` on itself as it is resized.
    public static readonly AttachedProperty<bool> SizesColumnsProperty =
        AvaloniaProperty.RegisterAttached<ProjectListGrid, Control, bool>("SizesColumns");

    // Which column a child of this grid is: logo, main, desc, prof, last, pl, stat or act.
    public static readonly AttachedProperty<string?> CellProperty =
        AvaloniaProperty.RegisterAttached<ProjectListGrid, Control, string?>("Cell");

    private static readonly string[] _wideCells = ["logo", "main", "desc", "prof", "last", "stat", "act"];
    private static readonly string[] _narrowCells = ["logo", "main", "pl", "stat", "act"];
    private static readonly string[] _smallCells = ["logo", "main", "act"];

    static ProjectListGrid()
    {
        SizesColumnsProperty.Changed.AddClassHandler<Control>((list, args) =>
        {
            if (args.NewValue is true)
            {
                list.SizeChanged += (_, sized) =>
                {
                    var width = TopLevel.GetTopLevel(list)?.Bounds.Width ?? sized.NewSize.Width;
                    SetIsWide(list, width >= WideListWidth);
                    SetIsSmall(list, width < SmallListWidth);
                };
            }
        });
    }

    public ProjectListGrid()
    {
        ColumnSpacing = 14;
        _Apply();
    }

    public static bool GetIsWide(Control control) => control.GetValue(IsWideProperty);

    public static void SetIsWide(Control control, bool value) => control.SetValue(IsWideProperty, value);

    public static bool GetIsSmall(Control control) => control.GetValue(IsSmallProperty);

    public static void SetIsSmall(Control control, bool value) => control.SetValue(IsSmallProperty, value);

    public static bool GetSizesColumns(Control control) => control.GetValue(SizesColumnsProperty);

    public static void SetSizesColumns(Control control, bool value) => control.SetValue(SizesColumnsProperty, value);

    public static string? GetCell(Control control) => control.GetValue(CellProperty);

    public static void SetCell(Control control, string? value) => control.SetValue(CellProperty, value);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsWideProperty || change.Property == IsSmallProperty)
        {
            _Apply();
        }
    }

    protected override void ChildrenChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        base.ChildrenChanged(sender, e);
        _Apply();
    }

    private void _Apply()
    {
        var wide = GetIsWide(this);
        var small = !wide && GetIsSmall(this);
        ColumnDefinitions = wide
            ? [new(28, GridUnitType.Pixel), new(1, GridUnitType.Star) { MinWidth = 180 }, new(1.5, GridUnitType.Star),
               new(130, GridUnitType.Pixel), new(100, GridUnitType.Pixel), new(200, GridUnitType.Pixel), _Actions()]
            : small
                ? [new(28, GridUnitType.Pixel), new(1, GridUnitType.Star), _Actions()]
                : [new(28, GridUnitType.Pixel), new(1, GridUnitType.Star), new(140, GridUnitType.Pixel),
                   new(170, GridUnitType.Pixel), _Actions()];

        var cells = wide ? _wideCells : small ? _smallCells : _narrowCells;
        foreach (var child in Children)
        {
            var column = Array.IndexOf(cells, GetCell(child));
            child.IsVisible = column >= 0;
            SetColumn(child, Math.Max(column, 0));
        }
    }

    // The mockup's 96 px, widened rather than clipped when a row's action needs more ("Pick how it runs"); shared
    // across the list (`Grid.IsSharedSizeScope` on its host), so every row's columns still line up.
    private static ColumnDefinition _Actions() => new(GridLength.Auto) { MinWidth = 96, SharedSizeGroup = "ProjectListActions" };
}
