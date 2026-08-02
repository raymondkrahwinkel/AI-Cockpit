namespace Cockpit.Plugin.Autopilot;

// Aggregates the history into the AC-347 reliability number — pure, no state of its own, so it is exercised without a
// storage or a controller. `Summarize` reads `records` newest-first, the order
// `AutopilotRunHistory.Items` already holds them in.
internal static class AutopilotRunReliability
{
    // How many of the most recent runs the summary considers, by default.
    internal const int Window = 20;

    // Whether a settled run counts as clean: it reached `AutopilotPlanPhase.MergeReady`, not one of its
    // steps needed a correction, and it actually delivered its pull request. A blocked or operator-stopped run is
    // never clean by definition — it did not reach merge-ready without intervention — and neither is a merge-ready run
    // that could not open its PR (`AutopilotRunRecord.PullRequestMissing`): it still needs a human to open
    // one by hand, which is exactly the intervention "clean" rules out.
    public static bool RanClean(AutopilotRunRecord record) =>
        record.Outcome == AutopilotPlanPhase.MergeReady
        && record.Steps.All(step => step.Correction == AutopilotCorrectionKind.None)
        && !record.PullRequestMissing;

    // The reliability summary over the newest `window` runs: the streak of clean runs counting back
    // from the newest until the first non-clean one, and how many of that same window were clean.
    public static AutopilotReliabilitySummary Summarize(IReadOnlyList<AutopilotRunRecord> records, int window = Window)
    {
        var considered = records.Take(window).ToList();

        var streak = 0;
        foreach (var record in considered)
        {
            if (!RanClean(record))
            {
                break;
            }

            streak++;
        }

        var clean = considered.Count(RanClean);
        return new AutopilotReliabilitySummary(streak, clean, considered.Count);
    }
}
