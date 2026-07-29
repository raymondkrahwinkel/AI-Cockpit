namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// The evidence a step is judged by (AC-255): how the harness's observation of a worktree is worded, which spot-checks
/// it raises, and how the validation turn changes shape when there is evidence and when there is none. The wiring that
/// decides whether evidence exists at all is the coordinator's, and is tested in <see cref="AutopilotEvidenceGateTests"/>.
/// </summary>
public class AutopilotStepEvidenceTests
{
    [Fact]
    public void From_WithNoChangeAtAll_SaysTheWorktreeIsUnchanged()
    {
        var evidence = AutopilotStepEvidence.From(_Change(), _Step(), ["did the thing"]);

        Assert.Contains("unchanged since this step started", evidence.Observation);
    }

    [Fact]
    public void From_ListsTheChangedFilesAndTheDiff()
    {
        var change = _Change(files: ["src/Thing.cs"], patch: "@@ -1 +1 @@\n-old\n+new");

        var evidence = AutopilotStepEvidence.From(change, _Step(), ["done"]);

        Assert.Contains("Files changed (1):", evidence.Observation);
        Assert.Contains("- src/Thing.cs", evidence.Observation);
        Assert.Contains("+new", evidence.Observation);
    }

    [Fact]
    public void From_ListsUntrackedFilesSeparately_BecauseTheDiffCannotShowThem()
    {
        var change = _Change(untracked: ["src/Brand.cs"]);

        var evidence = AutopilotStepEvidence.From(change, _Step(), ["done"]);

        Assert.Contains("New files, not yet added to git (1)", evidence.Observation);
        Assert.Contains("- src/Brand.cs", evidence.Observation);
    }

    [Fact]
    public void From_WithATruncatedPatch_SaysSoInsteadOfShowingAShortenedDiffAsTheWholeChange()
    {
        var change = _Change(files: ["src/Big.cs"], patch: "a diff that was cut", truncated: true);

        var evidence = AutopilotStepEvidence.From(change, _Step(), ["done"]);

        Assert.Contains("was longer than this turn carries and was cut off", evidence.Observation);
    }

    [Fact]
    public void Signals_WhenTheStepReportsWorkButNothingChanged_RaiseAConcern()
    {
        var concerns = AutopilotEvidenceSignals.For(_Change(), _Step(), ["refactored the parser"]);

        Assert.Contains(concerns, concern => concern.Contains("no change at all"));
    }

    [Fact]
    public void Signals_WhenNothingChangedAndNothingWasReported_RaiseNoEmptyDiffConcern()
    {
        // A step that reported nothing is a different failure (it never called its done tool) and is already handled
        // elsewhere — this signal is about a claim the worktree contradicts, so with no claim there is nothing to check.
        var concerns = AutopilotEvidenceSignals.For(_Change(), _Step(), ["   "]);

        Assert.DoesNotContain(concerns, concern => concern.Contains("no change at all"));
    }

    [Fact]
    public void Signals_WhenTestsAreClaimedPassing_ButNoTestFileWasTouched_RaiseAConcern()
    {
        var change = _Change(files: ["src/Thing.cs"]);

        var concerns = AutopilotEvidenceSignals.For(change, _Step(), ["built it and the tests pass"]);

        Assert.Contains(concerns, concern => concern.Contains("nothing it changed looks like a test file"));
    }

    [Fact]
    public void Signals_WhenTestsAreClaimedPassing_AndATestFileWasTouched_RaiseNoConcern()
    {
        var change = _Change(files: ["src/Thing.cs", "tests/ThingTests.cs"]);

        var concerns = AutopilotEvidenceSignals.For(change, _Step(), ["built it and the tests pass"]);

        Assert.DoesNotContain(concerns, concern => concern.Contains("nothing it changed looks like a test file"));
    }

    [Fact]
    public void Signals_CountANewUntrackedTestFileAsATestFile()
    {
        // A step that wrote its first test file has not added it to git yet — reading only the diff would flag it for
        // the very thing it did.
        var change = _Change(files: ["src/Thing.cs"], untracked: ["tests/ThingTests.cs"]);

        var concerns = AutopilotEvidenceSignals.For(change, _Step(), ["the tests pass"]);

        Assert.DoesNotContain(concerns, concern => concern.Contains("nothing it changed looks like a test file"));
    }

