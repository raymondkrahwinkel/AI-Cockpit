using Microsoft.Extensions.Logging;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Assistant;

namespace Cockpit.App.Services;

// AC-1013: host-side `IWorkspaceAgentGateway` (AC-391) — resolves the caller's pane and reports every other
// AI-session pane sharing its workspace, read live off `SessionWorkspacePlacement` rather than the persisted
// `Workspace.Panes` intention (AC-410). The caller is a Kestrel request thread, so the hop is capped (AC-1138).
internal sealed class WorkspaceAgentGateway(
    CockpitViewModel cockpit,
    IWorkspaceAgentCoordinator coordinator,
    ILogger<WorkspaceAgentGateway> logger)
    : IWorkspaceAgentGateway, ISingletonService
{
    public Task<WorkspaceAgentSnapshot?> GetWorkspaceSnapshotAsync(string paneId) =>
        UiThreadCall.RunAsync(() => _GetWorkspaceSnapshot(paneId));

    public Task<AgentWakeOutcome> TryWakeAsync(string callerPaneId, string targetPaneId, string kind) =>
        UiThreadCall.RunAsync(() => _TryWake(callerPaneId, targetPaneId, kind, checkDesk: true, AgentWakeTrigger.UrgentNotify));

    // AC-656: the host giving a pane its own already-delivered mail promptly, not a peer asking to interrupt it —
    // so there is no caller desk to re-check here the way TryWakeAsync re-checks its sender's. The boundary already
    // ran once, at the moment that mail was accepted into this pane's inbox.
    public Task<AgentWakeOutcome> TryWakeForWaitingMailAsync(string fromPaneId, string targetPaneId, string kind) =>
        UiThreadCall.RunAsync(() => _TryWake(fromPaneId, targetPaneId, kind, checkDesk: false, AgentWakeTrigger.WaitingMail));

    private AgentWakeOutcome _TryWake(string fromPaneId, string targetPaneId, string kind, bool checkDesk, AgentWakeTrigger trigger)
    {
        // AC-632/AC-656: the assistant is in neither collection `AllSessions` reads (it sits on no desk of its own)
        // but is a real session underneath — `AssistantPane` is where every other reach into it goes too.
        var target = string.Equals(targetPaneId, AssistantIdentity.PaneId, StringComparison.Ordinal)
            ? cockpit.AssistantPane
            : cockpit.AllSessions().FirstOrDefault(session => string.Equals(session.PaneId, targetPaneId, StringComparison.Ordinal));

        if (target is null)
        {
            return AgentWakeOutcome.PaneGone;
        }

        // The boundary, asked again here rather than trusted from the earlier snapshot, since a pane can move desks
        // or its sender's session can end in between. Skipped for a host-triggered wake: there is no live sender to
        // re-check, and the assistant's own address never resolves to one anyway (AC-632).
        if (checkDesk
            && (_GetWorkspaceSnapshot(fromPaneId) is not { } desk
                || !desk.Panes.Any(pane => string.Equals(pane.PaneId, targetPaneId, StringComparison.Ordinal))))
        {
            return AgentWakeOutcome.NotOnDesk;
        }

        // A question is open in front of a human on this pane. Nothing an agent labels urgent outranks that, and
        // the status flags do not cover it: a consent banner sets PendingConsent but leaves SessionStatus reading
        // as Idle or Done, both wakeable — same rule the cockpit already applies one layer up for a second consent.
        if (target.PendingConsent is not null)
        {
            return AgentWakeOutcome.AwaitingOperator;
        }

        // AC-1013: written as an allow-list of wakeable states, not a deny-list, so a status added later defaults
        // to "not woken" rather than silently becoming wakeable. AC-615: WaitingForInput was removed from the
        // wakeable set — it looks idle but means a decision is pending on the operator, same as NeedsAttention.
        if (target.SessionStatus switch
            {
                SessionStatus.Idle or SessionStatus.Done => (AgentWakeOutcome?)null,
                SessionStatus.WaitingForInput or SessionStatus.NeedsAttention => AgentWakeOutcome.AwaitingOperator,
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
        var notice = new AgentWakeTurnNotice(fromPaneId, kind, target.DeliversInboxAtTurnStart, trigger);

        // Deliberately not awaited: an SDK pane's send does not complete until its whole turn does, and the caller
        // here is an agent waiting on its own notify call — awaiting would hold that open for another agent's answer.
        // Woken claims only that a turn was started; how it goes is the recipient's own runtime to report.
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
        // A plain terminal pane (ShowPluginHeaderItems=false) is stamped with COCKPIT_PANE_ID/COCKPIT_MCP_KEY just
        // like an agent session but has no CLI to read a list_agents result. Refused here, not only filtered from
        // the sibling list below, so it cannot enroll itself or learn a roster via its own pane id.
        if (cockpit.FindSession(paneId) is not { ShowPluginHeaderItems: true } caller)
        {
            return null;
        }

        // Resolved once per call, not once per candidate pane: the fallback below is a scan of every workspace,
        // and every sibling sharing the caller's workspace was re-running that same scan for itself.
        var firstSessionsWorkspaceId = SessionWorkspacePlacement.FirstSessionsWorkspaceId(cockpit.Workspaces.Settings);

        if (SessionWorkspacePlacement.Resolve(caller, firstSessionsWorkspaceId) is not { } workspaceId)
        {
            // The caller sits on no desk: the assistant (which never does, by construction — AC-543), or a session
            // with no explicit workspace at a moment when no Sessions workspace exists to fall back to. Reporting
            // a desk here would describe one that is not on screen anywhere, so it is refused instead.
            return null;
        }

        var panes = cockpit.AllSessions()
            // Only real agent sessions share the roster (see the caller-side refusal above). The assistant resolves
            // to null here and matches no desk, so it is never reported as a neighbour — the other half of the
            // third-session-kind rule that the caller-side refusal alone would not cover.
            .Where(candidate => candidate.ShowPluginHeaderItems
                && SessionWorkspacePlacement.Resolve(candidate, firstSessionsWorkspaceId) == workspaceId)
            // Whether a pane gets passive delivery is asked of the pane, not decided here by its type — a type
            // check here would be a second answer to the same question, free to drift the moment a pane kind is added.
            .Select(candidate => new WorkspaceAgentPane(
                candidate.PaneId,
                candidate.Title,
                candidate.ActiveProfileLabel,
                candidate.Statusline,
                candidate.DeliversInboxAtTurnStart))
            .ToList();

        // AC-632: the assistant, addressed on every desk it manages rather than placed on one, so a session it
        // started can notify it back. Only while one is running — an address with nobody behind it is lost mail.
        if (cockpit.AssistantPane is { } assistant)
        {
            panes.Add(new WorkspaceAgentPane(
                assistant.PaneId,
                assistant.Title,
                assistant.ActiveProfileLabel,
                assistant.Statusline,
                assistant.DeliversInboxAtTurnStart));
        }

        // AC-613: the host writing down the panes it knows about, so the roster measures presence, not tool use.
        // Not the same as reaching the cockpit-agents server (that is RecordContact); keeping them apart preserves
        // the gap AC-156's silent injection failure shows up as. Enroll never clears prior state, so safe to repeat.
        foreach (var pane in panes)
        {
            coordinator.Enroll(pane.PaneId);
        }

        return new WorkspaceAgentSnapshot(workspaceId, panes);
    }
}
