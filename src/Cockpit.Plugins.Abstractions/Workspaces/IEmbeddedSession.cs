using Avalonia.Controls;

namespace Cockpit.Plugins.Abstractions.Workspaces;

/// <summary>
/// A live cockpit session embedded in a plugin workspace (<see cref="IWorkspaceContext.EmbedSession"/>): the
/// control to place in the body, and the pane id to act on it. The host owns the session — it built it, it ends
/// it when the workspace closes — so there is nothing here to dispose; the plugin holds the place, not the
/// lifetime.
/// </summary>
public interface IEmbeddedSession
{
    /// <summary>The session's live view, ready to drop into the body's layout. The host keeps it alive across re-layouts.</summary>
    Control View { get; }

    /// <summary>
    /// The embedded session's <c>IPluginSessionContext.PaneId</c> — the handle to act on this exact session
    /// (set its statusline, send it an intent, name it) through <see cref="ICockpitHost"/>.
    /// </summary>
    string PaneId { get; }

    /// <summary>
    /// Ends this one embedded session now — tears down its runtime and releases its worktree — without waiting for
    /// the workspace to close. What a body calls when it replaces one run's session with another's on the same
    /// surface, so the previous run's session and worktree are not left orphaned. Closing the workspace still ends
    /// any session left embedded; this is the finer-grained handle.
    /// </summary>
    Task CloseAsync();
}
