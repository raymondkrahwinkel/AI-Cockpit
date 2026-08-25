namespace Cockpit.Plugin.Autopilot;

// Targeted spot-checks the harness computes for itself (AC-255), chosen over sampling a fixed percentage of
// steps — these catch honest-but-wrong summaries without reading anything. Each raises a concern only, never
// a verdict: a false positive costs the CEO one look at the files, and no step is ever rejected on one.
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

        // Not an "else" on the rework above: a rework implies a second attempt, exactly where this caveat matters
        // most — the mark is retaken per attempt, so the change below is only the latest attempt's slice while
        // acceptance spans the whole step, which the rework concern above doesn't say.
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
