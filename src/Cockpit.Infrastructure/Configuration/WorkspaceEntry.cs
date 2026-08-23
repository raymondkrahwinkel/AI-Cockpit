using Cockpit.Core.Workspaces;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of one `Workspace` in the `workspaces` section of `cockpit.json`.
internal sealed class WorkspaceEntry
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Type { get; set; } = nameof(WorkspaceType.Sessions);

    public DashboardLayoutEntry? Layout { get; set; }

    // Absent when this workspace follows Options — which is the default, so most workspaces carry neither.
    public bool? SingleSessionLayout { get; set; }

    public bool? StackSessionsVertically { get; set; }

    public bool? FocusRailLayout { get; set; }

    public double? FocusRailWeight { get; set; }

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
        FocusRailLayout = workspace.Type == WorkspaceType.Sessions ? workspace.FocusRailLayout : null,
        FocusRailWeight = workspace.Type == WorkspaceType.Sessions ? workspace.FocusRailWeight : null,
        Panes = [.. workspace.Panes.Select(WorkspacePaneEntry.FromDomain)],
    };

    // This entry as a domain record. A blank type falls back to `Sessions`; an uninstalled plugin type
    // keeps its id (see `WorkspaceType.FromId`) rather than being rewritten. A pane the resulting type
    // cannot hold is dropped, not thrown on — a self-contradicting config is recoverable by ignoring it.
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
            FocusRailLayout = type == WorkspaceType.Sessions ? FocusRailLayout : null,
            FocusRailWeight = type == WorkspaceType.Sessions ? FocusRailWeight : null,
            Panes = [.. Panes.Select(pane => pane.ToDomain()).Where(pane => WorkspaceTypeRules.Accepts(type, pane.Kind))],
        };
    }
}
