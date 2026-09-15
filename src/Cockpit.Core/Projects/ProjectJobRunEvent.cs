namespace Cockpit.Core.Projects;

// AC-490: one line of `job-history.jsonl` — something the host saw happen to a session started from a project job.
// Several per run: `Started` opens one (a pane can hold more than one over its life, so its id does not), `Progress`
// each time the agent reports. Never an end: the host cannot observe one — a run is over when its pane is gone.
public sealed record ProjectJobRunEvent(
    string PaneId,
    string ProjectId,
    string JobId,
    DateTimeOffset At,
    ProjectJobRunEventKind Kind,
    string? AgentSummary = null);
