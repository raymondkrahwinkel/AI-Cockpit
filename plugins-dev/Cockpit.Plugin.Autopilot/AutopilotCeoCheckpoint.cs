namespace Cockpit.Plugin.Autopilot;

// AC-253: what a run's replacement validator CEO is briefed with when its context is checkpointed — one line per
// settled step, so later steps still have the verdicts to reason about without the diffs behind them. A pure builder,
// so the wording is tested without a live session.
internal static class AutopilotCeoCheckpoint
{
    // Whether a validator carrying `validationsSinceCheckpoint` turns is due to be replaced on an interval of
    // `everySteps`, where 0 turns checkpointing off — the operator's way to measure a run without it (AC-251).
    public static bool IsDue(int validationsSinceCheckpoint, int everySteps) =>
        everySteps > 0 && validationsSinceCheckpoint >= everySteps;

    // The ledger of what the previous validator already judged: one line per settled step, and never a pending or
    // running one — a step nobody has judged has no verdict to carry over.
    public static string CarryOver(AutopilotPlan plan)
    {
        var settled = plan.Steps.Where(step => _Verdict(step.Status) is not null).ToList();
        var ledger = settled.Count == 0
            ? "- (none yet)"
            : string.Join("\n", settled.Select(step => $"- {step.Title}: {_Verdict(step.Status)}{_Because(step.Note)}"));

        // The step's own account of what it changed is deliberately not summarised back in: saying "their diffs are
        // gone" while quietly keeping a paraphrase would be the same growing tail under another name.
        // AC-1051: normalise CRLF to '\n' so the carried-over text doesn't depend on how the plugin was checked out.
        return $"""
            Steps of this run that were already validated, and how they settled:
            {ledger}

            Their full diffs, test output and validation turns are NOT in this conversation: this session replaced the
            one that judged them, and the lines above are all that carried over. When a later step's acceptance turns on
            what an earlier one actually did, read it from your working directory or its git history rather than
            assuming it — "I cannot see it here" is not the same claim as "it was not done".
            """.ReplaceLineEndings("\n");
    }

    // How a settled step reads in the ledger, or null for one nobody has judged yet (pending, running, blocked).
    private static string? _Verdict(AutopilotStepStatus status) => status switch
    {
        AutopilotStepStatus.Passed => "done, verified",
        AutopilotStepStatus.Failed => "not accepted",
        AutopilotStepStatus.Skipped => "skipped",
        _ => null,
    };

    // The CEO's own one-line reason for that verdict, flattened to a single line and cut short — a step's note can
    // hold a multi-line failure message, and the whole point of the ledger is that it stays one line per step.
    private static string _Because(string note)
    {
        var flattened = string.Join(" ", note.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (flattened.Length == 0)
        {
            return string.Empty;
        }

        return flattened.Length <= NoteLimit ? $" — {flattened}" : $" — {flattened[..NoteLimit]}…";
    }

    private const int NoteLimit = 160;
}
