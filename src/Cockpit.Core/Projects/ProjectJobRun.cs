namespace Cockpit.Core.Projects;

// AC-490: one session started from a project job, folded from its `ProjectJobRunEvent` lines. Only what the host
// saw: when it started, when the agent last reported, and what the agent said — never whether it finished.
public sealed record ProjectJobRun(
    string PaneId,
    string ProjectId,
    string JobId,
    DateTimeOffset StartedAt,
    DateTimeOffset? LastReportedAt,
    string? AgentSummary)
{
    // Folds `events` (any order) into runs, newest start first. A `Progress` line without a `Started` line is
    // dropped: a report against a run the host never saw begin belongs to nothing.

    // Each `Started` opens a run of its own, the way `RunTracker` keeps runs apart by an identity of the run and
    // not of the session it sits in. A pane keeps its id across a restore, so a second job started in it writes a
    // second `Started` — folded on the pane alone, the card shows the first start's date beside the last summary.
    public static IReadOnlyList<ProjectJobRun> Fold(IEnumerable<ProjectJobRunEvent> events)
    {
        var runs = new List<ProjectJobRun>();
        foreach (var byPane in events.GroupBy(entry => entry.PaneId, StringComparer.Ordinal))
        {
            ProjectJobRunEvent? started = null;
            ProjectJobRunEvent? latestReport = null;

            // A report stamped at the same moment as a start belongs to that start, not to the one before it.
            foreach (var entry in byPane
                .OrderBy(entry => entry.At)
                .ThenBy(entry => entry.Kind == ProjectJobRunEventKind.Started ? 0 : 1))
            {
                if (entry.Kind == ProjectJobRunEventKind.Started)
                {
                    _Close(runs, started, latestReport);
                    (started, latestReport) = (entry, null);
                }
                else if (started is not null)
                {
                    latestReport = entry;
                }
            }

            _Close(runs, started, latestReport);
        }

        return runs.OrderByDescending(run => run.StartedAt).ToList();
    }

    private static void _Close(List<ProjectJobRun> runs, ProjectJobRunEvent? started, ProjectJobRunEvent? latestReport)
    {
        if (started is not null)
        {
            runs.Add(new ProjectJobRun(
                started.PaneId,
                started.ProjectId,
                started.JobId,
                started.At,
                latestReport?.At,
                latestReport?.AgentSummary));
        }
    }
}
