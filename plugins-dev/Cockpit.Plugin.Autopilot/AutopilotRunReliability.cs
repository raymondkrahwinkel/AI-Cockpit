namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// Aggregates the history into the AC-347 reliability number — pure, no state of its own, so it is exercised without a
/// storage or a controller. <see cref="Summarize"/> reads <paramref name="records"/> newest-first, the order
/// <see cref="AutopilotRunHistory.Items"/> already holds them in.
/// </summary>
internal static class AutopilotRunReliability
{
    /// <summary>How many of the most recent runs the summary considers, by default.</summary>
    internal const int Window = 20;

    /// <summary>
    /// Whether a settled run counts as clean: it reached <see cref="AutopilotPlanPhase.MergeReady"/> and not one of its
    /// steps needed a correction. A blocked or operator-stopped run is never clean by definition — it did not reach
    /// merge-ready without intervention.
    /// </summary>
    public static bool RanClean(AutopilotRunRecord record) =>
        record.Outcome == AutopilotPlanPhase.MergeReady && record.Steps.All(step => step.Correction == AutopilotCorrectionKind.None);

    /// <summary>
    /// The reliability summary over the newest <paramref name="window"/> runs: the streak of clean runs counting back
    /// from the newest until the first non-clean one, and how many of that same window were clean.
    /// </summary>
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
