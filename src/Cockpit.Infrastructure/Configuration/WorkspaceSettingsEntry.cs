using Cockpit.Core.Workspaces;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>
/// On-disk shape of <see cref="WorkspaceSettings"/> in the <c>workspaces</c> section of <c>cockpit.json</c>.
/// Carries only what the host itself needs to rebuild a workspace: which widget type, which instance, and
/// where it sits. A widget's own configuration is deliberately absent — it lives in the plugin's per-instance
/// storage keyed by the pane id, so the host never has to know the shape of a plugin's config and this file
/// never grows plugin blobs.
/// </summary>
internal sealed class WorkspaceSettingsEntry
{
    public List<WorkspaceEntry> Workspaces { get; set; } = [];

    public string? ActiveWorkspaceId { get; set; }

    public static WorkspaceSettingsEntry FromDomain(WorkspaceSettings settings) => new()
    {
        Workspaces = [.. settings.Workspaces.Select(WorkspaceEntry.FromDomain)],
        ActiveWorkspaceId = settings.ActiveWorkspaceId,
    };

    /// <summary>
    /// The saved workspaces as domain records, normalized so the result is always bindable — a config written
    /// by a newer build, or hand-edited, should cost the operator fidelity rather than the whole cockpit.
    /// </summary>
    public WorkspaceSettings ToDomain() => new WorkspaceSettings
    {
        Workspaces = [.. Workspaces.Select(entry => entry.ToDomain())],
        ActiveWorkspaceId = ActiveWorkspaceId,
    }.Normalized();
}
