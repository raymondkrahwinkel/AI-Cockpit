namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// One settled run in the history: what it was called, its goal, how it ended (merge-ready or
/// blocked) and why, when it finished, and each step's outcome — so a run that settled and left the surface is not lost
/// but shown in the history section with what it did. Persisted through the plugin's storage, so history survives a
/// restart. <see cref="FinishedAt"/> is an ISO-8601 string (formatted for display on render) rather than a DateTime, so
/// the record round-trips through JSON without a timezone surprise. <see cref="RunId"/>/<see cref="Ticket"/>/
/// <see cref="BlockadeAnswers"/> are init-properties, not positional parameters, so persisted history from before
/// AC-347 still deserializes — a missing field just reads back its default.
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

    /// <summary>The join-key into the host's usage-history trail (AC-251) — cost/tokens/duration live there, not here,
    /// so this figure is never a second measurement drifting from that one.</summary>
    public string RunId { get; init; } = string.Empty;

    /// <summary>The source issue key this run served, or empty for a CEO-first run with no supplied item.</summary>
    public string Ticket { get; init; } = string.Empty;

    /// <summary>How many blockade questions the operator answered during this run (AC-347) — counted, and explicitly
    /// <em>not</em> a correction: answering a question the run itself raised is not the same as the run needing rework.</summary>
    public int BlockadeAnswers { get; init; }
}
