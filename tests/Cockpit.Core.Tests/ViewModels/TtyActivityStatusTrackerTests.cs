using Cockpit.App.ViewModels;
using FluentAssertions;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The pure signal-to-status logic behind a TTY session's sidebar dot: Busy while a turn is in progress, Done
/// once it completes, Idle before the first signal — and, the fix, a long thinking pause stays Busy (status
/// follows the last transcript signal, not a quiet-timeout), with only a generous safety timeout to rescue a
/// stalled busy turn.
/// </summary>
public class TtyActivityStatusTrackerTests
{
    private static readonly TimeSpan SafetyTimeout = TimeSpan.FromSeconds(120);
    private static readonly DateTimeOffset T0 = new(2026, 7, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Poll_BeforeAnySignal_IsIdle()
    {
        var tracker = new TtyActivityStatusTracker(SafetyTimeout);

        tracker.Poll(T0).Should().Be(SessionStatus.Idle);
    }

    [Fact]
    public void OnLine_TurnInProgress_IsBusy()
    {
        var tracker = new TtyActivityStatusTracker(SafetyTimeout);

        tracker.OnLine(turnInProgress: true, T0).Should().Be(SessionStatus.Busy);
    }

    [Fact]
    public void OnLine_TurnCompleted_IsDone()
    {
        var tracker = new TtyActivityStatusTracker(SafetyTimeout);

        tracker.OnLine(turnInProgress: false, T0).Should().Be(SessionStatus.Done);
    }

    [Fact]
    public void Poll_DuringALongThinkingPause_StaysBusy()
    {
        // The core fix: a busy turn writes no transcript line for a long while (claude thinking) but must not
        // flip to Done the way the old quiet-timeout did.
        var tracker = new TtyActivityStatusTracker(SafetyTimeout);
        tracker.OnLine(turnInProgress: true, T0);

        tracker.Poll(T0 + TimeSpan.FromSeconds(30)).Should().Be(SessionStatus.Busy);
    }

    [Fact]
    public void OnLine_MetadataLine_LeavesTheStatusUnchanged()
    {
        var tracker = new TtyActivityStatusTracker(SafetyTimeout);
        tracker.OnLine(turnInProgress: true, T0);

        // A metadata line (null) carries no signal, so the prior Busy stands.
        tracker.OnLine(turnInProgress: null, T0 + TimeSpan.FromSeconds(1)).Should().Be(SessionStatus.Busy);
    }

    [Fact]
    public void Poll_WhenABusyTurnGoesSilentPastTheSafetyTimeout_FallsBackToDone()
    {
        var tracker = new TtyActivityStatusTracker(SafetyTimeout);
        tracker.OnLine(turnInProgress: true, T0);

        tracker.Poll(T0 + SafetyTimeout).Should().Be(SessionStatus.Done);
    }

    [Fact]
    public void OnLine_TurnStartsAgainAfterDone_ReturnsToBusy()
    {
        var tracker = new TtyActivityStatusTracker(SafetyTimeout);
        tracker.OnLine(turnInProgress: false, T0);
        tracker.Poll(T0 + TimeSpan.FromSeconds(1)).Should().Be(SessionStatus.Done);

        tracker.OnLine(turnInProgress: true, T0 + TimeSpan.FromSeconds(2)).Should().Be(SessionStatus.Busy);
    }
}
