namespace Cockpit.Plugin.Autopilot.Tests;

// The template-driven merge-ready PR decision (AC-216) and its preflight (AC-215): a code run (the template asked for a
// PR) delivers one when it can, degrades fail-soft when it cannot (no git run, no remote, no gh), and an administrative
// run reports nothing. The delivery travels as object (CS0051), so the rows keep it named, not numbered.
public class AutopilotMergeReadyDecisionTests
{
    [Theory]
    // An admin run: no PR expected, so the environment never matters — it must never report a missing-PR fault.
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    public void NoPrExpected_IsNotExpected_WhateverTheEnvironment(bool isGitRun, bool hasRemote, bool ghAvailable) =>
        Assert.Equal(
            AutopilotPrDelivery.NotExpected,
            AutopilotMergeReadyDecision.Decide(deliversPullRequest: false, isGitRun, hasRemote, ghAvailable));

    public static IEnumerable<object[]> CodeRunEnvironments() =>
    [
        [false, false, false, AutopilotPrDelivery.NoGitRun],
        [true, false, true, AutopilotPrDelivery.NoRemote],
        [true, true, false, AutopilotPrDelivery.PushOnly],
        [true, true, true, AutopilotPrDelivery.CanCreatePr],
    ];

    [Theory]
    [MemberData(nameof(CodeRunEnvironments))]
    public void CodeRun_DeliversAsFarAsTheEnvironmentAllows(bool isGitRun, bool hasRemote, bool ghAvailable, object expected) =>
        Assert.Equal(
            (AutopilotPrDelivery)expected,
            AutopilotMergeReadyDecision.Decide(deliversPullRequest: true, isGitRun, hasRemote, ghAvailable));

    public static IEnumerable<object[]> PreflightCases() =>
    [
        // Every case that cannot fully deliver warns up front.
        [AutopilotPrDelivery.NoGitRun, true],
        [AutopilotPrDelivery.NoRemote, true],
        [AutopilotPrDelivery.PushOnly, true],
        // Nothing to warn about: an admin run, or a run that can open its PR.
        [AutopilotPrDelivery.NotExpected, false],
        [AutopilotPrDelivery.CanCreatePr, false],
    ];

    [Theory]
    [MemberData(nameof(PreflightCases))]
    public void PreflightWarning_SpeaksExactlyWhenTheRunCannotFullyDeliver(object delivery, bool warns) =>
        Assert.Equal(
            warns,
            !string.IsNullOrWhiteSpace(AutopilotMergeReadyDecision.PreflightWarning((AutopilotPrDelivery)delivery)));

    // The settle sentence per delivery. Compared against the lower-cased outcome throughout, so a row states its
    // fragments once rather than half of them twice.
    public static IEnumerable<object[]> Outcomes() =>
    [
        // A plain settle: merge-ready, and no missing-PR fault reported anywhere in it.
        [AutopilotPrDelivery.NotExpected, null!, null!, null!, new[] { "merge-ready" }, new[] { "no pull request" }],
        // Nowhere to push to: name where the work is, so it does not evaporate with the worktree.
        [
            AutopilotPrDelivery.NoRemote, "ac-216-fix", "/tmp/wt", null!,
            new[] { "ac-216-fix", "/tmp/wt", "no pull request could be created" }, Array.Empty<string>(),
        ],
        // Pushed, but no gh to open the PR with — the operator finishes it.
        [
            AutopilotPrDelivery.PushOnly, "ac-216-fix", "/tmp/wt", null!,
            new[] { "ac-216-fix", "open the pull request yourself" }, Array.Empty<string>(),
        ],
        [
            AutopilotPrDelivery.CanCreatePr, "ac-216-fix", "/tmp/wt", "https://github.com/o/r/pull/7",
            new[] { "https://github.com/o/r/pull/7", "pull request opened" }, Array.Empty<string>(),
        ],
        // gh was available but opening the PR failed at the last step — the branch is pushed, so point at it.
        [
            AutopilotPrDelivery.CanCreatePr, "ac-216-fix", "/tmp/wt", null!,
            new[] { "open it yourself" }, Array.Empty<string>(),
        ],
    ];

    [Theory]
    [MemberData(nameof(Outcomes))]
    public void Outcome_SaysWhatWasDelivered_AndWhereTheRestOfItIs(
        object delivery, string? branch, string? worktreePath, string? prUrl, string[] present, string[] absent)
    {
        var outcome = AutopilotMergeReadyDecision.Outcome((AutopilotPrDelivery)delivery, branch, worktreePath, prUrl)
            .ToLowerInvariant();

        Assert.All(present, fragment => Assert.Contains(fragment, outcome));
        Assert.All(absent, fragment => Assert.DoesNotContain(fragment, outcome));
    }
}
