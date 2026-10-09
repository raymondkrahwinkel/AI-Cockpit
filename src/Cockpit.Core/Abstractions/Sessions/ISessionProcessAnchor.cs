namespace Cockpit.Core.Abstractions.Sessions;

public interface ISessionProcessAnchor
{
    /// <summary>
    /// Binds the process tree rooted at <paramref name="processId"/> to this Cockpit, so it ends when the session or the
    /// Cockpit does. <paramref name="paneId"/> names the pane it serves, for when a previous run's tree has to be named.
    /// </summary>
    IDisposable? Anchor(int processId, string? paneId = null);
}
