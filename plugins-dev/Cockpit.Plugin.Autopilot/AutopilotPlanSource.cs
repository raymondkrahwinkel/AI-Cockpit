namespace Cockpit.Plugin.Autopilot;

// Where a plan came from (AC-174 entry-modes). A tracker item supplies the tracker id, item id, title and
// description so the CEO can draft from what the issue asks for. A CEO-first run has *no* source (null): goal
// and steps grow out of the planning conversation. Kept source-neutral so a new trigger (AC-158) plugs in.
internal sealed record AutopilotPlanSource(string Tracker, string IssueId, string Title, string Description = "", string Url = "", string EpicId = "", string Acceptance = "")
{
    // The source a triggered run carries, or null when the run has no supplied item (a CEO-first plan). Carries
    // `AutopilotRun.EpicId` through unchanged (AC-346), and `Acceptance` (AC-1339) — the sub's own
    // acceptance-criteria section, extracted by `AutopilotEpicRunner` — both empty for a plain issue click.
    public static AutopilotPlanSource? FromRun(AutopilotRun? run) =>
        run is null || string.IsNullOrWhiteSpace(run.IssueId)
            ? null
            : new AutopilotPlanSource(
                run.Tracker,
                run.IssueId,
                run.Title,
                run.Data.GetValueOrDefault("description", string.Empty),
                run.Data.GetValueOrDefault("url", string.Empty),
                run.EpicId,
                run.Data.GetValueOrDefault("acceptance", string.Empty));
}
