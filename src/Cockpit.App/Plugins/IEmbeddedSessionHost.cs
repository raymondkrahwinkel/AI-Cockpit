using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.App.Plugins;

/// <summary>
/// The host side of <see cref="IWorkspaceContext.EmbedSession"/>: starts a real cockpit session on behalf of a
/// plugin workspace and owns its lifetime. Implemented by the shell view model, which holds the session
/// factories and the running-session machinery; the workspace context reaches it through this narrow seam so the
/// plugin never touches the session lifecycle. Kept out of the plugin contract on purpose — a plugin embeds a
/// session, it does not manage one.
/// </summary>
internal interface IEmbeddedSessionHost
{
    /// <summary>
    /// Starts a session for the plugin workspace <paramref name="workspaceId"/> and returns its live view and pane
    /// id. The session is stamped with that workspace so it stays out of the session grid, and the host closes it
    /// when the workspace closes. The plugin places the view; it never disposes it.
    /// </summary>
    IEmbeddedSession Embed(string workspaceId, EmbeddedSessionRequest request);

    /// <summary>Closes and disposes every session embedded in <paramref name="workspaceId"/> — the workspace is going away.</summary>
    void CloseForWorkspace(string workspaceId);
}
