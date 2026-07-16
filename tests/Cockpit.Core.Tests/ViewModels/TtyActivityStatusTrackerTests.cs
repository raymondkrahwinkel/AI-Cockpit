using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Voice;
using FluentAssertions;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The pure activity-to-status logic behind a TTY session's sidebar dot: Busy while a turn is in progress,
/// Working-background while only a sub-agent is still going, Done once it completes, Idle before the first
/// signal — and, the fix, a long thinking pause stays Busy (status follows the last transcript signal, not a
/// quiet-timeout), with only a generous safety timeout to rescue a stalled busy turn (which a live sub-agent's
/// keep-alives never trip).
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
    public void OnActivity_Busy_IsBusy()
    {
        var tracker = new TtyActivityStatusTracker(SafetyTimeout);

        tracker.OnActivity(SessionActivity.Busy, T0).Should().Be(SessionStatus.Busy);
    }

    [Fact]
    public void OnActivity_TurnComplete_IsDone()
    {
        var tracker = new TtyActivityStatusTracker(SafetyTimeout);

        tracker.OnActivity(SessionActivity.TurnComplete, T0).Should().Be(SessionStatus.Done);
    }

    [Fact]
    public void OnActivity_BackgroundBusy_IsWorkingBackground()
    {
        // A sub-agent is still running while the main agent is quiet — not idle, not the main agent working.
        var tracker = new TtyActivityStatusTracker(SafetyTimeout);
        tracker.OnActivity(SessionActivity.Busy, T0);

        tracker.OnActivity(SessionActivity.BackgroundBusy, T0 + TimeSpan.FromSeconds(1))
            .Should().Be(SessionStatus.WorkingBackground);
    }

    [Fact]
    public void BackgroundKeepAlives_KeepALongSubAgentRunOffDone()
    {
        // The bug: a sub-agent runs for minutes, the main transcript is silent, and the old logic timed the turn
        // out to Done. The plugin now emits BackgroundBusy keep-alives, each resetting the safety timeout.
        var tracker = new TtyActivityStatusTracker(SafetyTimeout);
        tracker.OnActivity(SessionActivity.Busy, T0);

        // Keep-alive every 5s for well past the safety timeout.
        SessionStatus status = SessionStatus.Idle;
        for (var t = TimeSpan.FromSeconds(5); t <= TimeSpan.FromSeconds(300); t += TimeSpan.FromSeconds(5))
        {
            status = tracker.OnActivity(SessionActivity.BackgroundBusy, T0 + t);
        }

        status.Should().Be(SessionStatus.WorkingBackground);
    }

    [Fact]
    public void Poll_DuringALongThinkingPause_StaysBusy()
    {
        // A busy turn writes no transcript line for a long while (claude thinking) but must not flip to Done the
        // way the old quiet-timeout did.
        var tracker = new TtyActivityStatusTracker(SafetyTimeout);
        tracker.OnActivity(SessionActivity.Busy, T0);

        tracker.Poll(T0 + TimeSpan.FromSeconds(30)).Should().Be(SessionStatus.Busy);
    }

    [Fact]
    public void OnActivity_None_LeavesTheStatusUnchanged()
    {
        var tracker = new TtyActivityStatusTracker(SafetyTimeout);
        tracker.OnActivity(SessionActivity.Busy, T0);

        // A metadata reading (None) carries no signal, so the prior Busy stands.
        tracker.OnActivity(SessionActivity.None, T0 + TimeSpan.FromSeconds(1)).Should().Be(SessionStatus.Busy);
    }

    [Fact]
    public void Poll_WhenABusyTurnGoesSilentPastTheSafetyTimeout_FallsBackToDone()
    {
        var tracker = new TtyActivityStatusTracker(SafetyTimeout);
        tracker.OnActivity(SessionActivity.Busy, T0);

        tracker.Poll(T0 + SafetyTimeout).Should().Be(SessionStatus.Done);
    }

    [Fact]
    public void OnActivity_TurnStartsAgainAfterDone_ReturnsToBusy()
    {
        var tracker = new TtyActivityStatusTracker(SafetyTimeout);
        tracker.OnActivity(SessionActivity.TurnComplete, T0);
        tracker.Poll(T0 + TimeSpan.FromSeconds(1)).Should().Be(SessionStatus.Done);

        tracker.OnActivity(SessionActivity.Busy, T0 + TimeSpan.FromSeconds(2)).Should().Be(SessionStatus.Busy);
    }
}
