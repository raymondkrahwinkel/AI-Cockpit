using Cockpit.Core.Workspaces;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of a dashboard's grid settings (`DashboardLayout`).
internal sealed class DashboardLayoutEntry
{
    public int Columns { get; set; } = DashboardLayout.DefaultColumns;

    public int Rows { get; set; } = DashboardLayout.DefaultRows;

    public bool ShowGridLines { get; set; }

    public static DashboardLayoutEntry FromDomain(DashboardLayout layout) => new()
    {
        Columns = layout.Columns,
        Rows = layout.Rows,
        ShowGridLines = layout.ShowGridLines,
    };

    // Clamped on the way in, so a hand-edited zero-column grid cannot reach the view and divide by zero.
    public DashboardLayout ToDomain() =>
        new DashboardLayout { Columns = Columns, Rows = Rows, ShowGridLines = ShowGridLines }.Clamped();
}
