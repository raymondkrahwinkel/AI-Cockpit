using Cockpit.Core.Workspaces;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>On-disk shape of one <see cref="Workspace"/> in the <c>workspaces</c> section of <c>cockpit.json</c>.</summary>
internal sealed class WorkspaceEntry
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Type { get; set; } = nameof(WorkspaceType.Sessions);

    public DashboardLayoutEntry? Layout { get; set; }

    /// <summary>Absent when this workspace follows Options — which is the default, so most workspaces carry neither.</summary>
    public bool? SingleSessionLayout { get; set; }

    public bool? StackSessionsVertically { get; set; }

    public List<WorkspacePaneEntry> Panes { get; set; } = [];

    public static WorkspaceEntry FromDomain(Workspace workspace) => new()
    {
        Id = workspace.Id,
        Name = workspace.Name,
        Type = workspace.Type.Id,
        // Each type writes only the settings it reads: a setting in the file that nothing acts on is one an
        // operator editing by hand would reasonably expect to do something.
        Layout = workspace.Type == WorkspaceType.Dashboard ? DashboardLayoutEntry.FromDomain(workspace.Layout) : null,
        SingleSessionLayout = workspace.Type == WorkspaceType.Sessions ? workspace.SingleSessionLayout : null,
        StackSessionsVertically = workspace.Type == WorkspaceType.Sessions ? workspace.StackSessionsVertically : null,
        Panes = [.. workspace.Panes.Select(WorkspacePaneEntry.FromDomain)],
    };

    /// <summary>
    /// This entry as a domain record. A blank type falls back to <see cref="WorkspaceType.Sessions"/>; a plugin
    /// type whose plugin is not installed keeps its id (so the workspace returns intact when the plugin does)
    /// rather than being rewritten to a host type — see <see cref="WorkspaceType.FromId"/>. A pane the resulting
    /// type cannot hold is dropped rather than thrown on: a config that disagrees with itself (a widget in a
    /// Sessions workspace, any grid pane in a plugin workspace) is recoverable by ignoring the pane, but not by
    /// refusing to start.
    /// </summary>
    public Workspace ToDomain()
    {
        var type = WorkspaceType.FromId(Type);
        return new Workspace(Id, Name, type)
        {
            Layout = Layout?.ToDomain() ?? DashboardLayout.Default,
            // Absent stays absent: null is "follow Options", which is what a workspace written before these
            // existed means, and what every workspace means until someone overrides it.
            SingleSessionLayout = type == WorkspaceType.Sessions ? SingleSessionLayout : null,
            StackSessionsVertically = type == WorkspaceType.Sessions ? StackSessionsVertically : null,
            Panes = [.. Panes.Select(pane => pane.ToDomain()).Where(pane => WorkspaceTypeRules.Accepts(type, pane.Kind))],
        };
    }
}
