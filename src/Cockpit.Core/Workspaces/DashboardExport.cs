namespace Cockpit.Core.Workspaces;

/// <summary>
/// A dashboard as a file: what you back up, and what you hand to someone else (Raymond, 2026-07-15). Carries
/// the arrangement and each widget's configuration — not the widgets themselves, which come from plugins the
/// receiver installs.
/// </summary>
/// <param name="FormatVersion">
/// Bumped when the shape changes in a way an older build cannot read. Present from the first version precisely
/// because it is worthless added later: a file with no version is one you have to guess at.
/// </param>
/// <param name="Name">The dashboard's name, offered on import — a shared file should arrive saying what it is.</param>
public sealed record DashboardExport(int FormatVersion, string Name, DashboardLayout Layout, IReadOnlyList<DashboardExportPane> Panes)
{
    public const int CurrentFormatVersion = 1;
}
