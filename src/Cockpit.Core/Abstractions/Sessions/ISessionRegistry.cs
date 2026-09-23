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
    /// Raised after a pane is registered or removed, on the thread that changed the registry.
    /// </summary>
    event EventHandler? Changed;
}