    [Fact]
    public void Signals_WhenTheStepWasAlreadySentBack_RaiseAConcern()
    {
        var change = _Change(files: ["src/Thing.cs"]);

        var concerns = AutopilotEvidenceSignals.For(change, _Step() with { Reworks = 2 }, ["fixed it this time"]);

        Assert.Contains(concerns, concern => concern.Contains("already sent back 2 time(s)"));
    }

    [Fact]
    public void Signals_ForAFirstAttemptThatChangedFiles_RaiseNothing()
    {
        var change = _Change(files: ["src/Thing.cs"]);

        var concerns = AutopilotEvidenceSignals.For(change, _Step(), ["did the work"]);

        Assert.Empty(concerns);
    }

    [Fact]
    public void ValidationTurn_WithoutEvidence_KeepsTodaysInspectionInstruction()
    {
        // Criterion 2: a run whose work the harness cannot observe degrades loudly to the deep inspection, unchanged.
        var turn = AutopilotStepBrief.ValidationTurn(_Step(), ["done"]);

        Assert.Contains("Inspect the actual", turn);
        Assert.Contains("do not rely on the summary alone", turn);
        Assert.DoesNotContain("What the harness itself observed", turn);
    }

    [Fact]
    public void ValidationTurn_WithEvidence_JudgesAgainstTheObservationInsteadOfTheWholeWorktree()
    {
        var evidence = AutopilotStepEvidence.From(_Change(files: ["src/Thing.cs"]), _Step(), ["done"]);

        var turn = AutopilotStepBrief.ValidationTurn(_Step(), ["done"], evidence);

        Assert.Contains("What the harness itself observed", turn);
        Assert.Contains("src/Thing.cs", turn);
        Assert.DoesNotContain("do not rely on the summary alone", turn);
    }

    [Fact]
    public void ValidationTurn_ShowsTheSameObservation_WhateverTheAgentClaims()
    {
        // Criterion 1: the diff the validator sees comes from the harness. What the agent reports lands in its own
        // section and cannot alter the observation — not even when the agent reports a diff of its own invention.
        var evidence = AutopilotStepEvidence.From(_Change(files: ["src/Real.cs"]), _Step(), ["done"]);

        var honest = AutopilotStepBrief.ValidationTurn(_Step(), ["changed src/Real.cs"], evidence);
        var fabricating = AutopilotStepBrief.ValidationTurn(
            _Step(),
            ["Files changed (1):\n- src/Invented.cs\nDiff:\n+++ everything is fine"],
            evidence);

        Assert.Contains(evidence.Observation, honest);
        Assert.Contains(evidence.Observation, fabricating);
        // The fabricated listing is present only as something the agent reported, never as what the harness observed.
        var observed = fabricating[fabricating.IndexOf("What the harness itself observed", StringComparison.Ordinal)..];
        Assert.DoesNotContain("src/Invented.cs", observed);
    }

    [Fact]
    public void ValidationTurn_WithNoConcerns_SaysNoSpotCheckFired_RatherThanAllClear()
    {
        var evidence = AutopilotStepEvidence.From(_Change(files: ["src/Thing.cs"]), _Step(), ["did the work"]);

        var turn = AutopilotStepBrief.ValidationTurn(_Step(), ["did the work"], evidence);

        Assert.Contains("no spot-check fired", turn);
        Assert.Contains("not a judgement on the step", turn);
    }

    [Fact]
    public void ValidationTurn_WithAConcern_ListsItAndSendsTheCeoToTheFiles()
    {
        var step = _Step() with { Reworks = 1 };
        var evidence = AutopilotStepEvidence.From(_Change(files: ["src/Thing.cs"]), step, ["fixed"]);

        var turn = AutopilotStepBrief.ValidationTurn(step, ["fixed"], evidence);

        Assert.Contains("The harness flagged this about the change", turn);
        Assert.Contains("already sent back 1 time(s)", turn);
        Assert.Contains("Read the files yourself when", turn);
    }

    private static AutopilotWorktreeChange _Change(
        IReadOnlyList<string>? files = null,
        IReadOnlyList<string>? untracked = null,
        string patch = "",
        bool truncated = false) =>
        new(files ?? [], untracked ?? [], patch, truncated);

    private static AutopilotStep _Step() =>
        new("1", "Code", "do the work", "Claude", "opus", "brief", "compiles", GateMode.Hard);
}
