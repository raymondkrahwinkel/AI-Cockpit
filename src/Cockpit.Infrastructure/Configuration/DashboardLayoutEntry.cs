using Cockpit.Core.Workspaces;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>On-disk shape of a dashboard's grid settings (<see cref="DashboardLayout"/>).</summary>
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

    /// <summary>Clamped on the way in, so a hand-edited zero-column grid cannot reach the view and divide by zero.</summary>
    public DashboardLayout ToDomain() =>
        new DashboardLayout { Columns = Columns, Rows = Rows, ShowGridLines = ShowGridLines }.Clamped();
}
