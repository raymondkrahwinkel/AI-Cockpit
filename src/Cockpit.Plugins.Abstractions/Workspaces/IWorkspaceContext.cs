using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugins.Abstractions.Workspaces;

/// <summary>
/// Handed to a workspace's body factory (<see cref="WorkspaceTypeRegistration.CreateBody"/>): what one
/// full-surface workspace needs that its plugin cannot reach on its own. Per-workspace, so two workspaces of the
/// same type keep separate state, and so the body can host a live cockpit session and follow what the desk is
/// doing without the core knowing what the body is.
/// </summary>
/// <remarks>
/// Deliberately narrow, like <see cref="IWidgetContext"/>: it carries only the per-instance and host-internal
/// things a body cannot get otherwise — this workspace's storage, the observe surface, and the session-embedding
/// seam. Cross-plugin intents (<c>ICockpitHost.SendIntent</c>), dialogs and the theme are already the plugin's to
/// reach: the body factory is a closure created in <c>ICockpitPlugin.Initialize</c>, where the plugin captured the
/// <see cref="ICockpitHost"/>, and the theme is app resources any control binds with <c>DynamicResource</c>.
/// </remarks>
public interface IWorkspaceContext
{
    /// <summary>This workspace instance's stable id — the key its state is stored under, and distinct from the workspace <em>type</em> id.</summary>
    string WorkspaceId { get; }

    /// <summary>
    /// Per-workspace persistence for this body's own state (a pipeline's progress, a chosen target, scratch text).
    /// Scoped to <see cref="WorkspaceId"/> under the owning plugin's storage, so it survives a restart and never
    /// collides with another workspace of the same type.
    /// </summary>
    IPluginStorage Storage { get; }

    /// <summary>
    /// The same read/observe surface over the cockpit's sessions the host exposes (<see cref="ICockpitHost.Sessions"/>):
    /// the active session's working directory and its output stream, so a body can follow what a session is doing.
    /// </summary>
    ICockpitSessionObserver Sessions { get; }

    /// <summary>
    /// Starts a host-owned session and returns a control that embeds its live view, for the body to place wherever
    /// its layout wants it (Autopilot's own zelfsturende session sitting inside its pipeline surface). The host owns
    /// the session's lifecycle and ties it to this workspace — closing the workspace ends the session — and keeps it
    /// out of the session grid, so it shows only here. The plugin owns the place, never the lifetime.
    /// </summary>
    /// <param name="request">Which session to start (profile, working directory); see <see cref="EmbeddedSessionRequest"/>.</param>
    IEmbeddedSession EmbedSession(EmbeddedSessionRequest request);

    /// <summary>
    /// Raised when the host asks this workspace to refresh. A body that drives itself can ignore it; one that shows a
    /// snapshot should re-read and update.
    /// </summary>
    event EventHandler RefreshRequested;
}
