using Cockpit.Plugin.LocalCi.Execution;

namespace Cockpit.Plugin.LocalCi.Tests;

public class LocalRunReportingTests
{
    [Fact]
    public void NoEndingEverClaimsSomethingAboutCi()
    {
        // Over every ending there is rather than a list that has to be kept in step with the enum: a new outcome
        // added later is covered by this the day it exists.
        foreach (var outcome in Enum.GetValues<LocalRunOutcome>())
        {
            var headline = _Result(outcome).Headline;

            // act's own documentation says its images differ from GitHub's. A sentence that could be read as "the
            // pull-request check passed" is a claim this plugin is not entitled to make, and it is exactly the
            // kind of wording that gets softened later by someone tidying up.
            Assert.DoesNotContain("CI", headline, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("green", headline, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void AVerdictSaysWhereItWasReached()
    {
        Assert.Contains("on this machine", _Result(LocalRunOutcome.Passed).Headline);
        Assert.Contains("on this machine", _Result(LocalRunOutcome.Failed).Headline);
    }

    [Fact]
    public void OnlyARunThatFinishedCountsAsAVerdict()
    {
        var reached = Enum.GetValues<LocalRunOutcome>().Where(outcome => _Result(outcome).ReachedAVerdict);

        Assert.Equal([LocalRunOutcome.Passed, LocalRunOutcome.Failed], reached);
    }

    [Fact]
    public void ADidNotRunResultCarriesNoDurationOrExitCode()
    {
        var result = LocalRunResult.DidNotRun("ci.yml", "build", LocalRunOutcome.Refused, "it uses a matrix");

        Assert.Equal(TimeSpan.Zero, result.Duration);
        Assert.Null(result.ExitCode);
        Assert.Contains("it uses a matrix", result.Headline);
    }

    private static LocalRunResult _Result(LocalRunOutcome outcome) =>
        new("ci.yml", "build", outcome, TimeSpan.FromSeconds(131), ExitCode: 0, "a reason", LogTail: string.Empty);
}

public class LocalRunTrackerTests
{
    private static readonly DateTimeOffset Noon = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NothingRunningMeansNothingInTheStatusBar() => Assert.Empty(new LocalRunTracker().Snapshot());

    [Fact]
    public void ARunningJobIsVisibleAndStoppable()
    {
        var tracker = new LocalRunTracker();
        var stopped = false;

        tracker.Begin(Path.GetTempPath(), "build", Noon, () =>
        {
            stopped = true;
            return Task.CompletedTask;
        });

        var activity = Assert.Single(tracker.Snapshot());
        Assert.Contains("build", activity.Title);

        activity.StopAsync();
        Assert.True(stopped);
    }

    [Fact]
    public void AFinishedRunLeavesTheStatusBarAndIsRememberedInstead()
    {
        var tracker = new LocalRunTracker();
        var root = Path.GetTempPath();
        tracker.Begin(root, "build", Noon, () => Task.CompletedTask);

        tracker.Complete(root, _Passed(), "abc123", Noon.AddMinutes(2));

        Assert.Empty(tracker.Snapshot());
        Assert.Equal("abc123", tracker.LastFor(root)?.Commit);
    }

    [Fact]
    public void TwoSpellingsOfOneCheckoutAreOneCheckout()
    {
        var tracker = new LocalRunTracker();
        var root = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
        tracker.Complete(root, _Passed(), "abc123", Noon);

        // The same directory reaches this from a session's working directory, a workflow's folder and an intent's
        // payload; filed under two names, the gate would look up a run that is sitting right there.
        Assert.NotNull(tracker.LastFor(root + Path.DirectorySeparatorChar));
        Assert.NotNull(tracker.LastFor(Path.Combine(root, "sub", "..")));
    }

    [Fact]
    public void ACheckoutNothingHasRunInHasNoLastRun() =>
        Assert.Null(new LocalRunTracker().LastFor(Path.GetTempPath()));

    private static LocalRunResult _Passed() =>
        new("ci.yml", "build", LocalRunOutcome.Passed, TimeSpan.FromSeconds(131), 0, null, string.Empty);
}
