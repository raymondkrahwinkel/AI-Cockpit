namespace Cockpit.Plugin.Autopilot.Tests;

// AC-346's epic-progress comment text — extracted as a pure static so its wording is testable without a UI or
// tracker fake (AC-346 review, MEDIUM 8: previously only the building blocks underneath it were tested,
// never the sentence that actually lands on the epic).
public class AutopilotEpicProgressCommentTests
{
    private static AutopilotReliabilitySummary Reliability() => new(3, 3, 3);

    // The phase is an internal enum, so the rows box it and the test casts back once.
    public static IEnumerable<object[]> Settles() =>
    [
        // A merge-ready sub names itself, says it got there, and carries the run's reliability line.
        [
            "AC-1", AutopilotPlanPhase.MergeReady, null!, false,
            new[] { "AC-1", "reached a merge-ready PR", "3 in a row without a correction · 3 of the last 3" },
            Array.Empty<string>(),
        ],
        // AC-347's own PullRequestMissing warning must survive into the epic's comment (AC-346 review, MEDIUM 8) — a
        // sub that settled MergeReady but never actually opened its PR is not "done" from the epic's point of view.
        [
            "AC-1", AutopilotPlanPhase.MergeReady, null!, true,
            new[] { "could not open its pull request" }, new[] { "reached a merge-ready PR" },
        ],
        ["AC-2", AutopilotPlanPhase.Blocked, "a hard step failed", false, new[] { "blocked", "a hard step failed" }, Array.Empty<string>()],
        ["AC-3", AutopilotPlanPhase.Stopped, null!, false, new[] { "stopped by the operator" }, Array.Empty<string>()],
    ];

    [Theory]
    [MemberData(nameof(Settles))]
    public void BuildEpicProgressComment_SaysHowTheSubActuallyEnded(
        string issueId, object outcome, string? blockReason, bool pullRequestMissing, string[] present, string[] absent)
    {
        var comment = AutopilotPlanWorkspaceBody.BuildEpicProgressComment(
            issueId, $"{issueId} - a sub", (AutopilotPlanPhase)outcome, blockReason, pullRequestMissing, Reliability());

        Assert.All(present, fragment => Assert.Contains(fragment, comment));
        Assert.All(absent, fragment => Assert.DoesNotContain(fragment, comment));
    }
}
