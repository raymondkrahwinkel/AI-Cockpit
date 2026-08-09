namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// Puts an OS-enforced memory ceiling around a spawned session's process tree (AC-661) — a Windows Job Object,
/// a Linux cgroup v2, nothing on macOS. Applied by process id right after the spawn, so the same call covers
/// both routes a session can start on (the pty host and a plugin driver's own child process); every process the
/// session spawns afterwards inherits the limit.
/// </summary>
/// <remarks>
/// The point is the boundary, not the number: the limit fires inside the job/cgroup only, so the cockpit is
/// structurally out of reach of it however far the child blows up. A session that hits its cap dying is the
/// accepted outcome.
/// </remarks>
public interface ISessionMemoryLimiter
{
    /// <summary>What this platform enforces, for the log and the operator-facing message; null when it enforces nothing.</summary>
    string? Mechanism { get; }

    /// <summary>
    /// Caps <paramref name="processId"/> and everything it spawns at <paramref name="capBytes"/>. Returns a handle
    /// to release when the session ends, or <see langword="null"/> when this platform (or this machine) cannot
    /// enforce one — never throws: a session that cannot be capped still starts, uncapped, and says so in the log.
    /// </summary>
    IDisposable? Apply(int processId, long capBytes);
}
