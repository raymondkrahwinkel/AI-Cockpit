namespace Cockpit.Plugin.Autopilot.Tests;

// The template-driven merge-ready PR decision (AC-216) and its preflight (AC-215): a code run (the template asked for a
// PR) delivers one when it can, degrades fail-soft when it cannot (no git run, no remote, no gh), and an administrative
// run reports nothing. Pure, so the outcome/fallback is proven here without a git repo, a live run or the network.
public class AutopilotMergeReadyDecisionTests
{
    [Fact]
    public void NoPrExpected_IsNotExpected_WhateverTheEnvironment()
    {
        // An admin run: no PR expected, so the environment never matters — it must never report a missing-PR fault.
        Assert.Equal(AutopilotPrDelivery.NotExpected, AutopilotMergeReadyDecision.Decide(deliversPullRequest: false, isGitRun: true, hasRemote: true, ghAvailable: true));
        Assert.Equal(AutopilotPrDelivery.NotExpected, AutopilotMergeReadyDecision.Decide(deliversPullRequest: false, isGitRun: false, hasRemote: false, ghAvailable: false));
    }

    [Fact]
    public void CodeRun_NotAGitRun_CannotDeliver()
    {
        Assert.Equal(AutopilotPrDelivery.NoGitRun, AutopilotMergeReadyDecision.Decide(deliversPullRequest: true, isGitRun: false, hasRemote: false, ghAvailable: false));
    }

    [Fact]
    public void CodeRun_GitRun_NoRemote_CannotDeliver()
    {
        Assert.Equal(AutopilotPrDelivery.NoRemote, AutopilotMergeReadyDecision.Decide(deliversPullRequest: true, isGitRun: true, hasRemote: false, ghAvailable: true));
    }

    [Fact]
    public void CodeRun_RemoteButNoGh_PushesOnly()
    {
        Assert.Equal(AutopilotPrDelivery.PushOnly, AutopilotMergeReadyDecision.Decide(deliversPullRequest: true, isGitRun: true, hasRemote: true, ghAvailable: false));
    }

    [Fact]
    public void CodeRun_RemoteAndGh_CanCreatePr()
    {
        Assert.Equal(AutopilotPrDelivery.CanCreatePr, AutopilotMergeReadyDecision.Decide(deliversPullRequest: true, isGitRun: true, hasRemote: true, ghAvailable: true));
    }

    [Fact]
    public void PreflightWarning_FlagsEveryCannotFullyDeliverCase()
    {
        Assert.False(string.IsNullOrWhiteSpace(AutopilotMergeReadyDecision.PreflightWarning(AutopilotPrDelivery.NoGitRun)));
        Assert.False(string.IsNullOrWhiteSpace(AutopilotMergeReadyDecision.PreflightWarning(AutopilotPrDelivery.NoRemote)));
        Assert.False(string.IsNullOrWhiteSpace(AutopilotMergeReadyDecision.PreflightWarning(AutopilotPrDelivery.PushOnly)));
    }

    [Fact]
    public void PreflightWarning_IsSilentWhenNothingToWarnAbout()
    {
        // Nothing to warn: an admin run, or a run that can open its PR — no up-front warning.
        Assert.Null(AutopilotMergeReadyDecision.PreflightWarning(AutopilotPrDelivery.NotExpected));
        Assert.Null(AutopilotMergeReadyDecision.PreflightWarning(AutopilotPrDelivery.CanCreatePr));
    }

    [Fact]
    public void Outcome_NotExpected_IsAPlainSettle_NoMissingPrFault()
    {
        var outcome = AutopilotMergeReadyDecision.Outcome(AutopilotPrDelivery.NotExpected, branch: null, worktreePath: null, prUrl: null);
        Assert.Contains("merge-ready", outcome);
        Assert.DoesNotContain("no pull request", outcome.ToLowerInvariant());
    }

    [Fact]
    public void Outcome_NoRemote_NamesWhereTheWorkIs_SoItDoesNotEvaporate()
    {
        var outcome = AutopilotMergeReadyDecision.Outcome(AutopilotPrDelivery.NoRemote, "ac-216-fix", "/tmp/wt", prUrl: null);
        Assert.Contains("ac-216-fix", outcome);
        Assert.Contains("/tmp/wt", outcome);
        Assert.Contains("no pull request could be created", outcome);
    }

    [Fact]
    public void Outcome_PushOnly_TellsOperatorToOpenThePrThemselves()
    {
        var outcome = AutopilotMergeReadyDecision.Outcome(AutopilotPrDelivery.PushOnly, "ac-216-fix", "/tmp/wt", prUrl: null);
        Assert.Contains("ac-216-fix", outcome);
        Assert.Contains("open the pull request yourself", outcome.ToLowerInvariant());
    }

    [Fact]
    public void Outcome_CanCreatePr_WithUrl_ReportsThePr()
    {
        var outcome = AutopilotMergeReadyDecision.Outcome(AutopilotPrDelivery.CanCreatePr, "ac-216-fix", "/tmp/wt", "https://github.com/o/r/pull/7");
        Assert.Contains("https://github.com/o/r/pull/7", outcome);
        Assert.Contains("pull request opened", outcome);
    }

    [Fact]
    public void Outcome_CanCreatePr_WithoutUrl_FallsBackToOpenItYourself()
    {
        // gh was available but opening the PR failed at the last step — the branch is pushed, so point the operator at it.
        var outcome = AutopilotMergeReadyDecision.Outcome(AutopilotPrDelivery.CanCreatePr, "ac-216-fix", "/tmp/wt", prUrl: null);
        Assert.Contains("open it yourself", outcome.ToLowerInvariant());
    }
}
