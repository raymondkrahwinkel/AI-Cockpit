using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// Status while work outlives the turn (AC-276), and status for a turn that did not end cleanly (AC-1309). The
/// main agent legitimately reaches <c>end_turn</c> several times per instruction while sub-agents it spawned keep
/// running — measured at 1195 of 3054 turn endings across 77 real sessions — and <see cref="SessionViewModel.IsBusy"/>
/// alone flipped the session to Done on every one of them, firing a premature "session finished" each time. A
/// sub-agent now holds the session on <see cref="SessionStatus.WorkingBackground"/>; a shell deliberately does
/// not, because a dev server would otherwise pin the session there for as long as it runs — instead it is
/// reported alongside the status via <see cref="SessionViewModel.HasOutstandingBackgroundShells"/>, never inside
/// it, so it cannot blot out <see cref="SessionStatus.NeedsAttention"/> or hide a crashed turn behind "Done".
/// AC-1319 adds the turn the host never sent: the CLI starts one itself from its own queue, and the stream has no
/// turn-start line for it — measured on 2026-09-14 as a session reading Idle through a 40-minute turn.
/// </summary>
[Collection("avalonia")]
public class SessionOutstandingWorkStatusTests
{
    private static BackgroundTasksChanged Outstanding(params BackgroundTask[] tasks) =>
        new() { SessionId = "s1", Tasks = tasks };

    private static TurnCompleted Turn() =>
        new() { SessionId = "s1", Subtype = "success", Result = "done", IsError = false };

    // The Claude SDK driver as the host sees it: it keeps its own input queue (AC-739), which is the one capability
    // that lets a turn the host never sent exist. A driver without it never starts a turn by itself (AC-1319).
    private static SessionViewModel _ClaudeSdkSession() =>
        new(Substitute.For<ISessionManager>()) { Capabilities = SessionCapabilities.ClaudeCli with { SupportsMidTurnInput = true } };

    [Fact]
    public void ATurnEndingWhileASubAgentRuns_ReadsAsWorkingBackground_NotDone() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();
        session.IsBusy = true;
        session.Apply(Outstanding(new BackgroundTask("a1", BackgroundTaskKind.SubAgent, "Agent 1")));

        session.Apply(Turn());

