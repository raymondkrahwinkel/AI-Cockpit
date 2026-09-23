namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// Every live session pane the cockpit holds, grid and embedded alike, the assistant excluded (AC-1373).
/// Safe to read from any thread.
/// </summary>
public interface ISessionRegistry
{
    /// <summary>
    /// A snapshot of the live panes in the order they were registered.
    /// </summary>
    IReadOnlyList<ISessionHandle> All { get; }

    /// <summary>
    /// The pane with <paramref name="paneId"/>, or null when none is live.
    /// </summary>
    ISessionHandle? Find(string paneId);

    /// <summary>
    /// The assistant's own pane while it runs, or null (AC-1374). Tracked apart from <see cref="All"/> and
    /// <see cref="Find"/> on purpose: the assistant sits on no desk, and every existing reader of those two —
    /// <c>SessionWorkspaces</c>, <c>PaneWorkspaceDirectory</c> — must keep seeing exactly the panes it sees today.
    /// </summary>
    ISessionHandle? Assistant { get; }

    /// <summary>
    /// Raised after a pane is registered or removed, on the thread that changed the registry.
    /// </summary>
    event EventHandler? Changed;
}
