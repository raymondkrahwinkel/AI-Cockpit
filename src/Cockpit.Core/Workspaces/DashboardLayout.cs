namespace Cockpit.Core.Workspaces;

/// <summary>
/// A dashboard workspace's own grid settings (Raymond, 2026-07-15: "static grid (2x2, 3x2, …) … in te
/// stellen via de instellingen van het dashboard zelf"), reachable from the ⚙ on the workspace tab.
/// </summary>
/// <remarks>
/// "Static" describes the cell <em>topology</em>, not pixels: <see cref="Columns"/> fixes how many columns
/// widgets snap to, while the gutters between them stay draggable exactly as they are today — a 2x2 with a
/// wide left column is fine.
/// <para>
/// <see cref="Rows"/> is a starting height, not a cap: the grid grows rows as widgets are added (see
/// <see cref="DashboardGridMath.PlaceNext"/>). A hard cap would mean "Add widget" silently does nothing once
/// the last cell is taken, which is a dead end for the operator; columns stay fixed because that is the part
/// that carries the "2x2 / 3x2" shape Raymond asked for.
/// </para>
/// <para>
/// There is deliberately no layout <c>Mode</c> here yet. Masonry (the other mode Raymond floated) is a second
/// packing algorithm rather than a setting on this one, and it is an open decision — see §4g of
/// <c>Cockpit-Workspaces-Widgets-Terminals-Design-2026-07-15.md</c>. Adding a mode enum whose only other
/// value does nothing would put a dead option in the settings dialog.
/// </para>
/// </remarks>
public sealed record DashboardLayout
{
    /// <summary>How many columns widgets snap to. Clamped to <see cref="MinColumns"/>..<see cref="MaxColumns"/>.</summary>
    public int Columns { get; init; } = DefaultColumns;

    /// <summary>How many rows the dashboard starts with. Grows as needed; clamped to <see cref="MinRows"/>..<see cref="MaxRows"/>.</summary>
    public int Rows { get; init; } = DefaultRows;

    public const int DefaultColumns = 2;
    public const int DefaultRows = 2;
    public const int MinColumns = 1;
    public const int MaxColumns = 6;
    public const int MinRows = 1;
    public const int MaxRows = 12;

    /// <summary>The default 2x2 dashboard.</summary>
    public static DashboardLayout Default { get; } = new();

    /// <summary>
    /// This layout with both dimensions forced into their allowed range — applied on load and on save, so a
    /// hand-edited or older <c>cockpit.json</c> can never produce a zero-column grid that divides by zero.
    /// </summary>
    public DashboardLayout Clamped() => this with
    {
        Columns = Math.Clamp(Columns, MinColumns, MaxColumns),
        Rows = Math.Clamp(Rows, MinRows, MaxRows),
    };
}
