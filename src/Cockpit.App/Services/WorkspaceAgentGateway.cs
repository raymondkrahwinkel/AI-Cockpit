using Avalonia.Threading;
using Microsoft.Extensions.Logging;
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
/// A Sessions workspace does persist its AI panes in <see cref="Workspace.Panes"/> now (AC-410), the same as a
/// Dashboard's widgets — but that record is the operator's saved <em>intention</em>, read back only to restore a
/// pane after a restart. Which live panel is on which desk right now is still read off the running
/// <see cref="SessionPanelViewModel.WorkspaceId"/> instead, the same live source <c>CockpitViewModel</c> itself
/// decides against: an empty <c>WorkspaceId</c> (a session started before workspaces existed, or in the
/// design-time graph) falls back to the first Sessions workspace, rather than being read as belonging to none —
/// but when there is no Sessions workspace to fall back to either, the pane resolves to no workspace at all, and
/// is refused rather than handed an invented empty one.
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
internal sealed class WorkspaceAgentGateway(CockpitViewModel cockpit, ILogger<WorkspaceAgentGateway> logger)
    : IWorkspaceAgentGateway, ISingletonService
{
    public Task<WorkspaceAgentSnapshot?> GetWorkspaceSnapshotAsync(string paneId) =>
        Dispatcher.UIThread.CheckAccess()
            ? Task.FromResult(_GetWorkspaceSnapshot(paneId))
            : Dispatcher.UIThread.InvokeAsync(() => _GetWorkspaceSnapshot(paneId)).GetTask();

    public Task<AgentWakeOutcome> TryWakeAsync(string callerPaneId, string targetPaneId, string kind) =>
        Dispatcher.UIThread.CheckAccess()
            ? Task.FromResult(_TryWake(callerPaneId, targetPaneId, kind))
            : Dispatcher.UIThread.InvokeAsync(() => _TryWake(callerPaneId, targetPaneId, kind)).GetTask();

    private AgentWakeOutcome _TryWake(string callerPaneId, string targetPaneId, string kind)
    {
        if (cockpit.AllSessions().FirstOrDefault(session => string.Equals(session.PaneId, targetPaneId, StringComparison.Ordinal)) is not { } target)
        {
            return AgentWakeOutcome.PaneGone;
        }

        // The boundary, asked again here rather than trusted from the send that got this far. The snapshot notify
        // checked was taken on an earlier trip to this thread, and a pane can be moved to another desk — or its
        // sender's own session can end, which is what makes the snapshot come back null — in between. Everything
        // below this line starts a turn on someone else's session, so the last word on whether that session is a
        // neighbour has to be spoken here, at the moment it happens.
        if (_GetWorkspaceSnapshot(callerPaneId) is not { } desk
            || !desk.Panes.Any(pane => string.Equals(pane.PaneId, targetPaneId, StringComparison.Ordinal)))
        {
            return AgentWakeOutcome.NotOnDesk;
        }

        // A question is open in front of a human on this pane. Nothing an agent labels urgent outranks that, and
        // the status flags do not cover it: opening a consent banner sets PendingConsent and leaves SessionStatus
        // alone, so a pane with a decision waiting on screen still reads as Idle or Done — both of which are woken.
        // The cockpit already treats an open banner as untouchable one layer up, where a second consent request is
        // refused rather than allowed to replace the first; this is the same rule for a different intruder.
        if (target.PendingConsent is not null)
        {
            return AgentWakeOutcome.AwaitingOperator;
        }

        // Written as the list of states that may be woken, not the list that may not. A status added later then
        // arrives as "not woken" and has to be argued into the set deliberately — where a deny-list would have made
        // it wakeable the day it was declared, silently, by whoever was thinking about something else entirely.
        if (target.SessionStatus switch
            {
                SessionStatus.Idle or SessionStatus.Done or SessionStatus.WaitingForInput => (AgentWakeOutcome?)null,
                SessionStatus.NeedsAttention => AgentWakeOutcome.AwaitingOperator,
                SessionStatus.Busy or SessionStatus.WorkingBackground => AgentWakeOutcome.Busy,
                _ => AgentWakeOutcome.Busy,
            } is { } refusal)
        {
            return refusal;
        }

        if (!target.CanTakeAPrompt)
        {
            return AgentWakeOutcome.CannotTakeATurn;
        }

        // Asked of the pane at the moment of waking, so the notice tells the truth about this turn rather than
        // about the pane as some earlier snapshot described it.
        var notice = new AgentWakeTurnNotice(callerPaneId, kind, target.DeliversInboxAtTurnStart);

        // Deliberately not awaited, unlike the scheduled-resume path that shares this method. An SDK pane's send does
        // not complete until its whole turn does, and the caller here is an agent waiting on its own notify call —
        // awaiting would hold that call open for the length of another agent's answer. So what Woken claims is that a
        // turn was started on a pane that could take one, which is settled by everything above this line; how the
        // turn goes is between the recipient and its own runtime, and its own surfaces report that.
        _ = _SendWakeAsync(target, notice);

        return AgentWakeOutcome.Woken;
    }

    // Observed rather than discarded, because the send can throw — the funnel it goes through rethrows after putting
    // any mail it took back in the inbox. On a discarded task that surfaces as an unobserved exception at some later
    // garbage collection, attributed to nothing; here it is one line naming the pane a wake did not reach.
    private async Task _SendWakeAsync(SessionPanelViewModel target, AgentWakeTurnNotice notice)
    {
        try
        {
            await target.SendPromptAsync(notice.Render());
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "A wake for session {Pane} was started but its turn could not be sent.", target.PaneId);
        }
    }

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
