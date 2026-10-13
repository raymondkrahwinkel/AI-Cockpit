namespace Cockpit.Core.Abstractions.Plugins;

/// <summary>
/// Restarts the server a remote plugin administrator talks to, so a plugin change there takes effect (AC-1525).
/// </summary>
public interface IServerRestart
{
    /// <summary>
    /// Asks the server to stop the way a SIGTERM stops it; its supervisor starts it again.
    /// </summary>
    Task RestartAsync(CancellationToken cancellationToken = default);
}
