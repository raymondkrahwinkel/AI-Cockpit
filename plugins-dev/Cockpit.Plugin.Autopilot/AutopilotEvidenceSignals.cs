namespace Cockpit.Plugin.Autopilot;

// The targeted spot-checks the harness computes for itself (AC-255) — chosen over deeply validating a fixed
// percentage of steps, which was the other candidate for this gate. The failure it guards against is an
// honest-but-wrong summary, not a lying one; against an honest mistake, sampling p% of steps catches p% of the
// mistakes, while these catch the shapes that are wrong in a way the harness can see without reading anything.
//
// Every one of these raises a concern and never returns a verdict. They are heuristics: a false positive costs the CEO
// one look at the files — which is what it did for every step before this gate existed — and a step is never rejected
// on one. That asymmetry is why they may be as blunt as they are.
internal static class AutopilotEvidenceSignals
{
    public static IReadOnlyList<string> For(AutopilotWorktreeChange change, AutopilotStep step, IReadOnlyList<string> summaries)
    {
        var concerns = new List<string>();

        if (change.IsEmpty && summaries.Any(summary => !string.IsNullOrWhiteSpace(summary)))
        {
            concerns.Add(
                "The step reported work, but the worktree shows no change at all since it started — check whether the "
                + "work landed somewhere else, or never landed.");
        }

        if (change.AddedFromBeforeTheMark.Count > 0)
        {
            concerns.Add(
                $"This step handed git {change.AddedFromBeforeTheMark.Count} file(s) that were already lying in the "
                + $"worktree before it started ({string.Join(", ", change.AddedFromBeforeTheMark.Take(5))}) — they read "
                + "as new in the diff below, but their contents are not this step's work.");
        }

        if (_ClaimsTestsPass(summaries) && !_TouchedATestFile(change))
        {
            concerns.Add(
                "The step reports its tests pass, but nothing it changed looks like a test file — check that the tests "
                + "it means actually cover this step's change.");
        }

        if (step.Reworks > 0)
        {
            concerns.Add(
                $"This step was already sent back {step.Reworks} time(s) — its acceptance has failed here before, so "
                + "read the change rather than the summary.");
        }

        // Deliberately not an "else" on the rework above: a rework always implies a second attempt, and it is exactly
        // there that this caveat matters most. The mark is retaken per attempt, so the change below is only the latest
        // attempt's slice while the acceptance spans the whole step — and the rework concern does not say that.
        // Attempts also grows on an attempt no CEO ever judged (a crashed or stalled session, see AutopilotCorrection),
        // which is the case where nothing else would fire at all.
        if (step.Attempts > 1)
        {
            concerns.Add(
                $"This is attempt {step.Attempts}: what follows is only what this attempt changed, while the acceptance "
                + "covers the whole step — earlier attempts' work sits behind this attempt's starting point.");
        }

        return concerns;
    }

    private static bool _ClaimsTestsPass(IReadOnlyList<string> summaries) =>
        summaries.Any(summary =>
            summary.Contains("test", StringComparison.OrdinalIgnoreCase)
            && (summary.Contains("pass", StringComparison.OrdinalIgnoreCase)
                || summary.Contains("green", StringComparison.OrdinalIgnoreCase)));

    private static bool _TouchedATestFile(AutopilotWorktreeChange change) =>
        change.FilesChanged.Concat(change.UntrackedFiles).Any(path => path.Contains("test", StringComparison.OrdinalIgnoreCase));
}
