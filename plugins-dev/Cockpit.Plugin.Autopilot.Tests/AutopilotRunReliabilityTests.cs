namespace Cockpit.Plugin.Autopilot.Tests;

// The AC-347 reliability aggregator: a run only ran clean when it reached merge-ready with no step needing a
// correction; the streak counts back from the newest run until the first that was not clean, within the same window
// `AutopilotReliabilitySummary.ConsideredRuns` reports.
public class AutopilotRunReliabilityTests
{
    private static AutopilotRunStepRecord Step(AutopilotCorrectionKind correction = AutopilotCorrectionKind.None) =>
        new("Step", AutopilotStepStatus.Passed, string.Empty) { Correction = correction };

    private static AutopilotRunRecord Record(string name, AutopilotPlanPhase outcome, params AutopilotRunStepRecord[] steps) =>
        new(name, $"goal for {name}", outcome, outcome == AutopilotPlanPhase.Blocked ? "a hard step failed" : null, "2026-07-28T00:00:00+00:00", steps);

    private static AutopilotRunRecord Clean(string name) => Record(name, AutopilotPlanPhase.MergeReady, Step());

    private static AutopilotRunRecord Blocked(string name) => Record(name, AutopilotPlanPhase.Blocked, Step());

    // The three ways a settled run fails to be clean, and the one way it succeeds. The phases and correction kinds are
    // internal enums, so the rows box them and the test casts back — each case still discovers under its own enum name.
    public static IEnumerable<object[]> SettledRuns() =>
    [
        [AutopilotPlanPhase.MergeReady, AutopilotCorrectionKind.None, false, true],
        [AutopilotPlanPhase.MergeReady, AutopilotCorrectionKind.ReviewFinding, false, false],
        [AutopilotPlanPhase.Blocked, AutopilotCorrectionKind.None, false, false],
        [AutopilotPlanPhase.Stopped, AutopilotCorrectionKind.None, false, false],
        // AC-347: every step ran clean, but the run could not open its PR — it still needs a human, so not clean.
        [AutopilotPlanPhase.MergeReady, AutopilotCorrectionKind.None, true, false],
    ];

    [Theory]
    [MemberData(nameof(SettledRuns))]
    public void RanClean_JudgesASettledRun(object outcome, object correction, bool pullRequestMissing, bool expected)
    {
        var record = Record("a", (AutopilotPlanPhase)outcome, Step((AutopilotCorrectionKind)correction))
            with { PullRequestMissing = pullRequestMissing };

        Assert.Equal(expected, AutopilotRunReliability.RanClean(record));
    }

    [Fact]
    public void RanClean_MergeReady_WithOneCorrectedStepAmongSeveralClean_IsFalse()
    {
        // Every fixture above has exactly one step, so All(...) and Any(...) agree by accident. Two steps — one clean,
        // one not — is the only way to tell them apart: All requires every step clean (correct, false here); Any would
        // already be satisfied by the one clean step and wrongly report true.
        var record = Record("a", AutopilotPlanPhase.MergeReady, Step(AutopilotCorrectionKind.ReviewFinding), Step());

        Assert.False(AutopilotRunReliability.RanClean(record));
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

    [Theory]
    // A run outside the window from the front cannot affect it: the whole window is clean, so streak, clean count and
    // considered count all equal the window.
    [InlineData(10, 5, 5, 5, 5)]
    // A run inside the window bounds all three: the streak stops at it, and it is the one non-clean run counted.
    [InlineData(3, 10, 3, 9, 10)]
    public void Summarize_ConsidersOnlyTheNewestWindow(int blockedIndex, int window, int streak, int cleanRuns, int consideredRuns)
    {
        var records = Enumerable.Range(0, 25).Select(i => Clean($"run-{i}")).ToList();
        records[blockedIndex] = Blocked($"run-{blockedIndex}");

        var summary = AutopilotRunReliability.Summarize(records, window);

        Assert.Equal(streak, summary.Streak);
        Assert.Equal(cleanRuns, summary.CleanRuns);
        Assert.Equal(consideredRuns, summary.ConsideredRuns);
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
