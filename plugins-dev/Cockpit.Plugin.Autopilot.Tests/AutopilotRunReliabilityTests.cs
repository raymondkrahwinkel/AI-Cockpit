namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// The AC-347 reliability aggregator: a run only ran clean when it reached merge-ready with no step needing a
/// correction; the streak counts back from the newest run until the first that was not clean, within the same window
/// <see cref="AutopilotReliabilitySummary.ConsideredRuns"/> reports.
/// </summary>
public class AutopilotRunReliabilityTests
{
    private static AutopilotRunStepRecord Step(AutopilotCorrectionKind correction = AutopilotCorrectionKind.None) =>
        new("Step", AutopilotStepStatus.Passed, string.Empty) { Correction = correction };

    private static AutopilotRunRecord Record(string name, AutopilotPlanPhase outcome, params AutopilotRunStepRecord[] steps) =>
        new(name, $"goal for {name}", outcome, outcome == AutopilotPlanPhase.Blocked ? "a hard step failed" : null, "2026-07-28T00:00:00+00:00", steps);

    private static AutopilotRunRecord Clean(string name) => Record(name, AutopilotPlanPhase.MergeReady, Step());

    private static AutopilotRunRecord CorrectedButMergeReady(string name) =>
        Record(name, AutopilotPlanPhase.MergeReady, Step(AutopilotCorrectionKind.ReviewFinding));

    private static AutopilotRunRecord Blocked(string name) => Record(name, AutopilotPlanPhase.Blocked, Step());

    private static AutopilotRunRecord Stopped(string name) => Record(name, AutopilotPlanPhase.Stopped, Step());

    private static AutopilotRunRecord MergeReadyWithoutPullRequest(string name) =>
        Record(name, AutopilotPlanPhase.MergeReady, Step()) with { PullRequestMissing = true };

    [Fact]
    public void RanClean_MergeReady_WithNoCorrection_IsTrue()
    {
        Assert.True(AutopilotRunReliability.RanClean(Clean("a")));
    }

    [Fact]
    public void RanClean_MergeReady_WithACorrection_IsFalse()
    {
        Assert.False(AutopilotRunReliability.RanClean(CorrectedButMergeReady("a")));
    }

    [Fact]
    public void RanClean_Blocked_IsFalse()
    {
        Assert.False(AutopilotRunReliability.RanClean(Blocked("a")));
    }

    [Fact]
    public void RanClean_Stopped_IsFalse()
    {
        Assert.False(AutopilotRunReliability.RanClean(Stopped("a")));
    }

    [Fact]
    public void RanClean_MergeReady_ButPullRequestMissing_IsFalse()
    {
        // AC-347: every step ran clean, but the run could not open its PR — it still needs a human, so it is not clean.
        Assert.False(AutopilotRunReliability.RanClean(MergeReadyWithoutPullRequest("a")));
    }

    [Fact]
    public void Summarize_PullRequestMissing_BreaksTheStreak()
    {
        var records = new[] { Clean("newest"), MergeReadyWithoutPullRequest("second"), Clean("oldest") };

        var summary = AutopilotRunReliability.Summarize(records);

        Assert.Equal(1, summary.Streak);
        Assert.Equal(2, summary.CleanRuns);
        Assert.Equal(3, summary.ConsideredRuns);
    }

    [Fact]
    public void Summarize_AllClean_StreakEqualsConsideredRuns()
    {
        var records = new[] { Clean("newest"), Clean("middle"), Clean("oldest") };

        var summary = AutopilotRunReliability.Summarize(records);

        Assert.Equal(3, summary.Streak);
        Assert.Equal(3, summary.CleanRuns);
        Assert.Equal(3, summary.ConsideredRuns);
    }

    [Fact]
    public void Summarize_StreakStops_AtTheFirstNonCleanRun_NewestFirst()
    {
        var records = new[] { Clean("newest"), Clean("second"), Blocked("third"), Clean("older-still") };

        var summary = AutopilotRunReliability.Summarize(records);

        Assert.Equal(2, summary.Streak);
        Assert.Equal(3, summary.CleanRuns);
        Assert.Equal(4, summary.ConsideredRuns);
    }

    [Fact]
    public void Summarize_MoreRunsThanTheWindow_ConsidersOnlyTheNewestWindow()
    {
        var records = Enumerable.Range(0, 25).Select(i => Clean($"run-{i}")).ToList();
        records[10] = Blocked("run-10"); // outside a 5-run window from the front, so it must not affect that window

        var summary = AutopilotRunReliability.Summarize(records, window: 5);

        Assert.Equal(5, summary.Streak);
        Assert.Equal(5, summary.CleanRuns);
        Assert.Equal(5, summary.ConsideredRuns);
    }

    [Fact]
    public void Summarize_TheNonCleanRunWithinTheWindow_BoundsTheStreakAndTheCount()
    {
        var records = Enumerable.Range(0, 25).Select(i => Clean($"run-{i}")).ToList();
        records[3] = Blocked("run-3"); // inside a 10-run window from the front

        var summary = AutopilotRunReliability.Summarize(records, window: 10);

        Assert.Equal(3, summary.Streak);
        Assert.Equal(9, summary.CleanRuns);
        Assert.Equal(10, summary.ConsideredRuns);
    }

    [Fact]
    public void Summarize_EmptyHistory_DescribeSaysNoSettledRunsYet()
    {
        var summary = AutopilotRunReliability.Summarize([]);

        Assert.Equal(0, summary.ConsideredRuns);
        Assert.Equal("No settled runs yet", summary.Describe());
    }

    [Fact]
    public void Describe_WithSettledRuns_ReportsTheExpectedLine()
    {
        var summary = new AutopilotReliabilitySummary(Streak: 4, CleanRuns: 6, ConsideredRuns: 8);

        Assert.Equal("4 in a row without a correction · 6 of the last 8", summary.Describe());
    }
}
