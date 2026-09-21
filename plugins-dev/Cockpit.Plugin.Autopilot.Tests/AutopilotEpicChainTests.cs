namespace Cockpit.Plugin.Autopilot.Tests;

// AC-1340: after a settled epic sub the chain starts the next Ready sub itself, strictly one at a time, or stops out
// loud — one row per stop reason the ticket names, plus the rows where it goes on. Fakes are delegates, in the style
// of AutopilotRunDriverTests: no git, no tracker, no UI.
public class AutopilotEpicChainTests
{
    private const string BlockReason = "Required step(s) did not pass: Build.";

    private static readonly AutopilotMergeResult Green = new(true, "pr merge", "tip-green", 0, string.Empty, null);
    private static readonly AutopilotMergeResult Red = new(true, "pr merge", "tip-red", 1, string.Empty, null);
    private static readonly AutopilotMergeResult NoVerdict = new(true, "pr merge", "tip-unknown", null, string.Empty, null);
    private static readonly AutopilotMergeResult NotLanded = new(false, "pr merge", null, null, string.Empty, "the rebase hit a conflict");

    private static readonly AutopilotEpicOutcome NextReady =
        AutopilotEpicOutcome.Ready(new AutopilotRun("youtrack", "AC-2", "Second", "Ready", new Dictionary<string, string>()));

    private static readonly AutopilotEpicOutcome NextPaused = AutopilotEpicOutcome.Paused("AC-2", "AC-2 is on Backlog, not Ready");

    private static AutopilotRunRecord Settled(AutopilotPlanPhase outcome, AutopilotStepStatus status, AutopilotCorrectionKind correction) =>
        new("run", "goal", outcome, BlockReason, "2026-09-21T00:00:00+00:00", [new AutopilotRunStepRecord("Build", status, string.Empty) { Correction = correction }])
        {
            Ticket = "AC-1",
            EpicId = "AC-EPIC",
        };

    // Columns: how the run ended (phase, its one step's status and correction), stranded commits, the gate's landing,
    // the AC-347 tolerance, what resolving the epic answers, whether planning accepts the sub — then how many planning
    // starts are expected, whether the chain went on, and the fragment both the epic comment and the note must carry.
    public static IEnumerable<object?[]> SettledSubs() =>
    [
        [AutopilotPlanPhase.Blocked, AutopilotStepStatus.Failed, AutopilotCorrectionKind.None, false, Green, 0, NextReady, true, 0, false, "the run blocked"],
        [AutopilotPlanPhase.Stopped, AutopilotStepStatus.Passed, AutopilotCorrectionKind.None, false, Green, 0, NextReady, true, 0, false, "stopped by the operator"],
        // A skippable gate out of attempts settles Failed inside a run that still reads merge-ready.
        [AutopilotPlanPhase.MergeReady, AutopilotStepStatus.Failed, AutopilotCorrectionKind.None, false, Green, 0, NextReady, true, 0, false, "ran out of attempts (Build)"],
        [AutopilotPlanPhase.MergeReady, AutopilotStepStatus.Passed, AutopilotCorrectionKind.None, true, Green, 0, NextReady, true, 0, false, "stranded"],
        [AutopilotPlanPhase.MergeReady, AutopilotStepStatus.Passed, AutopilotCorrectionKind.None, false, null, 0, NextReady, true, 0, false, "nothing was merged"],
        [AutopilotPlanPhase.MergeReady, AutopilotStepStatus.Passed, AutopilotCorrectionKind.None, false, NotLanded, 0, NextReady, true, 0, false, "did not land (the rebase hit a conflict)"],
        [AutopilotPlanPhase.MergeReady, AutopilotStepStatus.Passed, AutopilotCorrectionKind.None, false, Red, 0, NextReady, true, 0, false, "exited 1"],
        [AutopilotPlanPhase.MergeReady, AutopilotStepStatus.Passed, AutopilotCorrectionKind.None, false, NoVerdict, 0, NextReady, true, 0, false, "exited with no verdict"],
        // AC-347: a corrected run is not clean — above the tolerance it stops the chain, within it the chain goes on.
        [AutopilotPlanPhase.MergeReady, AutopilotStepStatus.Passed, AutopilotCorrectionKind.ReviewFinding, false, Green, 0, NextReady, true, 0, false, "did not run clean, above the tolerance of 0"],
        [AutopilotPlanPhase.MergeReady, AutopilotStepStatus.Passed, AutopilotCorrectionKind.ReviewFinding, false, Green, 1, NextReady, true, 1, true, "chained to AC-2"],
        // Resolving the epic again: paused, done, not an epic, planning refused — and the one row that chains.
        [AutopilotPlanPhase.MergeReady, AutopilotStepStatus.Passed, AutopilotCorrectionKind.None, false, Green, 0, NextPaused, true, 0, false, "AC-2 is on Backlog, not Ready"],
        [AutopilotPlanPhase.MergeReady, AutopilotStepStatus.Passed, AutopilotCorrectionKind.None, false, Green, 0, AutopilotEpicOutcome.Complete, true, 0, false, "every sub is merged"],
        [AutopilotPlanPhase.MergeReady, AutopilotStepStatus.Passed, AutopilotCorrectionKind.None, false, Green, 0, AutopilotEpicOutcome.NotEpic, true, 0, false, "no subtasks found"],
        [AutopilotPlanPhase.MergeReady, AutopilotStepStatus.Passed, AutopilotCorrectionKind.None, false, Green, 0, NextReady, false, 1, false, "planning round is already open"],
        [AutopilotPlanPhase.MergeReady, AutopilotStepStatus.Passed, AutopilotCorrectionKind.None, false, Green, 0, NextReady, true, 1, true, "chained to AC-2"],
    ];

    [Theory]
    [MemberData(nameof(SettledSubs))]
    public async Task ContinueAsync_AfterASettledSub_StartsTheNextOrStopsOutLoud(
        object outcome,
        object stepStatus,
        object correction,
        bool strandedCommits,
        object? merge,
        int tolerance,
        object resolved,
        bool planningAccepts,
        int expectedPlanningStarts,
        bool expectedChained,
        string expectedFragment)
    {
        var started = new List<string>();
        var comments = new List<string>();
        var notes = new List<string>();
        var chain = new AutopilotEpicChain(
            _ => Task.FromResult((AutopilotEpicOutcome)resolved),
            run =>
            {
                started.Add(run.IssueId);
                return Task.FromResult(planningAccepts);
            },
            (text, _) =>
            {
                comments.Add(text);
                return Task.CompletedTask;
            },
            text =>
            {
                notes.Add(text);
                return Task.FromResult(true);
            },
            tolerance);
        var settled = Settled((AutopilotPlanPhase)outcome, (AutopilotStepStatus)stepStatus, (AutopilotCorrectionKind)correction);

        var stop = await chain.ContinueAsync(settled, [settled], (AutopilotMergeResult?)merge, strandedCommits);

        Assert.Equal(expectedChained, stop is null);
        Assert.Equal(expectedPlanningStarts, started.Count);
        Assert.All(started, issueId => Assert.Equal("AC-2", issueId));
        Assert.Contains(expectedFragment, Assert.Single(comments));
        Assert.Contains(expectedFragment, Assert.Single(notes));
    }
}
