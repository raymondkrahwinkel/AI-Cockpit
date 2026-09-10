using Cockpit.App.ViewModels;
using Cockpit.Core.Sessions;

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
/// </summary>
[Collection("avalonia")]
public class SessionOutstandingWorkStatusTests
{
    private static BackgroundTasksChanged Outstanding(params BackgroundTask[] tasks) =>
        new() { SessionId = "s1", Tasks = tasks };

    private static TurnCompleted Turn() =>
        new() { SessionId = "s1", Subtype = "success", Result = "done", IsError = false };

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
}
