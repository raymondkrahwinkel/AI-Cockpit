namespace Cockpit.Core.Projects;

/// <summary>
/// Keeps which project jobs were started and what their agents reported (AC-490), so "has this job run, and what
/// came of it" has an answer after the session is gone. Deliberately not a field on <c>IUsageHistory</c>: that trail
/// is cost per turn, this is units of work — folding them makes both half of something.
/// </summary>
public interface IProjectJobHistory
{
    /// <summary>
    /// Appends one event. Never throws: losing a line must not take the session it describes down with it.
    /// </summary>
    Task RecordAsync(ProjectJobRunEvent entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// The runs folded from the most recent <paramref name="eventLimit"/> lines, newest start first.
    /// </summary>
    Task<IReadOnlyList<ProjectJobRun>> ReadRecentRunsAsync(int eventLimit = 500, CancellationToken cancellationToken = default);
}
