namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// A plugin surface tied to one session that is already running (AC-832) — a diagram or whiteboard window opened
/// beside the cockpit, bound to the pane the operator is talking to. The binding is a peephole, not an owner: it
/// starts nothing and ends nothing, so disposing it leaves the session running and the session ending leaves the
/// surface standing. Obtained from <see cref="ICockpitHost.BindToSession"/>; members are read and events raised on
/// the UI thread, like the rest of the plugin session surface.
/// </summary>
public interface IPluginSessionBinding : IDisposable
{
    /// <summary>
    /// The pane this surface is bound to, for as long as the surface lives — the same
    /// <see cref="IPluginSessionContext.PaneId"/> the rest of the host surface names a session by. Stays readable
    /// after the session ends, so a surface can still say which session it belonged to.
    /// </summary>
    string PaneId { get; }

    /// <summary>
    /// The bound session's title as the cockpit shows it, or <see langword="null"/> when there is no session behind
    /// <see cref="PaneId"/> (any more) — what a surface draws to say which session it belongs to.
    /// </summary>
    string? SessionName { get; }

    /// <summary>
    /// Whether a session is still running behind <see cref="PaneId"/>. False for a pane id the host does not know —
    /// an unknown or already-ended session is this state, never an exception — so a surface has one way to draw
    /// "this session no longer exists" whether it was never there or has since gone.
    /// </summary>
    bool IsLive { get; }

    /// <summary>
    /// Raised once when the bound session ends while this binding is open — the cue to show that the surface is on
    /// its own now. Never raised for a binding that was already not <see cref="IsLive"/>.
    /// </summary>
    event EventHandler? Ended;

    /// <summary>
    /// Sends <paramref name="text"/> to the bound session as a submitted turn, the same way
    /// <see cref="ICockpitHost.SendToSessionAsync"/> does — what a surface uses to hand the conversation something
    /// it produced (a pin, a selection). A session that has ended takes nothing, and says so by doing nothing.
    /// </summary>
    Task SendAsync(string text);
}
