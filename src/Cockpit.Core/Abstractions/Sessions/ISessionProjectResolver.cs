namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// Which project the session in a pane belongs to (AC-320). Lives here rather than with the sessions for the
/// same reason <see cref="ISessionResourceResolver"/> does: the answer needs the app's live sessions, while
/// callers — delegation among them — are Infrastructure's. The one place a pane's inherited project is looked up.
/// </summary>
public interface ISessionProjectResolver
{
    /// <summary>
    /// The project id of the session in <paramref name="paneId"/>, or <see langword="null"/> for no project, no
    /// live session, or a null pane — none an error. Asynchronous: reads on-screen sessions on the UI thread, so
    /// never await it from a thread blocking on the result (deadlock); a launch carries the project instead (AC-218).
    /// </summary>
    Task<string?> ProjectIdOfAsync(string? paneId, CancellationToken cancellationToken = default);
}
