namespace Cockpit.Core.Workspaces;

// AC-1013: a dashboard's own grid settings (Raymond, 2026-07-15: static grid 2x2/3x2, set via the dashboard's
// own ⚙ settings). `Columns` fixes cell topology (gutters stay draggable); `Rows` is a starting height, not a
// cap. No `Mode` yet — Masonry is an open decision, §4g of the design doc. (Full rationale on ticket.)
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

    // AC-1013: defaults are fine (24x24, a snap resolution not a slot count) since a coarse default dictates
    // shape while a fine one doesn't. Maxima aren't an opinion on dashboard size (a 49" screen may want 48x24) —
    // they're a floor/ceiling against a zero-column divide-by-zero and a huge config typo hanging the app.
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
