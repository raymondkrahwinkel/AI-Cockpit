using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Assistant;

namespace Cockpit.App.Services;

// Host-side `IWorkspaceAgentGateway` (AC-391) over the running session panels: there is no existing
// "find the workspace for this pane id" helper, so this is it — the same seam `SessionVerifyGateway`
// (AC-86) is for a session's working directory. It resolves the caller's pane via `CockpitViewModel.FindSession`,
// reads which workspace it is stamped with, and reports every other AI-session pane sharing that same workspace —
// including an embedded one (`CockpitViewModel.Embed`), which is a full agent session with its own MCP
// token even though the grid never lists it.
//
// A Sessions workspace does persist its AI panes in `Workspace.Panes` now (AC-410), the same as a
// Dashboard's widgets — but that record is the operator's saved *intention*, read back only to restore a
// pane after a restart. Which live panel is on which desk right now is still read off the running
// `SessionPanelViewModel.WorkspaceId` instead, the same live source `CockpitViewModel` itself
// decides against — through `SessionWorkspacePlacement`, which is where that rule lives for every
// consumer of it. A pane it places nowhere (the assistant, or an unassigned session at a moment when no
// Sessions workspace exists to fall back to) is refused rather than handed an invented empty desk. AC-632: that is
// about the caller — the assistant is refused a desk of its own and still listed on every desk, as an address.
//
// `CockpitViewModel.Sessions` is an `System.Collections.ObjectModel.ObservableCollection{T}`
// that only ever mutates on the UI thread, but an MCP tool call lands on the endpoint's own request thread — the
// same hazard `Plugins.PluginSessionObserver.GetCurrentTurnImages` guards against, and the same
// destination (marshal onto the UI thread; inline when already on it, so a caller that is already there — a unit
// test, say — never pays for a redundant dispatch), but not the same mechanism: like `SessionLabelSink`,
// this hands back the awaitable from `Dispatcher.UIThread.InvokeAsync` for the caller to await, rather than
// blocking on `Dispatcher.UIThread.Invoke` —
// the caller here is a Kestrel request thread, and blocking it with no timeout is the wrong shape for a seam later
// tickets (notify/inbox, claims, delivery, wake, budget, inspector) all land on top of.
internal sealed class WorkspaceAgentGateway(
    CockpitViewModel cockpit,
    IWorkspaceAgentCoordinator coordinator,
    ILogger<WorkspaceAgentGateway> logger)
    : IWorkspaceAgentGateway, ISingletonService
{
    public Task<WorkspaceAgentSnapshot?> GetWorkspaceSnapshotAsync(string paneId) =>
        Dispatcher.UIThread.CheckAccess()
            ? Task.FromResult(_GetWorkspaceSnapshot(paneId))
            : Dispatcher.UIThread.InvokeAsync(() => _GetWorkspaceSnapshot(paneId)).GetTask();

    public Task<AgentWakeOutcome> TryWakeAsync(string callerPaneId, string targetPaneId, string kind) =>
        Dispatcher.UIThread.CheckAccess()
            ? Task.FromResult(_TryWake(callerPaneId, targetPaneId, kind, checkDesk: true, AgentWakeTrigger.UrgentNotify))
            : Dispatcher.UIThread.InvokeAsync(() => _TryWake(callerPaneId, targetPaneId, kind, checkDesk: true, AgentWakeTrigger.UrgentNotify)).GetTask();

    // AC-656: the host giving a pane its own already-delivered mail promptly, not a peer asking to interrupt it —
    // so there is no caller desk to re-check here the way TryWakeAsync re-checks its sender's. The boundary already
    // ran once, at the moment that mail was accepted into this pane's inbox.
    public Task<AgentWakeOutcome> TryWakeForWaitingMailAsync(string fromPaneId, string targetPaneId, string kind) =>
        Dispatcher.UIThread.CheckAccess()
            ? Task.FromResult(_TryWake(fromPaneId, targetPaneId, kind, checkDesk: false, AgentWakeTrigger.WaitingMail))
            : Dispatcher.UIThread.InvokeAsync(() => _TryWake(fromPaneId, targetPaneId, kind, checkDesk: false, AgentWakeTrigger.WaitingMail)).GetTask();

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

        // The boundary, asked again here rather than trusted from the send that got this far. The snapshot notify
        // checked was taken on an earlier trip to this thread, and a pane can be moved to another desk — or its
        // sender's own session can end, which is what makes the snapshot come back null — in between. Everything
        // below this line starts a turn on someone else's session, so the last word on whether that session is a
        // neighbour has to be spoken here, at the moment it happens. Skipped for a host-triggered wake: there is no
        // live sender to re-check a desk against, and the assistant's own address never resolves to one anyway
        // (AC-632) — this is what lets the assistant be woken by its own waiting mail at all.
        if (checkDesk
            && (_GetWorkspaceSnapshot(fromPaneId) is not { } desk
                || !desk.Panes.Any(pane => string.Equals(pane.PaneId, targetPaneId, StringComparison.Ordinal))))
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
        //
        // WaitingForInput was in the wakeable set until AC-615 and is not any more. It reads as standing still, and
        // it is not: it means a tool-use permission decision is pending, or the CLI asked for something — a question
        // in front of the operator, exactly like NeedsAttention, which the enum's own docs call the same signal. It
        // was survivable while wake was opt-in, because only a session that had chosen it could be interrupted that
        // way. Now that the operator's setting makes wake the default, it would reach every session on the desk, and
        // the first thing an agent would do with it is talk over the decision its operator is standing at.
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
        var firstSessionsWorkspaceId = SessionWorkspacePlacement.FirstSessionsWorkspaceId(cockpit.Workspaces.Settings);

        if (SessionWorkspacePlacement.Resolve(caller, firstSessionsWorkspaceId) is not { } workspaceId)
        {
            // The caller sits on no desk: the assistant (which never does, by construction — AC-543), or a session
            // with no explicit workspace at a moment when no Sessions workspace exists to fall back to. Reporting
            // a desk here would describe one that is not on screen anywhere, so it is refused instead.
            return null;
        }

        var panes = cockpit.AllSessions()
            // Only real agent sessions share the roster: see the caller-side refusal above for why a plain
            // terminal pane is excluded on both sides of this call.
            // The assistant resolves to null here and so matches no desk — it is never reported as a neighbour,
            // which is the half of the third-session-kind rule that a caller-side refusal alone would not cover.
            .Where(candidate => candidate.ShowPluginHeaderItems
                && SessionWorkspacePlacement.Resolve(candidate, firstSessionsWorkspaceId) == workspaceId)
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

        // AC-613: the host writing down the panes it knows about, which is what makes the roster measure presence
        // instead of tool use. Done here rather than at session start because this is the one place that already
        // answers "which agent sessions are on this desk" — the same rule, from the same source, applied at the only
        // moment the answer is asked for. A pane created a second ago and a pane that has run all night are on the
        // roster identically, and neither has to have called anything.
        //
        // Deliberately not the same thing as the pane having reached the cockpit-agents server: that is
        // RecordContact, and keeping the two apart is what preserves the gap that AC-156's silent injection failure
        // shows up as. Enroll never clears what a pane has already said, so running this on every snapshot is safe
        // to repeat.
        foreach (var pane in panes)
        {
            coordinator.Enroll(pane.PaneId);
        }

        return new WorkspaceAgentSnapshot(workspaceId, panes);
    }
}
