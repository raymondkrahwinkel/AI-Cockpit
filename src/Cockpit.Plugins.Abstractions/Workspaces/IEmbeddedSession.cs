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
}
