namespace Cockpit.Plugin.Autopilot;

// Where a plan came from (AC-174 entry-modes). A tracker item supplies the tracker id, item id, title and
// description so the CEO can draft from what the issue asks for. A CEO-first run has *no* source (null): goal
// and steps grow out of the planning conversation. Kept source-neutral so a new trigger (AC-158) plugs in.
internal sealed record AutopilotPlanSource(string Tracker, string IssueId, string Title, string Description = "", string Url = "", string EpicId = "")
{
    // The source a triggered run carries, or null when the run has no supplied item (a CEO-first plan). The
    // description and url ride along so a template's `{{issue.url}}` resolves instead of blank (AC-189).
    // Carries `AutopilotRun.EpicId` through unchanged (AC-346) — empty for a run clicked on its own item.
    public static AutopilotPlanSource? FromRun(AutopilotRun? run) =>
        run is null || string.IsNullOrWhiteSpace(run.IssueId)
            ? null
            : new AutopilotPlanSource(
                run.Tracker,
                run.IssueId,
                run.Title,
                run.Data.GetValueOrDefault("description", string.Empty),
                run.Data.GetValueOrDefault("url", string.Empty),
                run.EpicId);
}
