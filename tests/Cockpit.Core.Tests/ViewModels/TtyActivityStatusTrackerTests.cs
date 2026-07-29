using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Voice;

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

        Assert.Equal(SessionStatus.Idle, tracker.Poll(T0));
    }

    [Fact]
    public void OnActivity_Busy_IsBusy()
    {
        var tracker = new TtyActivityStatusTracker(SafetyTimeout);

        Assert.Equal(SessionStatus.Busy, tracker.OnActivity(SessionActivity.Busy, T0));
    }

    [Fact]
    public void OnActivity_TurnComplete_IsDone()
    {
        var tracker = new TtyActivityStatusTracker(SafetyTimeout);

        Assert.Equal(SessionStatus.Done, tracker.OnActivity(SessionActivity.TurnComplete, T0));
    }

    [Fact]
    public void OnActivity_BackgroundBusy_IsWorkingBackground()
    {
        // A sub-agent is still running while the main agent is quiet — not idle, not the main agent working.
        var tracker = new TtyActivityStatusTracker(SafetyTimeout);
        tracker.OnActivity(SessionActivity.Busy, T0);

        Assert.Equal(SessionStatus.WorkingBackground, tracker.OnActivity(SessionActivity.BackgroundBusy, T0 + TimeSpan.FromSeconds(1)));
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

        Assert.Equal(SessionStatus.WorkingBackground, status);
    }

    [Fact]
    public void Poll_DuringALongThinkingPause_StaysBusy()
    {
        // A busy turn writes no transcript line for a long while (claude thinking) but must not flip to Done the
        // way the old quiet-timeout did.
        var tracker = new TtyActivityStatusTracker(SafetyTimeout);
        tracker.OnActivity(SessionActivity.Busy, T0);

        Assert.Equal(SessionStatus.Busy, tracker.Poll(T0 + TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void OnActivity_None_LeavesTheStatusUnchanged()
    {
        var tracker = new TtyActivityStatusTracker(SafetyTimeout);
        tracker.OnActivity(SessionActivity.Busy, T0);

        // A metadata reading (None) carries no signal, so the prior Busy stands.
        Assert.Equal(SessionStatus.Busy, tracker.OnActivity(SessionActivity.None, T0 + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Poll_WhenABusyTurnGoesSilentPastTheSafetyTimeout_FallsBackToDone()
    {
        var tracker = new TtyActivityStatusTracker(SafetyTimeout);
        tracker.OnActivity(SessionActivity.Busy, T0);

        Assert.Equal(SessionStatus.Done, tracker.Poll(T0 + SafetyTimeout));
    }

    [Fact]
    public void OnActivity_TurnStartsAgainAfterDone_ReturnsToBusy()
    {
        var tracker = new TtyActivityStatusTracker(SafetyTimeout);
        tracker.OnActivity(SessionActivity.TurnComplete, T0);
        Assert.Equal(SessionStatus.Done, tracker.Poll(T0 + TimeSpan.FromSeconds(1)));

        Assert.Equal(SessionStatus.Busy, tracker.OnActivity(SessionActivity.Busy, T0 + TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void OnAlive_KeepsAVisiblyWorkingBusyTurnOffDonePastTheSafetyTimeout()
    {
        // AC-75: a long silent think/plan phase writes no transcript line, but the pty keeps drawing (the spinner
        // ticks), so a liveness keep-alive from that output must keep the turn Busy past the safety timeout — the
        // same rescue a live sub-agent's BackgroundBusy keep-alives already give a background run.
        var tracker = new TtyActivityStatusTracker(SafetyTimeout);
        tracker.OnActivity(SessionActivity.Busy, T0);

        SessionStatus status = SessionStatus.Idle;
        for (var t = TimeSpan.FromSeconds(5); t <= TimeSpan.FromSeconds(600); t += TimeSpan.FromSeconds(5))
        {
            status = tracker.OnAlive(T0 + t);
        }

        Assert.Equal(SessionStatus.Busy, status);
    }

    [Fact]
    public void OnAlive_AfterATurnCompleted_DoesNotResurrectItToBusy()
    {
        // A liveness signal (e.g. the prompt redrawing) must not flip a genuinely completed turn back to Busy.
        var tracker = new TtyActivityStatusTracker(SafetyTimeout);
        tracker.OnActivity(SessionActivity.TurnComplete, T0);

        Assert.Equal(SessionStatus.Done, tracker.OnAlive(T0 + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void OnAlive_BeforeAnySignal_StaysIdle()
    {
        var tracker = new TtyActivityStatusTracker(SafetyTimeout);

        Assert.Equal(SessionStatus.Idle, tracker.OnAlive(T0));
    }

    [Fact]
    public void OnAlive_AfterASafetyTimeoutDone_RecoversToBusyOnRenewedOutput()
    {
        // A busy turn that decayed to Done via the safety timeout is not genuinely finished (its last activity is
        // still Busy), so renewed pty output means it is alive again and recovers to Busy — an alive session should
        // read Busy. Contrast a TurnComplete Done, which stays Done (OnAlive_AfterATurnCompleted_...).
        var tracker = new TtyActivityStatusTracker(SafetyTimeout);
        tracker.OnActivity(SessionActivity.Busy, T0);
        Assert.Equal(SessionStatus.Done, tracker.Poll(T0 + SafetyTimeout));

        Assert.Equal(SessionStatus.Busy, tracker.OnAlive(T0 + SafetyTimeout + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void OnAlive_ThatStopsComing_LetsTheTurnFallToDone()
    {
        // The safety net is intact: once the pty goes truly silent (no output, no keep-alive), the busy turn still
        // times out to Done — a stalled or killed CLI is not shown busy forever.
        var tracker = new TtyActivityStatusTracker(SafetyTimeout);
        tracker.OnActivity(SessionActivity.Busy, T0);
        tracker.OnAlive(T0 + TimeSpan.FromSeconds(30));

        // Last liveness signal at T0+30s; nothing after → times out 120s later.
        Assert.Equal(SessionStatus.Done, tracker.Poll(T0 + TimeSpan.FromSeconds(30) + SafetyTimeout));
    }
}
