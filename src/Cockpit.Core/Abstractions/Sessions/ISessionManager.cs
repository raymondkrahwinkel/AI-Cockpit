using Cockpit.Core.Profiles;

namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// The register of every live driver-backed session, and the one place their lifetime is owned. An interactive
/// panel and a delegated task (#67) both get and stop their runtime here, one stop path, one answer to "what is
/// running now". TTY sessions are absent: they are pty-backed with no <see cref="ISessionDriver"/> to register.
/// </summary>
public interface ISessionManager
{
    /// <summary>Every session currently registered, in creation order.</summary>
    IReadOnlyList<ISessionRuntime> Sessions { get; }

    /// <summary>Raised whenever a session is added or removed, so a consumer can keep a live count.</summary>
    event Action? SessionsChanged;

    /// <summary>
    /// Creates a runtime for <paramref name="profile"/> and registers it. The runtime is not started yet —
    /// the caller starts it, so it can subscribe to the event stream before the first event arrives.
    /// </summary>
    ISessionRuntime Create(SessionProfile? profile);

    /// <summary>The registered session with this id, or <see langword="null"/> when it has already been stopped.</summary>
    ISessionRuntime? Find(string id);

    /// <summary>
    /// Stops the session and removes it from the register. Safe to call for an unknown or already-stopped id,
    /// so a close flow and a <c>stop_task</c> racing each other cannot throw.
    /// </summary>
    Task StopAsync(string id);
}
