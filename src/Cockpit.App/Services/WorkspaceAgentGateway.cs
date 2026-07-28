using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Workspaces;

namespace Cockpit.App.Services;

/// <summary>
/// Host-side <see cref="IWorkspaceAgentGateway"/> (AC-391) over the running session panels: there is no existing
/// "find the workspace for this pane id" helper, so this is it — the same seam <see cref="SessionVerifyGateway"/>
/// (AC-86) is for a session's working directory. It resolves the caller's pane via <see cref="CockpitViewModel.FindSession"/>,
/// reads which workspace it is stamped with, and reports every other AI-session pane sharing that same workspace —
/// including an embedded one (<see cref="CockpitViewModel.Embed"/>), which is a full agent session with its own MCP
/// token even though the grid never lists it.
/// <para>
/// A Sessions workspace does not keep its AI panes in <see cref="Workspace.Panes"/> the way a Dashboard keeps its
/// widgets — a session is placed by <see cref="SessionPanelViewModel.WorkspaceId"/> instead, arranged automatically
/// rather than at an explicit cell. So membership here is read the same way <c>CockpitViewModel</c> itself decides
/// which sessions belong to the active workspace: an empty <c>WorkspaceId</c> (a session started before workspaces
/// existed, or in the design-time graph) falls back to the first Sessions workspace, rather than being read as
/// belonging to none — but when there is no Sessions workspace to fall back to either, the pane resolves to no
/// workspace at all, and is refused rather than handed an invented empty one.
/// </para>
/// <para>
/// <see cref="CockpitViewModel.Sessions"/> is an <see cref="System.Collections.ObjectModel.ObservableCollection{T}"/>
/// that only ever mutates on the UI thread, but an MCP tool call lands on the endpoint's own request thread — the
/// same hazard <see cref="Plugins.PluginSessionObserver.GetCurrentTurnImages"/> guards against, and the same
/// destination (marshal onto the UI thread; inline when already on it, so a caller that is already there — a unit
/// test, say — never pays for a redundant dispatch), but not the same mechanism: like <see cref="SessionLabelSink"/>,
/// this hands back the awaitable from <c>Dispatcher.UIThread.InvokeAsync</c> for the caller to await, rather than
/// blocking on <c>Dispatcher.UIThread.Invoke</c> —
/// the caller here is a Kestrel request thread, and blocking it with no timeout is the wrong shape for a seam later
/// tickets (notify/inbox, claims, delivery, wake, budget, inspector) all land on top of.
/// </para>
/// </summary>
internal sealed class WorkspaceAgentGateway(CockpitViewModel cockpit) : IWorkspaceAgentGateway, ISingletonService
{
    public Task<WorkspaceAgentSnapshot?> GetWorkspaceSnapshotAsync(string paneId) =>
        Dispatcher.UIThread.CheckAccess()
            ? Task.FromResult(_GetWorkspaceSnapshot(paneId))
            : Dispatcher.UIThread.InvokeAsync(() => _GetWorkspaceSnapshot(paneId)).GetTask();

    private WorkspaceAgentSnapshot? _GetWorkspaceSnapshot(string paneId)
    {
        // A plain terminal pane (ShowPluginHeaderItems=false) has TtyLauncher's COCKPIT_PANE_ID/COCKPIT_MCP_KEY
        // stamped into it just like an agent session — any TTY, including a kept-plain shell the operator or a
        // profile started — but it has no CLI on the other end to read a list_agents result. Refusing it here (not
        // only filtering it out of the sibling list below) means it can neither enroll itself nor learn a
        // workspace's roster by being handed its own pane id back on a first call.
        if (cockpit.FindSession(paneId) is not { ShowPluginHeaderItems: true } caller)
        {
            return null;
        }

        // Resolved once per call, not once per candidate pane: the fallback below is a scan of every workspace,
        // and every sibling sharing the caller's workspace was re-running that same scan for itself.
        var firstSessionsWorkspaceId = cockpit.Workspaces.Settings.Workspaces
            .FirstOrDefault(workspace => workspace.Type == WorkspaceType.Sessions)?.Id;

        var workspaceId = _ResolveWorkspaceId(caller, firstSessionsWorkspaceId);
        if (workspaceId.Length == 0)
        {
            // No explicit workspace, and no Sessions workspace exists to fall back to (every Sessions desk closed,
            // or a graph that never had one) — reporting "" would describe a desk that is not on screen anywhere.
            return null;
        }

        var panes = cockpit.AllSessions()
            // Only real agent sessions share the roster: see the caller-side refusal above for why a plain
            // terminal pane is excluded on both sides of this call.
            .Where(candidate => candidate.ShowPluginHeaderItems && _ResolveWorkspaceId(candidate, firstSessionsWorkspaceId) == workspaceId)
            // Whether a pane gets passive delivery is asked of the pane, not decided here by its type: the pane that
            // implements turn-start delivery is the one that can honestly claim it, and a check on the type here
            // would be a second, separate answer to the same question — free to drift from the first the moment a
            // pane kind is added.
            .Select(candidate => new WorkspaceAgentPane(
                candidate.PaneId,
                candidate.Title,
                candidate.ActiveProfileLabel,
                candidate.Statusline,
                candidate.DeliversInboxAtTurnStart))
            .ToList();

        return new WorkspaceAgentSnapshot(workspaceId, panes);
    }

    // Mirrors CockpitViewModel's own BelongsToActiveWorkspace fallback: an unassigned session (empty WorkspaceId)
    // belongs to the first Sessions workspace, not to none. Takes the already-resolved first-Sessions-workspace id
    // rather than looking it up itself, so a caller iterating many candidates pays for that scan once.
    private static string _ResolveWorkspaceId(SessionPanelViewModel session, string? firstSessionsWorkspaceId) =>
        session.WorkspaceId.Length == 0 ? firstSessionsWorkspaceId ?? string.Empty : session.WorkspaceId;
}
