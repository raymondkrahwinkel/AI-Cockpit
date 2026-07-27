using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Workspaces;

namespace Cockpit.App.Services;

/// <summary>
/// Host-side <see cref="IWorkspaceAgentGateway"/> (AC-391) over the running session panels: there is no existing
/// "find the workspace for this pane id" helper, so this is it — the same seam <see cref="SessionVerifyGateway"/>
/// (AC-86) is for a session's working directory. It resolves the caller's pane in <see cref="CockpitViewModel.Sessions"/>,
/// reads which workspace it is stamped with, and reports every other AI-session pane sharing that same workspace.
/// <para>
/// A Sessions workspace does not keep its AI panes in <see cref="Workspace.Panes"/> the way a Dashboard keeps its
/// widgets — a session is placed by <see cref="SessionPanelViewModel.WorkspaceId"/> instead, arranged automatically
/// rather than at an explicit cell. So membership here is read the same way <c>CockpitViewModel</c> itself decides
/// which sessions belong to the active workspace: an empty <c>WorkspaceId</c> (a session started before workspaces
/// existed, or in the design-time graph) falls back to the first Sessions workspace, rather than being read as
/// belonging to none.
/// </para>
/// </summary>
internal sealed class WorkspaceAgentGateway(CockpitViewModel cockpit) : IWorkspaceAgentGateway, ISingletonService
{
    public WorkspaceAgentSnapshot? GetWorkspaceSnapshot(string paneId)
    {
        if (_Find(paneId) is not { } caller)
        {
            return null;
        }

        var workspaceId = _ResolveWorkspaceId(caller);
        var panes = cockpit.Sessions
            // Only real agent sessions: a plain terminal pane (ShowPluginHeaderItems=false) cannot itself call an
            // MCP tool, and has nothing to report to one either, so it never appears as a sibling here.
            .Where(candidate => candidate.ShowPluginHeaderItems && _ResolveWorkspaceId(candidate) == workspaceId)
            .Select(candidate => new WorkspaceAgentPane(candidate.PaneId, candidate.Title, candidate.ActiveProfileLabel, candidate.Statusline))
            .ToList();

        return new WorkspaceAgentSnapshot(workspaceId, panes);
    }

    private SessionPanelViewModel? _Find(string paneId) =>
        cockpit.Sessions.FirstOrDefault(session => string.Equals(session.PaneId, paneId, StringComparison.Ordinal));

    // Mirrors CockpitViewModel's own BelongsToActiveWorkspace fallback: an unassigned session (empty WorkspaceId)
    // belongs to the first Sessions workspace, not to none — the one place that rule is allowed to drift from would
    // be a session that is on-screen on one desk but invisible to this gateway's notion of the same desk.
    private string _ResolveWorkspaceId(SessionPanelViewModel session) =>
        session.WorkspaceId.Length == 0
            ? cockpit.Workspaces.Settings.Workspaces.FirstOrDefault(workspace => workspace.Type == WorkspaceType.Sessions)?.Id ?? session.WorkspaceId
            : session.WorkspaceId;
}
