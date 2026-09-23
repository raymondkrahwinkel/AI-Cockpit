using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Assistant;

namespace Cockpit.Infrastructure.Agents;

// AC-1374: moved from Cockpit.App.Services — reads panes through the session registry (AC-1373) instead of
// CockpitViewModel; the UI-thread marshalling that lived here moved into the handle itself. `PlacedWorkspaceId`
// already carries the AC-543 fallback rule a handle's own adapter resolves, so this no longer re-derives it.
internal sealed class WorkspaceAgentGateway(
    ISessionRegistry sessions,
    ILogger<WorkspaceAgentGateway> logger)
    : IWorkspaceAgentGateway, ISingletonService
{
    public Task<WorkspaceAgentSnapshot?> GetWorkspaceSnapshotAsync(string paneId) =>
        Task.FromResult(_GetWorkspaceSnapshot(paneId));

    public Task<AgentWakeOutcome> TryWakeAsync(string callerPaneId, string targetPaneId, string kind) =>
        _TryWakeAsync(callerPaneId, targetPaneId, kind, checkDesk: true, AgentWakeTrigger.UrgentNotify);

    // AC-656: the host giving a pane its own already-delivered mail promptly, not a peer asking to interrupt it —
    // so there is no caller desk to re-check here the way TryWakeAsync re-checks its sender's. The boundary already
    // ran once, at the moment that mail was accepted into this pane's inbox.
    public Task<AgentWakeOutcome> TryWakeForWaitingMailAsync(string fromPaneId, string targetPaneId, string kind) =>
        _TryWakeAsync(fromPaneId, targetPaneId, kind, checkDesk: false, AgentWakeTrigger.WaitingMail);

    private async Task<AgentWakeOutcome> _TryWakeAsync(string fromPaneId, string targetPaneId, string kind, bool checkDesk, AgentWakeTrigger trigger)
    {
        // AC-632/AC-656: the assistant sits on no desk of its own but is a real session underneath — the registry's
        // own `Assistant` handle is where every other reach into it goes too.
        var target = string.Equals(targetPaneId, AssistantIdentity.PaneId, StringComparison.Ordinal)
            ? sessions.Assistant
            : sessions.Find(targetPaneId);

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

        // AC-1374: read together, not as three separate moments — a consent banner opening or a turn starting
        // between them must not leave this decision straddling before-and-after states.
        var state = await target.ReadWakeStateAsync().ConfigureAwait(false);

        // A question is open in front of a human on this pane. Nothing an agent labels urgent outranks that, and
        // the status flags do not cover it: a consent banner sets PendingConsent but leaves SessionStatus reading
        // as Idle or Done, both wakeable — same rule the cockpit already applies one layer up for a second consent.
        if (state.HasPendingConsent)
        {
            return AgentWakeOutcome.AwaitingOperator;
        }

        // AC-1013: written as an allow-list of wakeable states, not a deny-list, so a status added later defaults
        // to "not woken" rather than silently becoming wakeable. AC-1309: Failed joins Idle/Done — no question was
        // left standing — and the outstanding-work field never weighs in here, only the status next to it does.
        if (state.SessionStatus switch
            {
                SessionStatus.Idle or SessionStatus.Done or SessionStatus.Failed => (AgentWakeOutcome?)null,
                SessionStatus.NeedsAttention => AgentWakeOutcome.AwaitingOperator,
                SessionStatus.Busy or SessionStatus.WorkingBackground => AgentWakeOutcome.Busy,
                _ => AgentWakeOutcome.Busy,
            } is { } refusal)
        {
            return refusal;
        }

        if (!state.CanTakeAPrompt)
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
    private async Task _SendWakeAsync(ISessionHandle target, AgentWakeTurnNotice notice)
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
        // A plain terminal pane is stamped with COCKPIT_PANE_ID/COCKPIT_MCP_KEY just like an agent session but has
        // no CLI to read a list_agents result. Refused here, not only filtered from the sibling list below, so it
        // cannot enroll itself via its own pane id.
        if (sessions.Find(paneId) is not { IsTerminal: false } caller || caller.PlacedWorkspaceId is not { } workspaceId)
        {
            return null;
        }

        var panes = sessions.All
            // Only real agent sessions share the roster (see the caller-side refusal above). The assistant is not
            // in `All` at all, and is added separately below, so it is never reported as a neighbour by this scan.
            .Where(candidate => !candidate.IsTerminal && candidate.PlacedWorkspaceId == workspaceId)
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
        if (sessions.Assistant is { } assistant)
        {
            panes.Add(new WorkspaceAgentPane(
                assistant.PaneId,
                assistant.Title,
                assistant.ActiveProfileLabel,
                assistant.Statusline,
                assistant.DeliversInboxAtTurnStart));
        }

        return new WorkspaceAgentSnapshot(workspaceId, panes);
    }
}
