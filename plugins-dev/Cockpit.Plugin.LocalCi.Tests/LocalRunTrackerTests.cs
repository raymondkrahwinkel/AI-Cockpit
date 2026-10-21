using Cockpit.Plugin.LocalCi.Execution;

namespace Cockpit.Plugin.LocalCi.Tests;

// AC-1535, layer 2 (authorisation, AC-82): the operator's Kill is the only stop for a container an agent started, so
// one run beginning and ending must never take another run's Kill out of the status bar.
public class LocalRunTrackerTests
{
    [Fact]
    public void ASecondRunEnding_LeavesTheFirstRunsKillInTheStatusBar()
    {
        var tracker = new LocalRunTracker();
        var checkout = Path.GetTempPath();

        tracker.Begin("first", checkout, "build", DateTimeOffset.UtcNow, () => Task.CompletedTask);
        tracker.Begin("second", checkout, "build", DateTimeOffset.UtcNow, () => Task.CompletedTask);
        tracker.Complete(
            checkout,
            LocalRunResult.DidNotRun("ci.yml", "build", LocalRunOutcome.Cancelled, "stopped."),
            commit: null,
            DateTimeOffset.UtcNow,
            runId: "second");

        Assert.Equal("first", Assert.Single(tracker.Snapshot()).Id);
    }
}
