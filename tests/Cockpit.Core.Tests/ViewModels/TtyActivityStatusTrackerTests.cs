using Cockpit.App.ViewModels;
using FluentAssertions;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>The pure JSONL-activity-to-status logic behind a TTY session's sidebar dot (busy while the transcript grows, done once it falls quiet, idle before the first turn).</summary>
public class TtyActivityStatusTrackerTests
{
    private static readonly TimeSpan IdleThreshold = TimeSpan.FromSeconds(5);
    private static readonly DateTimeOffset T0 = new(2026, 7, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Poll_BeforeAnyActivity_IsIdle()
    {
        var tracker = new TtyActivityStatusTracker(IdleThreshold);

        tracker.Poll(T0).Should().Be(SessionStatus.Idle);
    }

    [Fact]
    public void OnActivity_ReportsBusy()
    {
        var tracker = new TtyActivityStatusTracker(IdleThreshold);

        tracker.OnActivity(T0).Should().Be(SessionStatus.Busy);
    }

    [Fact]
    public void Poll_ShortlyAfterActivity_StaysBusy()
    {
        var tracker = new TtyActivityStatusTracker(IdleThreshold);
        tracker.OnActivity(T0);

        tracker.Poll(T0 + TimeSpan.FromSeconds(2)).Should().Be(SessionStatus.Busy);
    }

    [Fact]
    public void Poll_OnceQuietPastTheIdleThreshold_IsDone()
    {
        var tracker = new TtyActivityStatusTracker(IdleThreshold);
        tracker.OnActivity(T0);

        tracker.Poll(T0 + IdleThreshold).Should().Be(SessionStatus.Done);
    }

    [Fact]
    public void OnActivity_AfterGoingDone_ReturnsToBusy()
    {
        var tracker = new TtyActivityStatusTracker(IdleThreshold);
        tracker.OnActivity(T0);
        tracker.Poll(T0 + TimeSpan.FromSeconds(10)).Should().Be(SessionStatus.Done);

        var resumed = tracker.OnActivity(T0 + TimeSpan.FromSeconds(11));

        resumed.Should().Be(SessionStatus.Busy);
        tracker.Poll(T0 + TimeSpan.FromSeconds(12)).Should().Be(SessionStatus.Busy);
    }
}
