namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// One settled run in the history (Raymond 2026-07-22): what it was called, its goal, how it ended (merge-ready or
/// blocked) and why, when it finished, and each step's outcome — so a run that settled and left the surface is not lost
/// but shown in the history section with what it did. Persisted through the plugin's storage, so history survives a
/// restart. <see cref="FinishedAt"/> is an ISO-8601 string (formatted for display on render) rather than a DateTime, so
/// the record round-trips through JSON without a timezone surprise.
/// </summary>
internal sealed record AutopilotRunRecord(
    string Name,
    string Goal,
    AutopilotPlanPhase Outcome,
    string? BlockReason,
    string FinishedAt,
    IReadOnlyList<AutopilotRunStepRecord> Steps)
{
    /// <summary>The run's display label in history — its name, or the goal when it ran without one.</summary>
    public string Label => string.IsNullOrWhiteSpace(Name) ? Goal : Name;
}
