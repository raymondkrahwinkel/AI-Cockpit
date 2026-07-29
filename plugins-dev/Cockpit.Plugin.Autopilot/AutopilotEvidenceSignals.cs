namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// The targeted spot-checks the harness computes for itself (AC-255) — the deliberate alternative to validating a fixed
/// percentage of steps deeply. The failure this gate guards against is an honest-but-wrong summary, not a lying one;
/// against an honest mistake, sampling p% of steps catches p% of the mistakes, while these catch the shapes that are
/// wrong in a way the harness can see without reading anything.
/// <para>
/// Every one of these RAISES A CONCERN and never returns a verdict. They are heuristics: a false positive costs the CEO
/// one look at the files — which is what it did for every step before this gate existed — and a step is never rejected
/// on one. That asymmetry is why they may be as blunt as they are.
/// </para>
/// </summary>
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
