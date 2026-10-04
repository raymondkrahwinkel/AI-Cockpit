using Cockpit.Core.Mcp;

namespace Cockpit.Core.Abstractions.Mcp;

/// <summary>
/// AC-1321: whether a paired controller is holding the line to this node right now — not "was ever paired" but
/// "has made an authorized call within the last minute". There is no heartbeat between the two cockpits: the
/// controller's own 20-second node poll (AC-796) and its assistant's reads (AC-1320) are the line, and this is
/// derived from the last of them. While <see cref="Current"/> is set the local assistant stands down.
/// </summary>
public interface INodeControllerPresence
{
    /// <summary>
    /// The controller that last reached this node within the window, or null once the window has passed with no
    /// call, or once its connect key expired or was revoked — at which point the node is on its own again.
    /// </summary>
    ActiveController? Current { get; }

    /// <summary>
    /// Raised when <see cref="Current"/> appears, when the window lets it go (a holder whose key ran out or was revoked
    /// reads as null before that), or when the line passes to another controller, from whatever thread noticed it.
    /// Not raised on every call that merely keeps the controller present.
    /// </summary>
    event EventHandler? Changed;
}
