namespace Cockpit.Core.Workspaces;

// AC-1013: a dashboard as a file — arrangement and widget configs, not the widgets themselves (from plugins the
// receiver installs); `FormatVersion` exists from v1 because an unversioned file can't be told apart later, and
// `Name` is offered on import so a shared file arrives saying what it is. (Trimmed: full rationale on ticket.)
public sealed record DashboardExport(int FormatVersion, string Name, DashboardLayout Layout, IReadOnlyList<DashboardExportPane> Panes)
{
    public const int CurrentFormatVersion = 1;
}
