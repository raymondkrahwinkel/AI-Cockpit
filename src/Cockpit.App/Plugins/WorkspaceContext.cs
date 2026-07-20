using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.App.Plugins;

/// <summary>
/// The host's <see cref="IWorkspaceContext"/>: what one plugin workspace is handed, built per workspace so its
/// storage and its refresh signal are its own. Embedding a session is routed to the shell's
/// <see cref="IEmbeddedSessionHost"/>, which owns the session's lifetime; the body only places the view.
/// </summary>
internal sealed class WorkspaceContext(
    string workspaceId,
    IPluginStorage pluginStorage,
    ICockpitSessionObserver sessions,
    IEmbeddedSessionHost? embeddedSessions) : IWorkspaceContext
{
    public string WorkspaceId => workspaceId;

    public IPluginStorage Storage { get; } = new WorkspaceStorage(pluginStorage, workspaceId);

    public ICockpitSessionObserver Sessions => sessions;

    public event EventHandler? RefreshRequested;

    public IEmbeddedSession EmbedSession(EmbeddedSessionRequest request)
    {
        // Null only in the design-time/test graph, where there is no session machinery behind the shell. A real
        // host always supplies one, so a body that reaches for a session there is asking for something the host
        // cannot give — a clear throw beats a placeholder that silently shows nothing.
        if (embeddedSessions is null)
        {
            throw new InvalidOperationException("This host cannot embed sessions.");
        }

        return embeddedSessions.Embed(workspaceId, request);
    }

    /// <summary>
    /// Asks this workspace to refresh — raised by the host (a workspace-wide refresh). Host-side only: a body
    /// listens to <see cref="RefreshRequested"/>, it does not fire it.
    /// </summary>
    public void RequestRefresh() => RefreshRequested?.Invoke(this, EventArgs.Empty);
}
