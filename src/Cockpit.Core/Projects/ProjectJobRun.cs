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
    public static IReadOnlyList<ProjectJobRun> Fold(IEnumerable<ProjectJobRunEvent> events)
    {
        var runs = new List<ProjectJobRun>();
        foreach (var byPane in events.GroupBy(entry => entry.PaneId, StringComparer.Ordinal))
        {
            if (byPane.FirstOrDefault(entry => entry.Kind == ProjectJobRunEventKind.Started) is not { } started)
            {
                continue;
            }

            var latestReport = byPane
                .Where(entry => entry.Kind == ProjectJobRunEventKind.Progress)
                .MaxBy(entry => entry.At);
            runs.Add(new ProjectJobRun(
                started.PaneId,
                started.ProjectId,
                started.JobId,
                started.At,
                latestReport?.At,
                latestReport?.AgentSummary));
        }

        return runs.OrderByDescending(run => run.StartedAt).ToList();
    }
}
