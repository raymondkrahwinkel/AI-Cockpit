using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.App.Plugins;

/// <summary>
/// The host side of <see cref="IWorkspaceContext.EmbedSession"/>: starts a real cockpit session for a plugin
/// workspace and owns its lifetime. Implemented by the shell view model (holds the session factories and the
/// running-session machinery); kept out of the plugin contract on purpose — a plugin embeds a session, not manages one.
/// </summary>
internal interface IEmbeddedSessionHost
{
    /// <summary>
    /// Starts a session for <paramref name="workspaceId"/> and returns its live view and pane id, stamped with that
    /// workspace so it stays out of the session grid; the host closes it when the workspace closes. The plugin
    /// places the view, never disposes it.
    /// </summary>
    IEmbeddedSession Embed(string workspaceId, EmbeddedSessionRequest request);

    /// <summary>
    /// Closes and disposes every session embedded in <paramref name="workspaceId"/> — the workspace is going away.
    /// </summary>
    void CloseForWorkspace(string workspaceId);
}
