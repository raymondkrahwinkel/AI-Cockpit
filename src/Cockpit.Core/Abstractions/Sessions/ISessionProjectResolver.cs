namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// Which project the session in a pane belongs to (AC-320). Lives here rather than where the sessions do for the
/// same reason <see cref="ISessionResourceResolver"/> does: the answer needs the live sessions, which are the app's,
/// while the callers — delegation among them — are Infrastructure's.
/// <para>
/// The one place that turns a pane into a project, so a session that inherits one from the session that started it
/// does not each time re-invent how that is looked up.
/// </para>
/// </summary>
public interface ISessionProjectResolver
{
    /// <summary>
    /// The project id of the session running in <paramref name="paneId"/>, or <see langword="null"/> when that pane
    /// has no project, names no live session, or is itself null — none of which is an error, only an ordinary
    /// session without a project.
    /// </summary>
    /// <remarks>
    /// Asynchronous because the lookup reads the on-screen sessions and therefore happens on the UI thread. Never
    /// call it from a path that resolves synchronously: awaiting the UI thread from a thread that is blocking on the
    /// result is a deadlock, which is why a launch carries the project as a value rather than looking it up (AC-218).
    /// </remarks>
    Task<string?> ProjectIdOfAsync(string? paneId, CancellationToken cancellationToken = default);
}
