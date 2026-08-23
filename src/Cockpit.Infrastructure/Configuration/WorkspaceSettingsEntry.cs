using Cockpit.Core.Workspaces;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of `WorkspaceSettings`. Carries only what the host needs to rebuild a workspace — widget
// type, instance, position. A widget's own config lives in the plugin's per-instance storage instead.
internal sealed class WorkspaceSettingsEntry
{
    public List<WorkspaceEntry> Workspaces { get; set; } = [];

    public string? ActiveWorkspaceId { get; set; }

    public static WorkspaceSettingsEntry FromDomain(WorkspaceSettings settings) => new()
    {
        Workspaces = [.. settings.Workspaces.Select(WorkspaceEntry.FromDomain)],
        ActiveWorkspaceId = settings.ActiveWorkspaceId,
    };

    // The saved workspaces as domain records, normalized so the result is always bindable — a config written
    // by a newer build, or hand-edited, should cost the operator fidelity rather than the whole cockpit.
    public WorkspaceSettings ToDomain() => new WorkspaceSettings
    {
        Workspaces = [.. Workspaces.Select(entry => entry.ToDomain())],
        ActiveWorkspaceId = ActiveWorkspaceId,
    }.Normalized();
}
