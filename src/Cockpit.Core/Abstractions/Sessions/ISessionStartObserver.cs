namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// Told once a launched session started, so what a frontend keeps about projects follows it (AC-1439).
/// The desktop marks the project opened and records a job's run after that save (AC-490).
/// </summary>
public interface ISessionStartObserver
{
    /// <summary>
    /// The session <paramref name="paneId"/> started, for <paramref name="projectId"/> and its job, when it has them.
    /// </summary>
    void SessionStarted(string paneId, string? projectId, string? projectJobId);
}
