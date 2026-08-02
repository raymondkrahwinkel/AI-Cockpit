namespace Cockpit.Core.Workspaces;

// A dashboard workspace's own grid settings (Raymond, 2026-07-15: "static grid (2x2, 3x2, …) … in te
// stellen via de instellingen van het dashboard zelf"), reachable from the ⚙ on the workspace tab.
// "Static" describes the cell *topology*, not pixels: `Columns` fixes how many columns
// widgets snap to, while the gutters between them stay draggable exactly as they are today — a 2x2 with a
// wide left column is fine.
//
// `Rows` is a starting height, not a cap: the grid grows rows as widgets are added (see
// `DashboardGridMath.PlaceNext`). A hard cap would mean "Add widget" silently does nothing once
// the last cell is taken, which is a dead end for the operator; columns stay fixed because that is the part
// that carries the "2x2 / 3x2" shape Raymond asked for.
//
// There is deliberately no layout `Mode` here yet. Masonry (the other mode Raymond floated) is a second
// packing algorithm rather than a setting on this one, and it is an open decision — see §4g of
// `Cockpit-Workspaces-Widgets-Terminals-Design-2026-07-15.md`. Adding a mode enum whose only other
// value does nothing would put a dead option in the settings dialog.
public sealed record DashboardLayout
{
    // How many columns widgets snap to. Clamped to `MinColumns`..`MaxColumns`.
    public int Columns { get; init; } = DefaultColumns;

    // How many rows the dashboard starts with. Grows as needed; clamped to `MinRows`..`MaxRows`.
    public int Rows { get; init; } = DefaultRows;

    // Draws the cells the widgets snap to. Off by default — a dashboard is something you look at, not a
    // worksheet — but a grid you cannot see is one you are placing on blind, so it is a toggle rather than a
    // debug build's secret. Per dashboard, since it answers a question about this dashboard's shape.
    public bool ShowGridLines { get; init; }

    // The defaults are a canvas, not a shape: a widget is placed and sized freely on it, so the grid is the
    // resolution you snap to rather than a slot count — so it defaults fine (24×24) rather than coarse. A
    // coarse default is the one that needs undoing: it decides the shape of everything placed on it, while a
    // fine one simply lets a widget be the size it wants.
    //
    // The maxima are not a design opinion about how big a dashboard should be — a 49" screen wants something
    // like 48×24, and that is the operator's call. They are a floor and ceiling on what can reach the view: a
    // zero-column grid divides by zero, and a config typo of 100000 would have the grid build a hundred
    // thousand definitions and hang the app. 256 is far past any real screen while still bounding that.
    public const int DefaultColumns = 24;
    public const int DefaultRows = 24;
    public const int MinColumns = 1;
    public const int MaxColumns = 256;
    public const int MinRows = 1;
    public const int MaxRows = 256;

    // The default 2x2 dashboard.
    public static DashboardLayout Default { get; } = new();

    // This layout with both dimensions forced into their allowed range — applied on load and on save, so a
    // hand-edited or older `cockpit.json` can never produce a zero-column grid that divides by zero.
    public DashboardLayout Clamped() => this with
    {
        Columns = Math.Clamp(Columns, MinColumns, MaxColumns),
        Rows = Math.Clamp(Rows, MinRows, MaxRows),
    };
}
