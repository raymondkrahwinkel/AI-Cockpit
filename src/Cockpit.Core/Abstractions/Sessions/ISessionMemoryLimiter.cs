namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// Caps a spawned session's whole process tree (AC-661), applied by pid right after the spawn so one call serves
/// both spawn routes. The point is the boundary: what dies over the cap is the session, never the cockpit.
/// </summary>
public interface ISessionMemoryLimiter
{
    /// <summary>
    /// Caps <paramref name="processId"/> and everything it spawns. Returns a handle to release when the session
    /// ends, or <see langword="null"/> when nothing could be enforced — never throws: an uncapped session still starts.
    /// </summary>
    IDisposable? Apply(int processId, long capBytes);
}
