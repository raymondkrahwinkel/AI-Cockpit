namespace Cockpit.Plugin.Autopilot;

// One settled run in the history: what it was called, its goal, how it ended (merge-ready or
// blocked) and why, when it finished, and each step's outcome — so a run that settled and left the surface is not lost
// but shown in the history section with what it did. Persisted through the plugin's storage, so history survives a
// restart. `FinishedAt` is an ISO-8601 string (formatted for display on render) rather than a DateTime, so
// the record round-trips through JSON without a timezone surprise. `RunId`/`Ticket`/
// `BlockadeAnswers`/`PullRequestMissing` are init-properties, not positional parameters, so
// persisted history from before AC-347 still deserializes — a missing field just reads back its default.
internal sealed record AutopilotRunRecord(
    string Name,
    string Goal,
    AutopilotPlanPhase Outcome,
    string? BlockReason,
    string FinishedAt,
    IReadOnlyList<AutopilotRunStepRecord> Steps)
{
    // The run's display label in history — its name, or the goal when it ran without one.
    public string Label => string.IsNullOrWhiteSpace(Name) ? Goal : Name;

    // The join-key into the host's usage-history trail (AC-251) — cost/tokens/duration live there, not here,
    // so this figure is never a second measurement drifting from that one.
    public string RunId { get; init; } = string.Empty;

    // The source issue key this run served, or empty for a CEO-first run with no supplied item.
    public string Ticket { get; init; } = string.Empty;

    // How many blockade questions the operator answered during this run (AC-347) — counted, and explicitly
    // *not* a correction: answering a question the run itself raised is not the same as the run needing rework.
    public int BlockadeAnswers { get; init; }

    // Whether this run reached merge-ready but could not deliver its pull request (AC-347) — no `gh`, no
    // remote, or the publish itself failed. Such a run still needs a human to open the PR by hand, so it is never
    // clean regardless of how its steps were classified; see `AutopilotRunReliability.RanClean`.
    public bool PullRequestMissing { get; init; }

    // The epic this run's sub was picked from (AC-346), or empty for a run clicked directly on its own item —
    // mirrors `AutopilotRun.EpicId`/`AutopilotPlanSource.EpicId`. Lets the epic-runner's
    // progress comment (`AutopilotPlanWorkspaceBody._PostEpicProgressAsync`) summarize reliability over just this
    // epic's own settled runs rather than the whole history, which would otherwise mix in every unrelated run ever
    // recorded.
    public string EpicId { get; init; } = string.Empty;

    // Captures a settled run's live state into its history record — the write path itself, extracted out of
    // `AutopilotPlanWorkspaceBody._RecordAndNotify` as a pure static factory so the mapping from
    // `AutopilotPlan`/`AutopilotStep` to persisted shape is unit-testable without a UI. A
    // static factory on the record it builds, rather than a helper on the workspace body, because every input here is
    // either plan state or a plain value the caller already snapshotted — nothing UI-shaped is needed to build one.
    // `finishedAt` is a parameter rather than read from `DateTimeOffset.Now` inside this
    // method, so the timestamp is deterministic in a test; the caller passes `DateTimeOffset.Now`.
    public static AutopilotRunRecord Capture(
        AutopilotPlan plan,
        AutopilotPlanPhase outcome,
        string? blockReason,
        string runId,
        int blockadeAnswers,
        bool pullRequestMissing,
        DateTimeOffset finishedAt) =>
        new(
            plan.Name,
            plan.Goal,
            outcome,
            blockReason,
            finishedAt.ToString("o"),
            [.. plan.Steps.Select(step => new AutopilotRunStepRecord(step.Title, step.Status, step.Note)
            {
                Attempts = step.Attempts,
                Reworks = step.Reworks,
                Correction = AutopilotCorrection.Classify(step.Status, step.Attempts, step.Reworks),
                CorrectionSource = AutopilotCorrectionSource.Automatic,
                ProfileLabel = step.ProfileLabel,
                Model = step.Model ?? string.Empty,
            })])
        {
            RunId = runId,
            Ticket = plan.Source?.IssueId ?? string.Empty,
            BlockadeAnswers = blockadeAnswers,
            PullRequestMissing = pullRequestMissing,
            EpicId = plan.Source?.EpicId ?? string.Empty,
        };
}
