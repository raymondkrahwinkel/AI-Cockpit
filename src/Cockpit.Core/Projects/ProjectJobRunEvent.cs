namespace Cockpit.Core.Projects;

// AC-490: one line of `job-history.jsonl` — something the host saw happen to a session started from a project job.
// Several per run, all keyed on the pane the run is: `Started` once, `Progress` each time the agent reports. Never
// an end: the host cannot observe one, so it writes none — a run is over when its pane is gone.
public sealed record ProjectJobRunEvent(
    string PaneId,
    string ProjectId,
    string JobId,
    DateTimeOffset At,
    ProjectJobRunEventKind Kind,
    string? AgentSummary = null);

public enum ProjectJobRunEventKind
{
    Started,

    // The agent's own account of the work so far, in the operator's terms. A claim, never a measurement.
    Progress,
}
