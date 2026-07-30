using Cockpit.App.ViewModels;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// Status while work outlives the turn (AC-276). The main agent legitimately reaches <c>end_turn</c> several times
/// per instruction while sub-agents it spawned keep running — measured at 1195 of 3054 turn endings across 77 real
/// sessions — and <see cref="SessionViewModel.IsBusy"/> alone flipped the session to Done on every one of them,
/// firing a premature "session finished" each time. A sub-agent now holds the session on
/// <see cref="SessionStatus.WorkingBackground"/>; a shell deliberately does not, because a dev server would
/// otherwise pin the session there for as long as it runs.
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
    });

    [Fact]
    public void AnOutstandingShell_DoesNotHoldTheStatus() => HeadlessAvalonia.Run(() =>
    {
        // The regression this guards is the fix's own worst failure mode: a dev server or tail -f never ends, so
        // treating it like a sub-agent would leave the session on WorkingBackground forever — worse than the
        // premature Done the ticket is about.
        var session = new SessionViewModel();
        session.IsBusy = true;
        session.Apply(Outstanding(new BackgroundTask("b1", BackgroundTaskKind.Shell, "npm run dev")));

        session.Apply(Turn());

        Assert.Equal(SessionStatus.Done, session.SessionStatus);
        Assert.True(session.HasOutstandingBackgroundShells, "the shell is still tracked, it just does not hold the status");
    });

    [Fact]
    public void AnUnknownKind_HoldsNothing_ButIsStillCarried() => HeadlessAvalonia.Run(() =>
    {
        // A task type a newer CLI names and this build does not: it must not be able to freeze the status by
        // passing itself off as a sub-agent. Ordinal 0 is Unknown precisely so an unmapped value lands here.
        var session = new SessionViewModel();
        session.IsBusy = true;
        session.Apply(Outstanding(new BackgroundTask("x1", BackgroundTaskKind.Unknown, "something new")));

        session.Apply(Turn());

        Assert.Equal(SessionStatus.Done, session.SessionStatus);
        Assert.False(session.HasOutstandingBackgroundShells);
    });

    [Fact]
    public void SubAgentsAndShellsTogether_TheSubAgentDecides() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();
        session.IsBusy = true;
        session.Apply(Outstanding(
            new BackgroundTask("a1", BackgroundTaskKind.SubAgent, "Agent 1"),
            new BackgroundTask("b1", BackgroundTaskKind.Shell, "npm run dev")));

        session.Apply(Turn());

        Assert.Equal(SessionStatus.WorkingBackground, session.SessionStatus);
        Assert.True(session.HasOutstandingBackgroundShells);
    });

    [Fact]
    public void ASessionError_ClearsOutstandingWork_SoACrashedSessionDoesNotHangOnWorkingBackground() => HeadlessAvalonia.Run(() =>
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

        // Idle rather than Done because no turn ever completed — the point is that it is not WorkingBackground,
        // which is where a surviving sub-agent entry would have pinned it with nothing left to release it.
        Assert.Equal(SessionStatus.Idle, session.SessionStatus);
        Assert.False(session.HasOutstandingBackgroundShells);
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
}
