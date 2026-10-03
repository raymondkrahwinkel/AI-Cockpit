using Cockpit.Core.Sessions;

namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// The resumes sessions have scheduled (AC-290, AC-368), as the frontend schedules, cancels and shows them.
/// </summary>
public interface IScheduledResumes
{
    /// <summary>
    /// Resolves a pane id to its live session, or null once that pane is gone.
    /// </summary>
    Func<string, ISessionHandle?>? ResolveSession { get; set; }

    /// <summary>
    /// Reopens a pane that is gone or not yet started and sends the prompt into it, answering whether that landed.
    /// </summary>
    Func<string, string, Task<bool>>? ReopenAndSend { get; set; }

    /// <summary>
    /// Raised when the set of pending resumes changes.
    /// </summary>
    event EventHandler? PendingChanged;

    /// <summary>
    /// The resume waiting on <paramref name="paneId"/>, or null when that session has none.
    /// </summary>
    ScheduledResume? PendingFor(string paneId);

    /// <summary>
    /// Loads what was scheduled before, reports what lapsed meanwhile, and starts watching the clock.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Schedules <paramref name="resume"/>, replacing whatever that session had pending.
    /// </summary>
    Task ScheduleAsync(ScheduledResume resume, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels the resume waiting on <paramref name="paneId"/>.
    /// </summary>
    Task CancelAsync(string paneId, CancellationToken cancellationToken = default);
}
