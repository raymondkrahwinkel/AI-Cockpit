namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// Where a plan came from (AC-174 entry-modes). A tracker item — YouTrack, GitHub Issues, or a later source — supplies
/// the tracker id, the item id and its title so the CEO can draft a plan from it. A CEO-first run has <em>no</em> source
/// (this is null): Autopilot is started with only the CEO and the goal and steps grow out of the planning conversation.
/// Kept source-neutral so a new trigger (a project, AC-158) plugs in without the plan model knowing which one.
/// </summary>
internal sealed record AutopilotPlanSource(string Tracker, string IssueId, string Title)
{
    /// <summary>The source a triggered run carries, or null when the run has no supplied item (a CEO-first plan).</summary>
    public static AutopilotPlanSource? FromRun(AutopilotRun? run) =>
        run is null || string.IsNullOrWhiteSpace(run.IssueId)
            ? null
            : new AutopilotPlanSource(run.Tracker, run.IssueId, run.Title);
}
