namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// A plugin surface tied to one session that is already running (AC-832) — a diagram or whiteboard window bound to
/// the pane the operator is talking to. It is a peephole, not an owner: it starts nothing and ends nothing.
/// </summary>
/// <remarks>
/// Obtained from <see cref="ICockpitHost.BindToSession"/>. Members are read and events raised on the UI thread,
/// like the rest of the plugin session surface.
/// </remarks>
public interface IPluginSessionBinding : IDisposable
{
    /// <summary>
    /// The pane this surface is bound to — the same <see cref="IPluginSessionContext.PaneId"/> the rest of the host
    /// surface names a session by. Stays readable after that session ends.
    /// </summary>
    string PaneId { get; }

    /// <summary>
    /// The bound session's title as the cockpit shows it, or <see langword="null"/> when no session is behind
    /// <see cref="PaneId"/> any more.
    /// </summary>
    string? SessionName { get; }

    /// <summary>
    /// Whether a session is still running behind <see cref="PaneId"/>. False for an unknown or already-ended pane
    /// id, so "this session no longer exists" is one state to draw rather than an exception to survive.
    /// </summary>
    bool IsLive { get; }

    /// <summary>
    /// Raised once when the bound session ends while this binding is open. Never raised for a binding that was
    /// already not <see cref="IsLive"/>.
    /// </summary>
    event EventHandler? Ended;

    /// <summary>
    /// Sends <paramref name="text"/> to the bound session as a submitted turn, the way
    /// <see cref="ICockpitHost.SendToSessionAsync"/> does. A session that has ended takes nothing.
    /// </summary>
    Task SendAsync(string text);
}
