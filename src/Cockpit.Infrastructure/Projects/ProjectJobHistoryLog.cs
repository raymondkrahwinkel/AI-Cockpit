using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Projects;
using Cockpit.Infrastructure.Auditing;

namespace Cockpit.Infrastructure.Projects;

// Appends the job-run trail (AC-490) to `job-history.jsonl`, on the shared append-only machinery. No rollover, like
// consent and delegation: a run is one to three lines of ~300 bytes, so ten runs a day is about 1 MB a year —
// the usage trail rolls because it writes a line per turn, which this never does.
internal sealed class ProjectJobHistoryLog : JsonlAuditLog<ProjectJobRunEvent>, IProjectJobHistory, ISingletonService
{
    // The agent's summary is kept for reading next to the job, not as a full report; a paragraph fits, a transcript
    // does not.
    internal const int MaxSummaryLength = 500;

    public ProjectJobHistoryLog(ILogger<ProjectJobHistoryLog> logger)
        : base(AuditTrailFiles.InStateRoot(AuditTrailFiles.JobHistory), logger)
    {
    }

    // Test seam: point the trail at an arbitrary file.
    internal ProjectJobHistoryLog(string logFilePath, ILogger<ProjectJobHistoryLog> logger)
        : base(logFilePath, logger)
    {
    }

    protected override string LogName => "job-history";

    protected override ProjectJobRunEvent PrepareForWrite(ProjectJobRunEvent entry) =>
        entry.AgentSummary is { Length: > MaxSummaryLength } summary
            ? entry with { AgentSummary = TrimText(summary, MaxSummaryLength) }
            : entry;

    public async Task<IReadOnlyList<ProjectJobRun>> ReadRecentRunsAsync(int eventLimit = 500, CancellationToken cancellationToken = default) =>
        ProjectJobRun.Fold(await ReadRecentAsync(eventLimit, cancellationToken).ConfigureAwait(false));
}
