namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// Where a plan came from (AC-174 entry-modes). A tracker item — YouTrack, GitHub Issues, or a later source — supplies
/// the tracker id, the item id, its title and its description so the CEO can draft a plan from what the issue actually
/// asks for. A CEO-first run has <em>no</em> source (this is null): Autopilot is started with only the CEO and the goal
/// and steps grow out of the planning conversation. Kept source-neutral so a new trigger (a project, AC-158) plugs in
/// without the plan model knowing which one.
/// </summary>
internal sealed record AutopilotPlanSource(string Tracker, string IssueId, string Title, string Description = "", string Url = "")
{
    /// <summary>The source a triggered run carries, or null when the run has no supplied item (a CEO-first plan). The
    /// issue's description and url ride along (from the intent's "description" and "url" keys) so the CEO drafts from the
    /// full item and a template's <c>{{issue.url}}</c> resolves to the real link instead of blank (AC-189).</summary>
    public static AutopilotPlanSource? FromRun(AutopilotRun? run) =>
        run is null || string.IsNullOrWhiteSpace(run.IssueId)
            ? null
            : new AutopilotPlanSource(
                run.Tracker,
                run.IssueId,
                run.Title,
                run.Data.GetValueOrDefault("description", string.Empty),
                run.Data.GetValueOrDefault("url", string.Empty));
}
