using Cockpit.Core.Abstractions.Shell;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.Infrastructure.Tests.Sessions;

public class RunTrackerTests
{
    [Fact]
    public void Begin_ShowsUpInTheSnapshot()
    {
        var tracker = new RunTracker();
        var stopped = false;

        tracker.Begin("run-1", "dotnet test", DateTimeOffset.UtcNow, () =>
        {
            stopped = true;
            return Task.CompletedTask;
        });

        var activity = Assert.Single(tracker.Snapshot());
        Assert.Equal("run-1", activity.Id);
        Assert.Equal("dotnet test", activity.Title);

        activity.StopAsync();
        Assert.True(stopped);
    }

    [Fact]
    public void TwoConcurrentRuns_BothShowInTheSnapshot()
    {
        var tracker = new RunTracker();

        tracker.Begin("run-1", "dotnet test A", DateTimeOffset.UtcNow, () => Task.CompletedTask);
        tracker.Begin("run-2", "dotnet test B", DateTimeOffset.UtcNow, () => Task.CompletedTask);

        Assert.Equal(2, tracker.Snapshot().Count);
    }

    [Fact]
    public void Complete_MovesARunFromRunningToFinished_AndOutOfTheSnapshot()
    {
        var tracker = new RunTracker();
        tracker.Begin("run-1", "dotnet test", DateTimeOffset.UtcNow, () => Task.CompletedTask);

        var result = new TrackedRunResult(0, "ok", "", TimeSpan.FromSeconds(1), TimedOut: false);
        tracker.Complete("run-1", result, DateTimeOffset.UtcNow);

        Assert.Empty(tracker.Snapshot());
        Assert.False(tracker.IsRunning("run-1"));
        Assert.Equal(result, tracker.Get("run-1")?.Result);
    }

    [Fact]
    public void Get_OnAnUnknownRun_ReturnsNull()
    {
        var tracker = new RunTracker();

        Assert.Null(tracker.Get("never-started"));
    }

    [Fact]
    public void Changed_FiresOnBeginAndOnComplete()
    {
        var tracker = new RunTracker();
        var fired = 0;
        tracker.Changed += () => fired++;

        tracker.Begin("run-1", "dotnet test", DateTimeOffset.UtcNow, () => Task.CompletedTask);
        tracker.Complete("run-1", new TrackedRunResult(0, "", "", TimeSpan.Zero, false), DateTimeOffset.UtcNow);

        Assert.Equal(2, fired);
    }
}
