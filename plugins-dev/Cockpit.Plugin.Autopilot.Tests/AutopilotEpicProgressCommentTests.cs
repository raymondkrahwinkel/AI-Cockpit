namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// AC-346's epic-progress comment text — extracted as a pure static
/// (<see cref="AutopilotPlanWorkspaceBody.BuildEpicProgressComment"/>) precisely so its actual wording is testable
/// without a UI or a tracker fake (AC-346 review, MEDIUM 8: the settle-hook previously had tests only on the building
/// blocks underneath it — the reliability summary, the record capture — never on the sentence that actually lands on
/// the epic).
/// </summary>
public class AutopilotEpicProgressCommentTests
{
    private static AutopilotReliabilitySummary Reliability(int streak = 3, int clean = 3, int considered = 3) => new(streak, clean, considered);

    [Fact]
    public void BuildEpicProgressComment_ForAMergeReadySettle_NamesTheSubAndTheReliabilityLine()
    {
        var comment = AutopilotPlanWorkspaceBody.BuildEpicProgressComment("AC-1", "AC-1 - First sub", AutopilotPlanPhase.MergeReady, null, pullRequestMissing: false, Reliability());

        Assert.Contains("AC-1", comment);
        Assert.Contains("reached a merge-ready PR", comment);
        Assert.Contains(Reliability().Describe(), comment);
    }

    [Fact]
    public void BuildEpicProgressComment_ForAMergeReadySettle_WithAMissingPullRequest_SaysSoInsteadOfClaimingSuccess()
    {
        // AC-347's own PullRequestMissing warning must survive into the epic's comment (AC-346 review, MEDIUM 8) — a
        // sub that settled MergeReady but never actually opened its PR is not "done" from the epic's point of view.
        var comment = AutopilotPlanWorkspaceBody.BuildEpicProgressComment("AC-1", "AC-1 - First sub", AutopilotPlanPhase.MergeReady, null, pullRequestMissing: true, Reliability());

        Assert.DoesNotContain("reached a merge-ready PR", comment);
        Assert.Contains("could not open its pull request", comment);
    }

    [Fact]
    public void BuildEpicProgressComment_ForABlockedSettle_NamesTheBlockReason()
    {
        var comment = AutopilotPlanWorkspaceBody.BuildEpicProgressComment("AC-2", "AC-2 - Second sub", AutopilotPlanPhase.Blocked, "a hard step failed", pullRequestMissing: false, Reliability());

        Assert.Contains("blocked", comment);
        Assert.Contains("a hard step failed", comment);
    }

    [Fact]
    public void BuildEpicProgressComment_ForAStoppedSettle_SaysTheOperatorStoppedIt()
    {
        var comment = AutopilotPlanWorkspaceBody.BuildEpicProgressComment("AC-3", "AC-3 - Third sub", AutopilotPlanPhase.Stopped, null, pullRequestMissing: false, Reliability());

        Assert.Contains("stopped by the operator", comment);
    }
}
