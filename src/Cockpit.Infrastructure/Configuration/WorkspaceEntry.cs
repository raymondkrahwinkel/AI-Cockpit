using Cockpit.Core.Workspaces;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>On-disk shape of one <see cref="Workspace"/> in the <c>workspaces</c> section of <c>cockpit.json</c>.</summary>
internal sealed class WorkspaceEntry
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Type { get; set; } = nameof(WorkspaceType.Sessions);

    public DashboardLayoutEntry? Layout { get; set; }

    public List<WorkspacePaneEntry> Panes { get; set; } = [];

    public static WorkspaceEntry FromDomain(Workspace workspace) => new()
    {
        Id = workspace.Id,
        Name = workspace.Name,
        Type = workspace.Type.ToString(),
        // A Sessions workspace ignores the dashboard grid; writing it would put a setting in the file that
        // nothing reads and that an operator editing by hand would reasonably expect to do something.
        Layout = workspace.Type == WorkspaceType.Dashboard ? DashboardLayoutEntry.FromDomain(workspace.Layout) : null,
        Panes = [.. workspace.Panes.Select(WorkspacePaneEntry.FromDomain)],
    };

    /// <summary>
    /// This entry as a domain record. An unparseable type falls back to <see cref="WorkspaceType.Sessions"/>,
    /// and a pane the resulting type cannot hold is dropped rather than thrown on: a config that disagrees
    /// with itself (a widget in a Sessions workspace) is recoverable by ignoring the pane, but not by
    /// refusing to start.
    /// </summary>
    public Workspace ToDomain()
    {
        var type = Enum.TryParse<WorkspaceType>(Type, ignoreCase: true, out var parsed) ? parsed : WorkspaceType.Sessions;
        return new Workspace(Id, Name, type)
        {
            Layout = Layout?.ToDomain() ?? DashboardLayout.Default,
            Panes = [.. Panes.Select(pane => pane.ToDomain()).Where(pane => WorkspaceTypeRules.Accepts(type, pane.Kind))],
        };
    }
}