        Assert.Equal(SessionStatus.WorkingBackground, session.SessionStatus);
    });

    [Fact]
    public void OnceTheLastSubAgentFinishes_TheSessionReachesDone() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();
        session.IsBusy = true;
        session.Apply(Outstanding(new BackgroundTask("a1", BackgroundTaskKind.SubAgent, "Agent 1")));
        session.Apply(Turn());

        // The provider restates the whole set, now empty — that is how the last one ending arrives.
        session.Apply(Outstanding());

        Assert.Equal(SessionStatus.Done, session.SessionStatus);
        // AC-1309's other half of the same pair: nothing left running means nothing to report either.
        Assert.False(session.HasOutstandingBackgroundShells);
    });

    [Theory]
    [InlineData(false, SessionStatus.Done)]
    [InlineData(true, SessionStatus.NeedsAttention)]
    public void AnOutstandingShell_DoesNotHoldTheStatus_ButIsStillReported(bool needsAttention, SessionStatus expected) => HeadlessAvalonia.Run(() =>
    {
        // AC-276's worst failure mode: a dev server never ends, so holding the status on it would pin the session
        // forever. AC-1309 adds the case a status-only fix cannot carry: a permission prompt still outranks the
        // status, but the shell must still be reported underneath it.
        var session = new SessionViewModel();
        session.IsBusy = true;
        session.Apply(Outstanding(new BackgroundTask("b1", BackgroundTaskKind.Shell, "npm run dev")));
        session.Apply(Turn());

        if (needsAttention)
        {
            session.Apply(new SessionStatusChanged { SessionId = "s1", NeedsAction = "permission" });
        }

        Assert.Equal(expected, session.SessionStatus);
        Assert.True(session.HasOutstandingBackgroundShells, "the shell is still tracked, it just does not hold the status");
    });

    [Fact]
    public void ASessionError_SetsFailed_AndClearsOutstandingWork_ButANewTurnReadsAsBusyAgain() => HeadlessAvalonia.Run(() =>
    {
        // Unlike the TTY route this one has no safety timeout to fall back on: a sub-agent left in the list after
        // the session died would hold it on WorkingBackground indefinitely, and RequiresCloseConfirmation would
        // keep asking "still working?" when closing a session that is not.
        var session = new SessionViewModel();
        session.IsBusy = true;
        session.Apply(Outstanding(
            new BackgroundTask("a1", BackgroundTaskKind.SubAgent, "Agent 1"),
            new BackgroundTask("b1", BackgroundTaskKind.Shell, "npm run dev")));

        session.Apply(new SessionError { SessionId = "s1", Message = "the driver died" });

        // AC-1309: a crashed turn must never read as Done — that is what the OAuth-token measurement on the
        // ticket showed happening today.
        Assert.Equal(SessionStatus.Failed, session.SessionStatus);
        Assert.False(session.HasOutstandingBackgroundShells, "whatever was outstanding died with the session (AC-276)");

        // The tegenproef a fix that only checks `ErrorKind != null` fails: Failed must not survive the next turn
        // starting, or a session that recovers reads as still broken while it is working again.
        session.IsBusy = true;
        session.Apply(new SessionStatusChanged { SessionId = "s1" });

        Assert.Equal(SessionStatus.Busy, session.SessionStatus);
    });

    [Fact]
    public void NeedsAttention_StillOutranksOutstandingWork() => HeadlessAvalonia.Run(() =>
    {
        // A permission prompt must still surface while a sub-agent runs: an operator who cannot see the request
        // cannot answer it, and the session would sit on WorkingBackground waiting for an answer it never asked for.
        var session = new SessionViewModel();
        session.IsBusy = true;
        session.Apply(Outstanding(new BackgroundTask("a1", BackgroundTaskKind.SubAgent, "Agent 1")));
        session.Apply(Turn());

        session.Apply(new SessionStatusChanged { SessionId = "s1", NeedsAction = "permission" });

        Assert.Equal(SessionStatus.NeedsAttention, session.SessionStatus);
    });

    [Fact]
    public void OutstandingWorkNeverMakesASessionRequireCloseConfirmation() => HeadlessAvalonia.Run(() =>
    {
        // AC-276's dev-server objection, as a test on the field this ticket adds: a shell that never ends must
        // not turn "Done" into "ask before closing" through the back door the status itself was closed to avoid.
        var session = new SessionViewModel();
        session.IsBusy = true;
        session.Apply(Outstanding(new BackgroundTask("b1", BackgroundTaskKind.Shell, "npm run dev")));
        session.Apply(Turn());

        Assert.Equal(SessionStatus.Done, session.SessionStatus);
        Assert.True(session.HasOutstandingBackgroundShells);
        Assert.False(session.RequiresCloseConfirmation, "only Busy/WorkingBackground ask before closing — the outstanding-work field must not join that set");
    });

    // AC-1319: the CLI's own queue starts a turn the host never sent — a prompt written mid-turn that arrived after
    // the `result` (SF-161, 07:58:55Z), whose first tool call then ran for eleven minutes under an Idle status.
    [Fact]
    public void ATurnTheCliStartsFromItsOwnQueue_ReadsAsBusyFromItsFirstEvent_AndDoneAfterItsResult() => HeadlessAvalonia.Run(() =>
    {
        var session = _ClaudeSdkSession();
        session.IsBusy = true;
        session.Apply(Turn());
        Assert.Equal(SessionStatus.Done, session.SessionStatus);

        // Acceptance 1: a foreground tool call without a result is never Idle or Done.
        session.Apply(new ToolUseRequested { SessionId = "s1", ToolUseId = "t1", ToolName = "Bash", InputJson = "{}" });
        Assert.Equal(SessionStatus.Busy, session.SessionStatus);

        // Tegenproef: the same session after its ToolResult and TurnCompleted is Done — acceptance 4 for the watcher,
        // which reads exactly this status and only ever saw Busy → Idle mid-turn because the status was wrong.
        session.Apply(new ToolResult { SessionId = "s1", ToolUseId = "t1", Content = "ok", IsError = false });
        session.Apply(Turn());
        Assert.Equal(SessionStatus.Done, session.SessionStatus);
    });

    // AC-1319, PP-110 (07:57:37Z): the async sub-agent finished, its task-notification started a turn, and the
    // ledger emptying in the same instant took the session from WorkingBackground to Idle while the agent worked.
    [Fact]
    public void ATaskNotificationTurn_ReadsAsBusy_NotDone_WhenTheLastSubAgentEndsAsItStarts() => HeadlessAvalonia.Run(() =>
    {
        var session = _ClaudeSdkSession();
        session.IsBusy = true;
        session.Apply(Outstanding(new BackgroundTask("a1", BackgroundTaskKind.SubAgent, "Agent 1")));
        session.Apply(Turn());
        // Acceptance 2: the launched sub-agent holds WorkingBackground once the launching turn ends.
        Assert.Equal(SessionStatus.WorkingBackground, session.SessionStatus);

        session.Apply(Outstanding());
        session.Apply(new AssistantThinkingDelta { SessionId = "s1", BlockIndex = 0, Thinking = "The agent is done." });

        Assert.Equal(SessionStatus.Busy, session.SessionStatus);
        // Tegenproef: with the sub-agent gone and the turn over, nothing holds it any more.
        session.Apply(Turn());
        Assert.Equal(SessionStatus.Done, session.SessionStatus);
    });

    // The opposite bug the trigger must not introduce: what still arrives after a `result` without a new turn —
    // measured as none of the agent's own output in 721 turn endings across 18 sessions, only sub-agent traffic and
    // ledger events — leaves a finished session finished.
    [Fact]
    public void EventsAfterTheResultThatAreNotTheAgentsOwn_LeaveAFinishedSessionDone() => HeadlessAvalonia.Run(() =>
    {
        var session = _ClaudeSdkSession();
        session.IsBusy = true;
        session.Apply(Outstanding(new BackgroundTask("a1", BackgroundTaskKind.SubAgent, "Agent 1")));
        session.Apply(Turn());

        session.Apply(new AssistantTextDelta { SessionId = "s1", BlockIndex = 0, Text = "sub-agent prose", ParentToolUseId = "task-1" });
        session.Apply(new ToolUseRequested { SessionId = "s1", ToolUseId = "t9", ToolName = "Read", InputJson = "{}", ParentToolUseId = "task-1" });
        session.Apply(new BackgroundTaskNotification { SessionId = "s1", TaskId = "a1", ToolUseId = "task-1", Status = BackgroundTaskStatus.Completed });
        session.Apply(Outstanding());
        session.Apply(new SessionStatusChanged { SessionId = "s1" });

        Assert.Equal(SessionStatus.Done, session.SessionStatus);
        Assert.False(session.IsBusy);
    });

    // Tegenproef on the gate: a driver without its own input queue (Kimi polls /usage after each turn and a reply
    // chunk can fall through as plain text) never starts a turn by itself, so its stray output leaves Done alone.
    [Fact]
    public void AStrayDeltaFromADriverWithoutMidTurnInput_LeavesAFinishedSessionDone() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel(Substitute.For<ISessionManager>());
        session.IsBusy = true;
        session.Apply(Turn());

        session.Apply(new AssistantTextDelta { SessionId = "s1", BlockIndex = 0, Text = "context: 12%" });

        Assert.Equal(SessionStatus.Done, session.SessionStatus);
    });
}
