using Cockpit.Core.Profiles;

namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// The register of every live driver-backed session, and the one place their lifetime is owned. An
/// interactive panel and a delegated task (#67) both get their runtime from here and both stop it here, so
/// there is a single stop path and a single answer to "what is running right now".
/// </summary>
/// <remarks>
/// TTY sessions are deliberately absent: they are pty-backed and have no <see cref="ISessionDriver"/>, and
/// nothing that consumes this register (delegation targets, the headless surface) can drive one.
/// </remarks>
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
