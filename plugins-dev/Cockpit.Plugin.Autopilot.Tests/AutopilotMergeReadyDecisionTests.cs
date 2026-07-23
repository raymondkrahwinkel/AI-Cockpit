using FluentAssertions;

namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// The template-driven merge-ready PR decision (AC-216) and its preflight (AC-215): a code run (the template asked for a
/// PR) delivers one when it can, degrades fail-soft when it cannot (no git run, no remote, no gh), and an administrative
/// run reports nothing. Pure, so the outcome/fallback is proven here without a git repo, a live run or the network.
/// </summary>
public class AutopilotMergeReadyDecisionTests
{
    [Fact]
    public void NoPrExpected_IsNotExpected_WhateverTheEnvironment()
    {
        // An admin run: no PR expected, so the environment never matters — it must never report a missing-PR fault.
        AutopilotMergeReadyDecision.Decide(deliversPullRequest: false, isGitRun: true, hasRemote: true, ghAvailable: true)
            .Should().Be(AutopilotPrDelivery.NotExpected);
        AutopilotMergeReadyDecision.Decide(deliversPullRequest: false, isGitRun: false, hasRemote: false, ghAvailable: false)
            .Should().Be(AutopilotPrDelivery.NotExpected);
    }

    [Fact]
    public void CodeRun_NotAGitRun_CannotDeliver()
    {
        AutopilotMergeReadyDecision.Decide(deliversPullRequest: true, isGitRun: false, hasRemote: false, ghAvailable: false)
            .Should().Be(AutopilotPrDelivery.NoGitRun);
    }

    [Fact]
    public void CodeRun_GitRun_NoRemote_CannotDeliver()
    {
        AutopilotMergeReadyDecision.Decide(deliversPullRequest: true, isGitRun: true, hasRemote: false, ghAvailable: true)
            .Should().Be(AutopilotPrDelivery.NoRemote);
    }

    [Fact]
    public void CodeRun_RemoteButNoGh_PushesOnly()
    {
        AutopilotMergeReadyDecision.Decide(deliversPullRequest: true, isGitRun: true, hasRemote: true, ghAvailable: false)
            .Should().Be(AutopilotPrDelivery.PushOnly);
    }

    [Fact]
    public void CodeRun_RemoteAndGh_CanCreatePr()
    {
        AutopilotMergeReadyDecision.Decide(deliversPullRequest: true, isGitRun: true, hasRemote: true, ghAvailable: true)
            .Should().Be(AutopilotPrDelivery.CanCreatePr);
    }

    [Fact]
    public void PreflightWarning_FlagsEveryCannotFullyDeliverCase()
    {
        AutopilotMergeReadyDecision.PreflightWarning(AutopilotPrDelivery.NoGitRun).Should().NotBeNullOrWhiteSpace();
        AutopilotMergeReadyDecision.PreflightWarning(AutopilotPrDelivery.NoRemote).Should().NotBeNullOrWhiteSpace();
        AutopilotMergeReadyDecision.PreflightWarning(AutopilotPrDelivery.PushOnly).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void PreflightWarning_IsSilentWhenNothingToWarnAbout()
    {
        // Nothing to warn: an admin run, or a run that can open its PR — no up-front warning.
        AutopilotMergeReadyDecision.PreflightWarning(AutopilotPrDelivery.NotExpected).Should().BeNull();
        AutopilotMergeReadyDecision.PreflightWarning(AutopilotPrDelivery.CanCreatePr).Should().BeNull();
    }

    [Fact]
    public void Outcome_NotExpected_IsAPlainSettle_NoMissingPrFault()
    {
        var outcome = AutopilotMergeReadyDecision.Outcome(AutopilotPrDelivery.NotExpected, branch: null, worktreePath: null, prUrl: null);
        outcome.Should().Contain("merge-ready");
        outcome.ToLowerInvariant().Should().NotContain("no pull request");
    }

    [Fact]
    public void Outcome_NoRemote_NamesWhereTheWorkIs_SoItDoesNotEvaporate()
    {
        var outcome = AutopilotMergeReadyDecision.Outcome(AutopilotPrDelivery.NoRemote, "ac-216-fix", "/tmp/wt", prUrl: null);
        outcome.Should().Contain("ac-216-fix");
        outcome.Should().Contain("/tmp/wt");
        outcome.Should().Contain("no pull request could be created");
    }

    [Fact]
    public void Outcome_PushOnly_TellsOperatorToOpenThePrThemselves()
    {
        var outcome = AutopilotMergeReadyDecision.Outcome(AutopilotPrDelivery.PushOnly, "ac-216-fix", "/tmp/wt", prUrl: null);
        outcome.Should().Contain("ac-216-fix");
        outcome.ToLowerInvariant().Should().Contain("open the pull request yourself");
    }

    [Fact]
    public void Outcome_CanCreatePr_WithUrl_ReportsThePr()
    {
        var outcome = AutopilotMergeReadyDecision.Outcome(AutopilotPrDelivery.CanCreatePr, "ac-216-fix", "/tmp/wt", "https://github.com/o/r/pull/7");
        outcome.Should().Contain("https://github.com/o/r/pull/7");
        outcome.Should().Contain("pull request opened");
    }

    [Fact]
    public void Outcome_CanCreatePr_WithoutUrl_FallsBackToOpenItYourself()
    {
        // gh was available but opening the PR failed at the last step — the branch is pushed, so point the operator at it.
        var outcome = AutopilotMergeReadyDecision.Outcome(AutopilotPrDelivery.CanCreatePr, "ac-216-fix", "/tmp/wt", prUrl: null);
        outcome.ToLowerInvariant().Should().Contain("open it yourself");
    }
}
